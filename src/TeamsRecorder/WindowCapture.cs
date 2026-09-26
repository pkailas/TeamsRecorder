using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Mathematics;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace TeamsRecorder;

/// <summary>
/// Captures a Teams meeting window as a 1920×1080 H.264 MP4 (video only) using
/// Windows.Graphics.Capture + a Vortice D3D11 device, encoded by ffmpeg.
///
/// Design:
/// - A free-threaded <see cref="Direct3D11CaptureFramePool"/> is created at 1920×1080.
///   WGC does NOT scale into the pool size — it CLIPS content larger than the pool.
///   So on each frame we read <c>frame.ContentSize</c> (the window's real client size);
///   if it differs from the current pool size we call <c>pool.Recreate(device, …,
///   frame.ContentSize)</c> and recreate the staging texture to match. We then read
///   only <c>ContentSize.Width × ContentSize.Height</c> pixels from the mapped staging
///   texture and letterbox-resample (nearest-neighbour, in C#) into the fixed 1920×1080
///   BGRA canvas. The encoder therefore never sees a size change.
/// - Frames are sampled at <see cref="Settings.VideoFps"/> (default 5 fps) on a timer.
///   Intermediate <c>FrameArrived</c> frames are dropped (only the latest is kept).
///   If no new frame arrived (window minimized / static) the last canvas is re-sent
///   so the video stays in real-time sync with the audio.
/// - If the captured window closes / becomes invalid, a background thread polls every
///   2 s for a new meeting window and re-attaches to the same ffmpeg pipe.
/// - ffmpeg's stderr is redirected to <c>ffmpeg.log</c> in the session folder.
///   On <see cref="Stop"/>: stdin is closed, ffmpeg is waited on (≤ 15 s), exit code logged.
/// </summary>
public sealed class WindowCapture : IDisposable
{
    // --- Fixed canvas (the encoder never sees a size change) ---
    private const int CanvasW = 1920;
    private const int CanvasH = 1080;
    private const int CanvasBytes = CanvasW * CanvasH * 4; // BGRA

    // --- Re-acquire cadence ---
    private const int ReacquireIntervalMs = 2000;
    private const int FfmpegExitTimeoutMs = 15000;

    // --- Win32 ---
    private const int PER_MONITOR_DPI_AWARE = -4;

    private readonly object _lock = new();
    private readonly object _logLock = new();
    private readonly byte[] _canvas = new byte[CanvasBytes];

    // --- Configuration ---
    private string? _sessionDir;
    private string _outputMp4 = "";

    // --- D3D11 ---
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private ID3D11Texture2D? _staging;
    private (int w, int h) _stagingSize;

    // --- Capture ---
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private bool _itemClosed;
    private Direct3D11CaptureFrame? _latestFrame;
    private bool _frameDirty;
    private IntPtr _hwnd;
    private long _framesArrived;
    private SizeInt32 _poolSize;

    // --- ffmpeg ---
    private Process? _ffmpeg;
    private Stream? _ffmpegStdin;

    // --- Threads ---
    private System.Threading.Timer? _sampler;
    private Thread? _reacquireThread;
    private volatile bool _stopRequested;

    /// <summary>
    /// Free-form logging hook. The caller (TrayContext) routes this to
    /// <c>Debug.WriteLine</c> and appends to <c>&lt;session&gt;\video.log</c>.
    /// </summary>
    public event Action<string>? Log;

    // ------------------------------------------------------------------
    //  Start / Stop
    // ------------------------------------------------------------------

