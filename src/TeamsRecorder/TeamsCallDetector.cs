using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace TeamsRecorder;

/// <summary>
/// One WASAPI audio session seen on a capture (microphone) endpoint.
/// </summary>
/// <param name="Device">Friendly name of the capture endpoint that owns the session.</param>
/// <param name="ProcessId">Owning process id (0 = system session).</param>
/// <param name="ProcessName">Owning process name, or <c>"(exited)"</c> when it is gone.</param>
/// <param name="State">WASAPI session state as reported by the endpoint.</param>
/// <param name="IsTeamsProcess">True when <paramref name="ProcessName"/> is <c>ms-teams</c>.</param>
/// <param name="ParentChainHitTeams">True when the process's parent chain (5 levels) reaches <c>ms-teams</c>.</param>
public sealed record TeamsCallSession(
    string Device,
    int ProcessId,
    string ProcessName,
    string State,
    bool IsTeamsProcess,
    bool ParentChainHitTeams)
{
    /// <summary>True when this session counts as a Teams session (own name or parent chain).</summary>
    public bool IsTeams => IsTeamsProcess || ParentChainHitTeams;

    /// <summary>True when this session is the live microphone signal of a Teams process.</summary>
    public bool IsLiveTeamsCapture => IsTeams && State == LiveStateName;

    /// <summary>
    /// The <see cref="State"/> string that means "this session is producing audio right now".
    /// NAudio's <c>State.ToString()</c> prints the full enum member name
    /// (<c>AudioSessionStateActive</c>), so this is derived from the enum rather than spelled out.
    /// </summary>
    public const string LiveStateName = nameof(NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive);
}

