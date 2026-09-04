using System.ComponentModel;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TeamsRecorder;

/// <summary>
/// Records microphone (capture) and system loopback (render) audio as two WAV
/// files, each converted to 16 kHz / 16-bit / mono for the Python sidecar.
///
/// Usage:
///   var r = new Recorder();
///   r.Start(settings);
///   (mic, loopback) = r.Stop();
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly Lock _gate = new();

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private WasapiOut? _silencePlayer;
    private WaveFileWriter? _micWriter;
    private WaveFileWriter? _loopbackWriter;
    private IWaveProvider? _micProvider;
    private IWaveProvider? _loopbackProvider;
    private Thread? _micWriterThread;
    private Thread? _loopbackWriterThread;
    private volatile bool _stopRequested;
    private bool _recording;
    private string? _sessionFolder;

    /// <summary>Target sample rate for both output files (sidecar requirement).</summary>
    private const int TargetSampleRate = 16000;

    public bool IsRecording { get; private set; }

    /// <summary>Folder that receives mic.wav / loopback.wav for the active session.</summary>
    public string? SessionFolder => _sessionFolder;

    /// <summary>Free-form logging hook (e.g. wired to the tray context).</summary>
    public event Action<string>? Log;

    public void Start(Settings s)
    {
        lock (_gate)
        {
            if (_recording)
                throw new InvalidOperationException("Already recording.");

            try
            {
                Directory.CreateDirectory(s.OutputFolder);
                _sessionFolder = Path.Combine(s.OutputFolder, DateTime.Now.ToString("yyyy-MM-dd_HHmm"));
                Directory.CreateDirectory(_sessionFolder);
                var micWav = Path.Combine(_sessionFolder, "mic.wav");
                var loopbackWav = Path.Combine(_sessionFolder, "loopback.wav");

                var enumerator = new MMDeviceEnumerator();

                // --- Mic (capture) ---
                var micDevice = s.MicDeviceName is null
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                    : FindDevice(enumerator, DataFlow.Capture, s.MicDeviceName);
                _micCapture = new WasapiCapture(micDevice);
                AttachPipeline(_micCapture, micWav, out _micProvider, out _);

                // --- Loopback (render) ---
                var loopbackDevice = s.LoopbackDeviceName is null
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : FindDevice(enumerator, DataFlow.Render, s.LoopbackDeviceName);
                _loopbackCapture = new WasapiLoopbackCapture(loopbackDevice);
                AttachPipeline(_loopbackCapture, loopbackWav, out _loopbackProvider, out _);

                // GOTCHA: WasapiLoopbackCapture raises NO DataAvailable events while
                // nothing is being played, so the loopback file would stop growing and
                // drift out of sync with the mic file. Fix: keep a WasapiOut playing
                // SilenceProvider on the SAME render device until Stop() -- this
                // guarantees a continuous stream of loopback frames.
                _silencePlayer = new WasapiOut(loopbackDevice, AudioClientShareMode.Shared, true, 200);
                _silencePlayer.Init(new SilenceProvider(new WaveFormat(44100, 2)));
                _silencePlayer.Play();

                _stopRequested = false;
                var micProvider = _micProvider;
                var loopbackProvider = _loopbackProvider;
                _micWriterThread = new Thread(() => WriteLoop(micProvider)) { IsBackground = true, Name = "TeamsRecorder.MicWriter" };
                _loopbackWriterThread = new Thread(() => WriteLoop(loopbackProvider)) { IsBackground = true, Name = "TeamsRecorder.LoopbackWriter" };

                _micCapture.StartRecording();
                _loopbackCapture.StartRecording();

                _micWriterThread.Start();
                _loopbackWriterThread.Start();

                _recording = true;
                IsRecording = true;
                Log?.Invoke($"Recording started: {_sessionFolder}");
            }
            catch
            {
                // Clean up any partially-created state and rethrow so the caller
                // (tray) can show a message box with the exception text.
                try { CleanupLocked(); } catch { /* best effort */ }
                throw;
            }
        }
    }

    public (string micWav, string loopbackWav) Stop()
    {
        lock (_gate)
        {
            if (!_recording)
                throw new InvalidOperationException("Not recording.");

            _stopRequested = true;

            // Stop the silence player first so no new loopback frames arrive.
            try { _silencePlayer?.Stop(); } catch { }
            try { _micCapture?.StopRecording(); } catch { }
            try { _loopbackCapture?.StopRecording(); } catch { }

            // Wait for writer threads to drain and close the files. They never
            // marshal to the UI thread, so this is safe to call from the UI.
            _micWriterThread?.Join(TimeSpan.FromSeconds(10));
            _loopbackWriterThread?.Join(TimeSpan.FromSeconds(10));

            var folder = _sessionFolder!;
            var micWav = Path.Combine(folder, "mic.wav");
            var loopbackWav = Path.Combine(folder, "loopback.wav");

            CleanupLocked();
            Log?.Invoke($"Recording stopped: {folder}");
            return (micWav, loopbackWav);
        }
    }

    private void CleanupLocked()
    {
        _silencePlayer?.Dispose();
        _micCapture?.Dispose();
        _loopbackCapture?.Dispose();
        _micWriter?.Dispose();
        _loopbackWriter?.Dispose();
        _micProvider = null;
        _loopbackProvider = null;
        _micCapture = null;
        _loopbackCapture = null;
        _silencePlayer = null;
        _micWriter = null;
        _loopbackWriter = null;
        _micWriterThread = null;
        _loopbackWriterThread = null;
        _recording = false;
        IsRecording = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_recording)
            {
                try { Stop(); } catch { /* best effort on dispose */ }
            }
            CleanupLocked();
        }
    }

    /// <summary>
    /// Wires a capture's DataAvailable into a buffered sample pipeline that ends
    /// in a 16 kHz / 16-bit / mono IWaveProvider, and opens the WaveFileWriter.
    /// </summary>
    private void AttachPipeline(WasapiCapture capture, string path, out IWaveProvider pcm16, out BufferedWaveProvider buffered)
    {
        var format = capture.WaveFormat;

        buffered = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(30),
        };
        var bufferedRef = buffered; // lambda cannot capture an out parameter
        capture.DataAvailable += (_, e) => bufferedRef.AddSamples(e.Buffer, 0, e.BytesRecorded);

        ISampleProvider mono;
        if (format.Channels > 2)
        {
            // More than stereo (e.g. 5.1/7.1 capture mix): take channel 0 via
            // MultiplexingSampleProvider instead of averaging everything into mono.
            mono = new MultiplexingSampleProvider([buffered.ToSampleProvider()], 1);
        }
        else if (format.Channels > 1)
        {
            var stereoToMono = new StereoToMonoSampleProvider(buffered.ToSampleProvider())
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f,
            };
            mono = stereoToMono;
        }
        else
        {
            mono = buffered.ToSampleProvider();
        }

        var resampled = new WdlResamplingSampleProvider(mono, TargetSampleRate);
        pcm16 = resampled.ToWaveProvider16(); // 16-bit PCM

        var writer = new WaveFileWriter(path, pcm16.WaveFormat);
        if (path.EndsWith("mic.wav", StringComparison.OrdinalIgnoreCase))
            _micWriter = writer;
        else
            _loopbackWriter = writer;
    }

    /// <summary>Background writer: pulls 16 kHz/16-bit PCM and writes until stopped, then drains.</summary>
    private void WriteLoop(IWaveProvider provider)
    {
        // Identify the matching writer for this provider.
        var writer = ReferenceEquals(provider, _micProvider) ? _micWriter : _loopbackWriter;
        if (writer is null)
            return;

        var buf = new byte[16000 * 2]; // 1 s at 16 kHz 16-bit mono
        try
        {
            while (!_stopRequested)
            {
                int n = provider.Read(buf, 0, buf.Length);
                if (n > 0)
                    writer.Write(buf, 0, n);
                else
                    Thread.Sleep(20);
            }

            // Drain whatever is left in the pipeline before closing.
            while (true)
            {
                int n = provider.Read(buf, 0, buf.Length);
                if (n <= 0)
                    break;
                writer.Write(buf, 0, n);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Writer thread error: {ex.Message}");
        }
        finally
        {
            writer.Flush();
            writer.Dispose();
            if (ReferenceEquals(writer, _micWriter))
                _micWriter = null;
            else
                _loopbackWriter = null;
        }
    }

    private static MMDevice FindDevice(MMDeviceEnumerator enumerator, DataFlow flow, string name)
    {
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            if (device.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase))
                return device;
        }
        throw new FileNotFoundException($"Audio device matching '{name}' not found.", name);
    }
}