    /// <summary>
    /// Starts capturing <paramref name="hwnd"/> to <paramref name="outputMp4"/>.
    /// Throws if ffmpeg is missing or the D3D device cannot be created.
    /// </summary>
    public void Start(IntPtr hwnd, string outputMp4, Settings s)
    {
        lock (_lock)
        {
            if (_ffmpeg is not null)
                throw new InvalidOperationException("Already capturing.");

            _outputMp4 = outputMp4;
            _sessionDir = Path.GetDirectoryName(outputMp4);
            _stopRequested = false;
            _hwnd = hwnd;

            // Make the process per-monitor DPI-aware so window rects / capture coords line up.
            SetProcessDpiAwarenessContext(PER_MONITOR_DPI_AWARE);

            if (string.IsNullOrWhiteSpace(s.FfmpegExe) || !File.Exists(s.FfmpegExe))
                throw new FileNotFoundException($"ffmpeg not found: '{s.FfmpegExe}'.", s.FfmpegExe);

            if (!string.IsNullOrEmpty(_sessionDir))
                Directory.CreateDirectory(_sessionDir);

            // --- D3D11 device ---
            try
            {
                _device = D3D11.D3D11CreateDevice(
                    DriverType.Hardware,
                    DeviceCreationFlags.None,
                    [FeatureLevel.Level_11_1]);
                _context = _device.ImmediateContext;

                // WinRT IDirect3DDevice (needed by Direct3D11CaptureFramePool).
                // Vortice's generic CreateDirect3D11DeviceFromDXGIDevice<T> cannot CCW a
                // WinRT interface, so use the raw P/Invoke + CsWinRT FromAbi pattern.
                var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
                int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winrtDevPtr);
                if (hr != 0)
                    throw new InvalidOperationException(
                        $"CreateDirect3D11DeviceFromDXGIDevice failed: HResult=0x{hr:X8}");
                _winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(winrtDevPtr);
                Marshal.Release(winrtDevPtr);
            }
            catch (Exception ex)
            {
                var hr = Marshal.GetHRForException(ex);
                Log?.Invoke($"D3D11 device creation failed: {ex.GetType().Name}: {ex.Message} (HResult=0x{hr:X8})");
                throw;
            }

            // --- ffmpeg ---
            StartFfmpeg(s);

            // --- Attach to the window ---
            if (hwnd != IntPtr.Zero)
                AttachLocked(hwnd);

            // --- Sampler: VideoFps fps ---
            var fps = Math.Max(1, s.VideoFps);
            _sampler = new System.Threading.Timer(_ => SampleTick(), null, 0, 1000 / fps);

            // --- Re-acquire: poll for a meeting window if the item closes / is missing ---
            _reacquireThread = new Thread(ReacquireLoop)
            {
                IsBackground = true,
                Name = "TeamsRecorder.VideoReacquire",
            };
            _reacquireThread.Start();

            Log?.Invoke($"Video capture started → {outputMp4} ({fps} fps, {CanvasW}×{CanvasH})");
        }
    }

    /// <summary>Stops the capture and finalizes the MP4 (waits ≤ 15 s for ffmpeg to exit).</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_ffmpeg is null)
                return; // never started

            _stopRequested = true;
            Log?.Invoke("Video capture stopping…");

            // Stop the sampler first so no new frames are written.
            try { _sampler?.Dispose(); } catch { /* best effort */ }
            _sampler = null;

            // Stop the re-acquire thread.
            try { _reacquireThread?.Interrupt(); } catch { /* best effort */ }
            try { _reacquireThread?.Join(3000); } catch { /* best effort */ }
            _reacquireThread = null;

            // Detach the capture (releases the pool → FrameArrived stops firing).
            DetachLocked();

            // Close stdin → ffmpeg finalizes the MP4 (faststart moov).
            try { _ffmpegStdin?.Flush(); } catch { /* best effort */ }
            try { _ffmpegStdin?.Close(); } catch { /* best effort */ }
            _ffmpegStdin = null;

            int exitCode = -1;
            try
            {
                if (_ffmpeg.WaitForExit(FfmpegExitTimeoutMs))
                    exitCode = _ffmpeg.ExitCode;
                else
                {
                    Log?.Invoke("ffmpeg did not exit within 15 s; killing.");
                    try { _ffmpeg.Kill(); } catch { /* best effort */ }
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"ffmpeg wait error: {ex.Message}");
            }
            Log?.Invoke($"ffmpeg exited with code {exitCode}.");
            Log?.Invoke($"frames arrived: {Interlocked.Read(ref _framesArrived)}");

            _ffmpeg?.Dispose();
            _ffmpeg = null;

            // --- D3D cleanup ---
            _staging?.Dispose(); _staging = null;
            _stagingSize = (0, 0);
            _winrtDevice = null;
            _context?.Dispose(); _context = null;
            _device?.Dispose(); _device = null;

            Log?.Invoke($"Video capture stopped → {_outputMp4}");
        }
    }

    public void Dispose()
    {
        try { Stop(); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    //  ffmpeg
    // ------------------------------------------------------------------

    private void StartFfmpeg(Settings s)
    {
        var fps = Math.Max(1, s.VideoFps);
        var sessionDir = _sessionDir!;
        var ffmpegLog = Path.Combine(sessionDir, "ffmpeg.log");

        // -nostats: kill the progress printing that assumes a console. The pixel data
        // is piped into stdin, so we cannot use -nostdin; instead CreateNoWindow gives
        // ffmpeg no console at all, so its interactive key reader is never started
        // (it was treating the 0x71 'q' byte in BGRA frames as a quit command).
        var args =
            $"-hide_banner -loglevel error -nostats " +
            $"-f rawvideo -pix_fmt bgra -s {CanvasW}x{CanvasH} -r {fps} -i - " +
            $"-c:v h264_nvenc -preset p4 -rc vbr -cq 28 -g {fps * 5} -pix_fmt yuv420p " +
            $"-movflags +faststart -y \"{_outputMp4}\"";

        var psi = new ProcessStartInfo
        {
            FileName = s.FfmpegExe,
            Arguments = args,
            WorkingDirectory = sessionDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true, // drain so nothing blocks on a missing console handle
            CreateNoWindow = true,
        };

        var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                Log?.Invoke(e.Data);
        };
        proc.OutputDataReceived += (_, e) => { /* drain stdout */ };

        try
        {
            if (!proc.Start())
                throw new InvalidOperationException("Failed to start ffmpeg.");
        }
        catch (Exception ex)
        {
            var hr = Marshal.GetHRForException(ex);
            Log?.Invoke($"ffmpeg start failed: {ex.GetType().Name}: {ex.Message} (HResult=0x{hr:X8})");
            throw;
        }

        _ffmpeg = proc;
        _ffmpegStdin = proc.StandardInput.BaseStream;
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();

        Log?.Invoke($"ffmpeg started (pid {proc.Id}); stderr → {ffmpegLog}");
    }

    // ------------------------------------------------------------------
    //  Capture attach / detach / re-acquire
    // ------------------------------------------------------------------

    /// <summary>Creates a capture item + frame pool for <paramref name="hwnd"/> (caller holds _lock).</summary>
    private void AttachLocked(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _itemClosed = false;

        var item = CreateCaptureItem(hwnd);
        if (item is null)
        {
            Log?.Invoke($"Failed to create GraphicsCaptureItem for hwnd 0x{hwnd:X} (see log above for HResult).");
            return;
        }
        _item = item;

        // The Closed event fires when the window is destroyed / the item is closed.
        item.Closed += (_, _) =>
        {
            Log?.Invoke("Captured window closed (GraphicsCaptureItem.Closed).");
            lock (_lock) { _itemClosed = true; }
        };

        // Free-threaded frame pool at the window's client size (physical pixels;
        // the process is per-monitor DPI-aware). WGC clips content larger than the
        // pool; BlitFrameToCanvas calls pool.Recreate (after the blit) if the
        // content is larger than the pool, so a slight initial mismatch is harmless.
        GetClientRect(hwnd, out var clientRect);
        var poolSize = new SizeInt32(
            Math.Max(1, clientRect.Right - clientRect.Left),
            Math.Max(1, clientRect.Bottom - clientRect.Top));
        _poolSize = poolSize;

        Direct3D11CaptureFramePool pool;
        try
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice!,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                poolSize);
        }
        catch (Exception ex)
        {
            var hr = Marshal.GetHRForException(ex);
            Log?.Invoke($"Frame pool creation failed: {ex.GetType().Name}: {ex.Message} (HResult=0x{hr:X8})");
            return;
        }
        _pool = pool;

        pool.FrameArrived += OnFrameArrived;

        // Create + start the capture session (without this, FrameArrived never fires).
        try
        {
            var session = pool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = false; } catch { /* best effort */ }
            try { session.IsBorderRequired = false; } catch { /* may throw without GraphicsCaptureAccess */ }
            session.StartCapture();
            _session = session;
        }
        catch (Exception ex)
        {
            var hr = Marshal.GetHRForException(ex);
            Log?.Invoke($"Capture session start failed: {ex.GetType().Name}: {ex.Message} (HResult=0x{hr:X8})");
            return;
        }

        Log?.Invoke($"Attached to window 0x{hwnd:X} ({GetWindowTitle(hwnd)}). Capture session started.");
    }

    /// <summary>Releases the current capture session, pool + item (caller holds _lock).</summary>
    private void DetachLocked()
    {
        // Stop the session first so no more frames arrive.
        try { _session?.Dispose(); } catch { /* best effort */ }
        _session = null;

        // Release the pool so FrameArrived stops firing.
        try { _pool?.Dispose(); } catch { /* best effort */ }
        _pool = null;

        // Release the item (GC handles the COM ref).
        _item = null;

        // Drop any pending frame.
        try { _latestFrame?.Dispose(); } catch { /* best effort */ }
        _latestFrame = null;
        _frameDirty = false;
        _itemClosed = false;
    }

    private void ReacquireLoop()
    {
        try
        {
            while (!_stopRequested)
            {
                Thread.Sleep(ReacquireIntervalMs);
                if (_stopRequested)
                    break;

                bool needAttach;
                lock (_lock)
                {
                    needAttach = _item is null || _itemClosed
                        || _hwnd == IntPtr.Zero
                        || !IsWindow(_hwnd)
                        || !IsWindowVisible(_hwnd);
                }
                if (!needAttach)
                    continue;

                var (hwnd, title, _) = FindTeamsMeetingWindowCore();
                if (hwnd == IntPtr.Zero)
                    continue;

                lock (_lock)
                {
                    if (_stopRequested)
                        break;
                    DetachLocked();
                    AttachLocked(hwnd);
                }
                Log?.Invoke($"Re-attached to meeting window 0x{hwnd:X} ({title}).");
            }
        }
        catch (ThreadInterruptedException)
        {
            // Stop() interrupted us — expected.
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Re-acquire thread error: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    //  Frame sampling (VideoFps fps)
    // ------------------------------------------------------------------

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object? args)
    {
        Direct3D11CaptureFrame? frame;
        try
        {
            frame = sender.TryGetNextFrame();
        }
        catch
        {
            return;
        }
        if (frame is null)
            return;

        Interlocked.Increment(ref _framesArrived);

        lock (_lock)
        {
            try { _latestFrame?.Dispose(); } catch { /* best effort */ }
            _latestFrame = frame;
            _frameDirty = true;
        }
    }

    private void SampleTick()
    {
        if (_stopRequested)
            return;

        try
        {
            Direct3D11CaptureFrame? frame = null;
            lock (_lock)
            {
                if (_frameDirty)
                {
                    frame = _latestFrame;
                    _latestFrame = null;
                    _frameDirty = false;
                }
                // If no new frame arrived, frame stays null and we re-send the last
                // canvas below so the video stays in real-time sync with the audio.
            }

            if (frame is not null)
            {
                BlitFrameToCanvas(frame);
                try { frame.Dispose(); } catch { /* best effort */ }
            }

            // Write the (fixed-size) canvas to ffmpeg's stdin.
            // If no new frame arrived, this re-sends the last canvas so the video
            // stays in real-time sync with the audio.
            var stdin = _ffmpegStdin;
            if (stdin is null)
                return;

            try
            {
                stdin.Write(_canvas, 0, _canvas.Length);
            }
            catch (IOException)
            {
                // ffmpeg already exited — stop writing.
                Log?.Invoke("ffmpeg stdin closed; stopping video writes.");
                _stopRequested = true;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Video sample error: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the frame onto a staging texture (recreating it if ContentSize changed),
    /// maps it, reads the BGRA pixels into a temp buffer, and letterbox-resamples
    /// into the 1080p canvas.
    /// </summary>
    private void BlitFrameToCanvas(Direct3D11CaptureFrame frame)
    {
        if (_device is null || _context is null)
            return;

        var surface = frame.Surface;
        if (surface is null)
            return;

        // Get the ID3D11Texture2D pointer behind the captured surface.
        // Marshal.GetIUnknownForObject gives the .NET CCW's IUnknown (QI fails with
        // E_NOINTERFACE). Use CsWinRT's MarshalInspectable to reach the NATIVE WinRT
        // object, then do a real QueryInterface for IDirect3DDxgiInterfaceAccess.
        ID3D11Texture2D? tex = null;
        IntPtr accessPtr = IntPtr.Zero;
        try
        {
            var surfaceInspectable = WinRT.MarshalInspectable<object>.FromManaged(surface);
            Guid accessIid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            int qhr = Marshal.QueryInterface(surfaceInspectable, in accessIid, out accessPtr);
            Marshal.Release(surfaceInspectable);
            if (qhr != 0 || accessPtr == IntPtr.Zero)
            {
                Log?.Invoke($"QI for IDirect3DDxgiInterfaceAccess failed: HResult=0x{qhr:X8}");
                return;
            }

            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
            Guid texIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C"); // IID_ID3D11Texture2D
            int ghr = access.GetInterface(ref texIid, out var texPtr);
            if (ghr != 0 || texPtr == IntPtr.Zero)
            {
                Log?.Invoke($"IDirect3DDxgiInterfaceAccess.GetInterface(ID3D11Texture2D) failed: HResult=0x{ghr:X8}");
                return;
            }
            tex = new ID3D11Texture2D(texPtr);

            // The pool surface is pool-sized; the window content occupies its
            // top-left ContentSize region. Copy that sub-region so the FIRST frame
            // is usable regardless of pool size (a full CopyResource with mismatched
            // dimensions is a silent D3D11 no-op).
            var size = frame.ContentSize;
            var sw = Math.Min(size.Width, (int)tex.Description.Width);
            var sh = Math.Min(size.Height, (int)tex.Description.Height);
            if (sw <= 0 || sh <= 0)
                return;

            // Recreate the staging texture if the content size changed.
            if (_staging is null || _stagingSize != (sw, sh))
            {
                _staging?.Dispose();
                _stagingSize = (sw, sh);
                _staging = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)sw,
                    Height = (uint)sh,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                });
            }

            // GPU copy: content sub-region of the frame texture → staging texture.
            _context.CopySubresourceRegion(
                _staging, 0,
                0, 0, 0,
                tex, 0,
                new Box(0, 0, 0, sw, sh, 1));

            // Read the staging texture back to CPU into a temp buffer.
            var rowBytes = sw * 4;
            var frameBuf = new byte[rowBytes * sh];
            var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    var src = (byte*)mapped.DataPointer;
                    fixed (byte* dst = frameBuf)
                    {
                        for (var y = 0; y < sh; y++)
                        {
                            var srcRow = (void*)(src + (long)y * mapped.RowPitch);
                            Unsafe.CopyBlockUnaligned(dst + (long)y * rowBytes, srcRow, (uint)rowBytes);
                        }
                    }
                }
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            // Letterbox-resample the frame into the fixed 1080p canvas.
            ResampleToCanvas(frameBuf, sw, sh, rowBytes);

            // If the window content is larger than the pool in either dimension,
            // recreate the pool at the new size so nothing is clipped. Done AFTER
            // the blit so the current frame is not lost (static windows may never
            // send a new frame after a recreate).
            if (size.Width > _poolSize.Width || size.Height > _poolSize.Height)
            {
                try
                {
                    _pool?.Recreate(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                    _poolSize = size;
                    Log?.Invoke($"Pool recreated at {size.Width}×{size.Height}.");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Pool.Recreate failed: {ex.Message}");
                }
            }
        }
        finally
        {
            tex?.Dispose();
            if (accessPtr != IntPtr.Zero)
                Marshal.Release(accessPtr);
        }
    }

    /// <summary>
    /// Letterbox-resamples the BGRA frame (sw×sh, rowPitch bytes per row) in
    /// <paramref name="src"/> into the fixed 1920×1080 <see cref="_canvas"/>.
    /// Nearest-neighbour; black bars where the aspect ratio differs.
    /// </summary>
    private void ResampleToCanvas(byte[] src, int sw, int sh, int srcRowPitch)
    {
        // Clear canvas to black.
        Array.Fill(_canvas, (byte)0);

        if (sw <= 0 || sh <= 0)
            return;

        double scale = Math.Min((double)CanvasW / sw, (double)CanvasH / sh);
        int dw = Math.Max(1, (int)(sw * scale));
        int dh = Math.Max(1, (int)(sh * scale));
        int dx = (CanvasW - dw) / 2;
        int dy = (CanvasH - dh) / 2;

        unsafe
        {
            fixed (byte* srcPtr = src)
            {
                for (var y = 0; y < dh; y++)
                {
                    var sy = (int)((double)y * sh / dh);
                    var srcRow = srcPtr + (long)sy * srcRowPitch;
                    var dstRow = _canvas.AsSpan((dy + y) * CanvasW * 4 + dx * 4, dw * 4);

                    for (var x = 0; x < dw; x++)
                    {
                        var sx = (int)((double)x * sw / dw);
                        var srcPixel = (byte*)(srcRow + sx * 4);
                        var off = x * 4;
                        dstRow[off] = srcPixel[0]; // B
                        dstRow[off + 1] = srcPixel[1]; // G
                        dstRow[off + 2] = srcPixel[2]; // R
                        dstRow[off + 3] = srcPixel[3]; // A
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    //  Logging
    // ------------------------------------------------------------------

    /// <summary>
    /// Appends a line to <c>&lt;session&gt;\video.log</c> (and <c>recorder.log</c>
    /// for candidate-window logging). Never throws.
    /// </summary>
    internal void AppendLog(string line, string? extraFile = null)
    {
        var stamp = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}";
        try
        {
            lock (_logLock)
            {
                if (_sessionDir is not null)
                {
                    File.AppendAllText(Path.Combine(_sessionDir, "video.log"), stamp + Environment.NewLine);
                    if (extraFile is not null)
                        File.AppendAllText(Path.Combine(_sessionDir, extraFile), stamp + Environment.NewLine);
                }
            }
        }
        catch
        {
            // Never let logging take down the capture.
        }
        Debug.WriteLine(stamp);
    }

    // ------------------------------------------------------------------
    //  Window discovery
    // ------------------------------------------------------------------

    /// <summary>
    /// Finds the Teams meeting window to capture. Returns (hwnd, title);
    /// hwnd is <see cref="IntPtr.Zero"/> if no ms-teams window is found.
    ///
    /// Selection rule (ordered, easy to adjust):
    ///   1. A visible ms-teams top-level window whose title contains " | Microsoft Teams"
    ///      and does NOT start with "Chat |", "Activity |", or "Calendar |".
    ///   2. Fallback: the largest visible ms-teams window.
    /// </summary>
    public static IntPtr FindTeamsMeetingWindow(out string title)
    {
        var (hwnd, t, _) = FindTeamsMeetingWindowCore();
        title = t;
        return hwnd;
    }

    /// <summary>Returns (hwnd, title, allCandidates) for the best meeting window.</summary>
    public static (IntPtr hwnd, string title, List<(IntPtr Hwnd, string Title, int W, int H)> candidates)
        FindTeamsMeetingWindowCore()
    {
        var pidSet = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("ms-teams"))
        {
            using (p)
            {
                pidSet.Add(p.Id);
            }
        }
        if (pidSet.Count == 0)
            return (IntPtr.Zero, "", new List<(IntPtr, string, int, int)>());

        var candidates = new List<(IntPtr Hwnd, string Title, int W, int H)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pidSet.Contains((int)pid))
                return true;

            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            GetWindowRect(hwnd, out var rect);
            var w = rect.Right - rect.Left;
            var h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0)
                return true;

            candidates.Add((hwnd, title, w, h));
            return true;
        }, IntPtr.Zero);

        if (candidates.Count == 0)
            return (IntPtr.Zero, "", candidates);

        // Rule 1: meeting pop-out title.
        var prefixes = new[] { "Chat |", "Activity |", "Calendar |" };
        foreach (var c in candidates)
        {
            if (c.Title.Contains(" | Microsoft Teams", StringComparison.Ordinal)
                && !prefixes.Any(p => c.Title.StartsWith(p, StringComparison.Ordinal)))
            {
                return (c.Hwnd, c.Title, candidates);
            }
        }

        // Rule 2: largest visible window.
        var largest = candidates.OrderByDescending(c => (long)c.W * c.H).First();
        return (largest.Hwnd, largest.Title, candidates);
    }

    // ------------------------------------------------------------------
    //  GraphicsCaptureItem from an HWND (WinRT interop)
    // ------------------------------------------------------------------

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(ref Guid iid, out IntPtr ppv);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern void WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    [return: MarshalAs(UnmanagedType.I4)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    private GraphicsCaptureItem? CreateCaptureItem(IntPtr hwnd)
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        try
        {
            // Create the HSTRING for the class name (CharSet.Unicode is critical).
            int hr = WindowsCreateString(className, (uint)className.Length, out var hstr);
            if (hr != 0)
            {
                Log?.Invoke($"WindowsCreateString failed: HResult=0x{hr:X8}");
                return null;
            }
            if (hstr == IntPtr.Zero)
            {
                Log?.Invoke("WindowsCreateString returned null HSTRING.");
                return null;
            }

            // Get the activation factory for IGraphicsCaptureItemInterop.
            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(hstr, ref interopIid, out var factoryPtr);
            WindowsDeleteString(hstr);
            if (hr != 0)
            {
                Log?.Invoke($"RoGetActivationFactory failed: HResult=0x{hr:X8} ({className})");
                return null;
            }

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);

            // Create the capture item for the window.
            var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            var itemPtr = interop.CreateForWindow(hwnd, ref itemIid);
            if (itemPtr == IntPtr.Zero)
            {
                Log?.Invoke($"CreateForWindow returned null for hwnd 0x{hwnd:X}.");
                return null;
            }

            var item = GraphicsCaptureItem.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
            return item;
        }
        catch (Exception ex)
        {
            var hr = Marshal.GetHRForException(ex);
            Log?.Invoke($"CreateCaptureItem exception: {ex.GetType().Name}: {ex.Message} (HResult=0x{hr:X8})");
            return null;
        }
    }

    // ------------------------------------------------------------------
    //  Win32 interop
    // ------------------------------------------------------------------

    [DllImport("d3d11.dll")]
    [return: MarshalAs(UnmanagedType.I4)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(int value);

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0)
            return "";
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