/// <summary>
/// Result of <see cref="TeamsCallDetector.Probe"/>: every capture session that was seen,
/// whether a Teams call is in progress, and the failure reason when the probe could not run.
/// </summary>
/// <param name="Sessions">Every session seen on every active capture endpoint (empty when the probe failed).</param>
/// <param name="CallActive">True when a Teams-owned capture session is Active.</param>
/// <param name="Error">Non-null when the probe failed; <see cref="CallActive"/> is then false.</param>
public sealed record TeamsCallStatus(
    IReadOnlyList<TeamsCallSession> Sessions,
    bool CallActive,
    string? Error)
{
    /// <summary>
    /// Multi-line diagnostic text: one line per session, then the verdict (and the
    /// error, when the probe failed). Safe to pass to <c>Debug.WriteLine</c> / the console.
    /// </summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("TeamsCallDetector: ").Append(Sessions.Count).Append(" capture session(s); CallActive=");
        sb.Append(CallActive ? "True" : "False");
        if (Error is { } error)
            sb.Append("; error=").Append(error);
        sb.AppendLine();
        foreach (var s in Sessions)
            sb.Append("  ").Append(TeamsCallDetector.FormatSession(s)).AppendLine();
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Detects whether a Teams call is in progress by inspecting the audio sessions that
/// hold the microphone.
/// <para>
/// Teams only opens the microphone while you are in a call, so a capture session that is
/// <see cref="AudioSessionState.AudioSessionStateActive"/> and owned by a Teams process is the
/// signal. "Teams process" means the process is named <c>ms-teams</c>, or its parent chain
/// (walked up to <see cref="MaxParentHops"/> levels) reaches <c>ms-teams</c> — the new Teams
/// runs its media stack in a child process. This app's own sessions are excluded.
/// </para>
/// <para>
/// <see cref="Probe"/> never throws: on failure it returns <c>CallActive = false</c> plus an
/// error string. It allocates no state, so it is safe to call from the UI thread.
/// </para>
/// </summary>
public static partial class TeamsCallDetector
{
    /// <summary>Process name of the Teams client (matched case-insensitively).</summary>
    public const string TeamsProcessName = "ms-teams";

    /// <summary>How far up the parent chain a child process is traced to find <c>ms-teams</c>.</summary>
    public const int MaxParentHops = 5;

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static partial int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    /// <summary>
    /// ntdll PROCESS_BASIC_INFORMATION (winternl.h). Field order and pointer-sized fields
    /// match the native layout on x86 and x64; only the last field is read.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    /// <summary>
    /// Enumerates every session on every active capture endpoint and reports whether a
    /// Teams process currently holds an Active one. Never throws.
    /// </summary>
    public static TeamsCallStatus Probe()
    {
        var sessions = new List<TeamsCallSession>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                // One device failing (hot-unplug, access denied) must not sink the whole probe.
                try
                {
                    CollectFrom(device, sessions);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"TeamsCallDetector: device scan failed ({device?.ID}): {ex.Message}");
                }
                finally
                {
                    try { device.Dispose(); } catch { /* best effort */ }
                }
            }

            var callActive = sessions.Any(s => s.IsLiveTeamsCapture);
            return new TeamsCallStatus(sessions, callActive, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TeamsCallDetector: probe failed: {ex}");
            return new TeamsCallStatus(sessions, false, ex.Message);
        }
    }

    /// <summary>One line per session, for recorder.log and <c>--meeting-probe</c>.</summary>
    public static string FormatSession(TeamsCallSession s) =>
        $"{s.Device} | pid {s.ProcessId} | {s.ProcessName} | teams: {(s.IsTeams ? "yes" : "no")}" +
        $" (name {(s.IsTeamsProcess ? "yes" : "no")}, parent-chain {(s.ParentChainHitTeams ? "yes" : "no")}) | {s.State}";

    private static void CollectFrom(MMDevice device, List<TeamsCallSession> sessions)
    {
        var friendly = device.FriendlyName;
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();

        // Parent ids are shared between the sessions on all devices; cache them per probe.
        var parentCache = new Dictionary<int, int?>();

        var controls = manager.Sessions;
        for (var i = 0; i < controls.Count; i++)
        {
            var session = controls[i];
            var pid = (int)session.GetProcessID;
            var name = ProcessNameOf(pid);
            var isTeams = string.Equals(name, TeamsProcessName, StringComparison.OrdinalIgnoreCase);
            var chainHit = !isTeams && ParentChainReachesTeams(pid, parentCache);

            sessions.Add(new TeamsCallSession(
                friendly,
                pid,
                name,
                session.State.ToString(),
                isTeams,
                chainHit));
        }
    }

    /// <summary>Process name for a session's pid; "(exited)" when the process is gone.</summary>
    private static string ProcessNameOf(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch (Exception ex)
        {
            // The owning process exited between enumeration and lookup, or is
            // protected: the pid is still the useful part of the diagnostic.
            Debug.WriteLine($"TeamsCallDetector: process {pid} name unavailable: {ex.Message}");
            return pid == 0 ? "(system)" : "(exited)";
        }
    }

    /// <summary>True when this pid — or any ancestor within <see cref="MaxParentHops"/> — is <c>ms-teams</c>.</summary>
    private static bool ParentChainReachesTeams(int pid, Dictionary<int, int?> cache)
    {
        var ourPid = Environment.ProcessId;
        var current = pid;

        for (var hop = 0; hop < MaxParentHops; hop++)
        {
            if (!cache.TryGetValue(current, out var parent))
            {
                parent = current == 0 ? null : GetParentProcessId(current);
                cache[current] = parent;
            }

            if (parent is not { } parentId || parentId == 0 || parentId == current)
                return false;

            if (string.Equals(ProcessNameOf(parentId), TeamsProcessName, StringComparison.OrdinalIgnoreCase))
                return true;

            // Never climb through ourselves; the chain is broken by then.
            if (parentId == ourPid)
                return false;

            current = parentId;
        }

        return false;
    }

    /// <summary>
    /// Parent pid via ntdll!NtQueryInformationProcess(ProcessBasicInformation). Returns null
    /// when the process is gone, protected, or the query fails.
    /// </summary>
    private static int? GetParentProcessId(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var info = new ProcessBasicInformation();
            // ProcessBasicInformation = 0. Success is a non-negative NTSTATUS.
            var status = NtQueryInformationProcess(
                handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status < 0)
                return null;

            var parent = (int)(long)info.InheritedFromUniqueProcessId;
            return parent > 0 ? parent : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
