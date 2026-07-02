using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// A Windows ConPTY-backed command runner — the "real pty" upgrade the Ratchet doc
/// calls for, living behind the same shell seam the <see cref="BashTool"/> uses.
///
/// ConPTY (CreatePseudoConsole) gives the child process a genuine console, so tools
/// that detect a TTY emit colour/progress and programs that refuse to run headless
/// will run. The cost is that the captured stream is a *terminal* stream: it carries
/// VT escape sequences and the shell's own echo. We strip the escapes before handing
/// the text back so the model sees readable output, but for pure capture the plain
/// <see cref="System.Diagnostics.Process"/> path is cleaner — which is why this is
/// opt-in (<c>RATCHET_PTY=1</c>), not the default.
///
/// Everything here is BCL P/Invoke; Core stays dependency-free.
/// </summary>
internal static class WindowsPty
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Run a command line under a pseudo-console; return combined output + exit code.</summary>
    public static async Task<(int exitCode, string output)> RunAsync(string commandLine, CancellationToken ct)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("ConPTY is Windows-only.");

        IntPtr hPC = IntPtr.Zero, attrList = IntPtr.Zero, hJob = IntPtr.Zero;
        IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero, outRead = IntPtr.Zero, outWrite = IntPtr.Zero;
        var procInfo = new PROCESS_INFORMATION();
        CancellationTokenRegistration reg = default;

        try
        {
            if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0)) ThrowLast("CreatePipe(in)");
            if (!CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0)) ThrowLast("CreatePipe(out)");

            // Job object with KILL_ON_JOB_CLOSE: the child and everything it spawns
            // (the actual build/test the shell launches) belong to the job, so closing
            // it on cancel kills the WHOLE tree — TerminateProcess on the shell alone
            // left grandchildren orphaned (the redirected-Process path already tree-kills).
            hJob = CreateJobObject(IntPtr.Zero, null);
            if (hJob != IntPtr.Zero) ConfigureKillOnClose(hJob);

            // COORD is two shorts packed into one DWORD (X = low word, Y = high word).
            // Passing it as a packed uint sidesteps small-struct by-value marshalling
            // quirks that can leave CreatePseudoConsole with a bad handle.
            const short cols = 120, rows = 30;
            uint size = ((uint)(ushort)rows << 16) | (ushort)cols;
            var hr = CreatePseudoConsole(size, inRead, outWrite, 0, out hPC);
            if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed (0x{hr:X8}).");
            if (hPC == IntPtr.Zero) throw new InvalidOperationException("CreatePseudoConsole returned a null handle.");

            attrList = BuildAttributeListWithPseudoConsole(hPC);

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED, IntPtr.Zero, null, ref si, out procInfo))
                ThrowLast("CreateProcess");

            // Assign to the job BEFORE the process runs (created suspended), so any
            // child it spawns is captured too, then release it.
            if (hJob != IntPtr.Zero) AssignProcessToJobObject(hJob, procInfo.hProcess);
            ResumeThread(procInfo.hThread);

            // Close our copies of the PTY-end handles ONLY after the child is created
            // and attached — closing them earlier severs the pseudo-console. After this
            // the sole writer on the output pipe is ConPTY, so closing it (below) is
            // what produces EOF on the read side.
            CloseHandle(inRead); inRead = IntPtr.Zero;
            CloseHandle(outWrite); outWrite = IntPtr.Zero;

            // If the caller cancels, close the job to kill the whole tree (falling back to
            // TerminateProcess if the job wasn't created) so the read loop can finish.
            var jobForCancel = hJob;
            var procForCancel = procInfo.hProcess;
            reg = ct.Register(() =>
            {
                try { if (jobForCancel != IntPtr.Zero) CloseHandle(jobForCancel); else TerminateProcess(procForCancel, 1); }
                catch { /* best effort */ }
            });

            // Read the output pipe to EOF on a background thread. EOF arrives once we
            // close the pseudo-console (below), after the child has exited.
            var progress = new Counter();
            var readTask = Task.Run(() => DrainPipe(outRead, progress), CancellationToken.None);

            await WaitHandleAsync(procInfo.hProcess, ct).ConfigureAwait(false);

            // The child has exited, but the pseudo-console's renderer (conhost) may not
            // have flushed the child's final output into the pipe yet. Closing the PC
            // immediately would tear conhost down mid-flush and lose that tail. Wait
            // until the output pipe stops growing before closing.
            await DrainToIdleAsync(progress).ConfigureAwait(false);

            // Closing the PC drops ConPTY's writer on the output pipe -> read EOF.
            ClosePseudoConsole(hPC); hPC = IntPtr.Zero;

            var bytes = await readTask.ConfigureAwait(false);

            // Cancellation is a cancellation, not a completed command: propagate it rather
            // than returning partial output that looks like a finished run.
            ct.ThrowIfCancellationRequested();

            // The exit code is only meaningful once the process has actually exited; after a
            // kill the handle can still read STILL_ACTIVE (259). Wait briefly so we report a
            // real code, not a phantom one.
            if (WaitForSingleObject(procInfo.hProcess, 2000) != WAIT_TIMEOUT &&
                GetExitCodeProcess(procInfo.hProcess, out var exit))
                return ((int)exit, Vt.Strip(Encoding.UTF8.GetString(bytes)));
            return (-1, Vt.Strip(Encoding.UTF8.GetString(bytes)));
        }
        finally
        {
            reg.Dispose();
            if (hPC != IntPtr.Zero) ClosePseudoConsole(hPC);
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (inRead != IntPtr.Zero) CloseHandle(inRead);
            if (inWrite != IntPtr.Zero) CloseHandle(inWrite);
            if (outRead != IntPtr.Zero) CloseHandle(outRead);
            if (outWrite != IntPtr.Zero) CloseHandle(outWrite);
            if (procInfo.hThread != IntPtr.Zero) CloseHandle(procInfo.hThread);
            if (procInfo.hProcess != IntPtr.Zero) CloseHandle(procInfo.hProcess);
            if (hJob != IntPtr.Zero) CloseHandle(hJob);   // also kill-on-close: reaps any stragglers
        }
    }

    private static void ConfigureKillOnClose(IntPtr hJob)
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, ptr, (uint)len);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private sealed class Counter { public long Value; }

    private static byte[] DrainPipe(IntPtr readHandle, Counter progress)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        while (true)
        {
            // ReadFile blocks until data or the write end closes; at EOF it returns
            // false with ERROR_BROKEN_PIPE (or true with 0 bytes). Either ends the loop.
            if (!ReadFile(readHandle, buf, (uint)buf.Length, out var read, IntPtr.Zero) || read == 0)
                break;
            ms.Write(buf, 0, (int)read);
            Interlocked.Exchange(ref progress.Value, ms.Length);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// After the child exits, wait for the output pipe to stop growing — a proxy for
    /// "conhost has flushed the last of the child's output" — before we tear the
    /// pseudo-console down. Bounded so a pathological stream can't hang the close.
    /// </summary>
    private static async Task DrainToIdleAsync(Counter progress)
    {
        long last = -1;
        var stable = 0;
        for (var i = 0; i < 30; i++)             // ~1.2s ceiling, post-exit only
        {
            var cur = Interlocked.Read(ref progress.Value);
            if (cur == last) { if (++stable >= 3) return; }   // unchanged ~120ms -> idle
            else { stable = 0; last = cur; }
            await Task.Delay(40, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task WaitHandleAsync(IntPtr handle, CancellationToken ct)
    {
        // Poll the process handle; cheap and keeps cancellation responsive without
        // marshalling the handle into a WaitHandle.
        while (WaitForSingleObject(handle, 50) == WAIT_TIMEOUT)
        {
            if (ct.IsCancellationRequested) return; // the ct.Register above will kill it
            await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static IntPtr BuildAttributeListWithPseudoConsole(IntPtr hPC)
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(list);
            ThrowLast("InitializeProcThreadAttributeList");
        }
        if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC,
                (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            ThrowLast("UpdateProcThreadAttribute");
        }
        return list;
    }

    private static void ThrowLast(string what) =>
        throw new InvalidOperationException($"{what} failed (Win32 error {Marshal.GetLastWin32Error()}).");

    // ---- constants --------------------------------------------------------
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    // ---- structs ----------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    // ---- imports ----------------------------------------------------------
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(uint size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);
}

/// <summary>Strips the VT/ANSI escape sequences a pseudo-console emits, leaving plain text.</summary>
internal static partial class Vt
{
    public static string Strip(string s)
    {
        if (s.IndexOf('\x1B') < 0) return s.Replace("\0", "");
        var cleaned = Csi().Replace(s, "");
        cleaned = Osc().Replace(cleaned, "");
        cleaned = Other().Replace(cleaned, "");
        return cleaned.Replace("\0", "");
    }

    // CSI: ESC [ ... final-byte
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Csi();

    // OSC: ESC ] ... (BEL | ESC \)
    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)")]
    private static partial Regex Osc();

    // Other ESC-prefixed two/three byte sequences (charset selection, single shifts, etc.)
    [GeneratedRegex(@"\x1B[@-Z\\-_=>]|\x1B[()][0-9A-Za-z]")]
    private static partial Regex Other();
}
