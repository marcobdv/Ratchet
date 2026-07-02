using System.Diagnostics;
using System.Text;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Runs an external process to completion, capturing combined stdout+stderr. Centralises
/// the things every shell-spawning tool needs and used to get wrong:
///
///   1. <b>Kill on cancel.</b> <c>Process.Dispose()</c> does NOT terminate a running child —
///      so a cancelled command (a build, a test run, a hung gate) would otherwise keep
///      running orphaned. On cancellation we kill the whole process tree before rethrowing.
///   2. <b>Drain on exit.</b> <c>WaitForExitAsync</c> does not guarantee the async output
///      handlers have flushed; the synchronous <c>WaitForExit()</c> afterwards does, so we
///      never lose the tail of a short-lived command's output.
///   3. <b>Closed stdin.</b> A child that prompts (git credentials, a pager, `set /p`) gets
///      EOF instead of inheriting the console and hanging the turn forever.
///   4. <b>Bounded output.</b> Everything captured lands in the transcript and is re-sent
///      every model call — one `git diff` after a refactor (or a runaway `yes`) must not
///      blow the context window or the heap. Head + tail are kept; the middle is elided
///      with a marker.
///   5. <b>Timeout.</b> An optional deadline kills the tree and returns the partial output
///      as a failed result — essential for unattended runs, where nobody can Ctrl+C.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Default cap on captured output (chars). ~50k tokens — already generous for a transcript.</summary>
    public const int DefaultMaxOutputChars = 200_000;

    public static async Task<(int exitCode, string output)> RunAsync(
        ProcessStartInfo psi, CancellationToken ct,
        TimeSpan? timeout = null, int maxOutputChars = DefaultMaxOutputChars)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        // Modern CLIs (git, dotnet, node) emit UTF-8; without this, Windows decodes
        // redirected output with the OEM codepage and non-ASCII becomes mojibake.
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        using var proc = new Process { StartInfo = psi };
        var output = new BoundedLineBuffer(maxOutputChars);
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AddLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AddLine(e.Data); };

        proc.Start();
        try { proc.StandardInput.Close(); } catch { /* child may have exited already */ }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) deadline.CancelAfter(t);

        try
        {
            await proc.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // The managed wrapper would be disposed by `using` without killing the OS process
            // tree; do it explicitly so nothing is left running detached.
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone / no access */ }
            if (ct.IsCancellationRequested) throw;   // caller cancellation propagates

            // Deadline, not cancellation: return the partial output as a failed result the
            // model can react to, instead of wedging an unattended run.
            try { proc.WaitForExit(); } catch { /* flushing is best-effort after a kill */ }
            lock (output)
                return (-1, output +
                    $"\n[timed out after {timeout!.Value.TotalSeconds:0}s — process tree killed; output above is partial]");
        }

        proc.WaitForExit();   // flush the async output handlers before reading the buffer
        lock (output) return (proc.ExitCode, output.ToString());
    }
}

/// <summary>
/// Line buffer with a hard character budget: the head fills first, then a rolling tail;
/// the elided middle is replaced by a single marker. Head+tail beats plain truncation
/// because command output puts the command/context first and the error last.
/// </summary>
internal sealed class BoundedLineBuffer
{
    private const int MaxLineChars = 20_000; // one minified/pathological line can't eat the budget

    private readonly int _headCap;
    private readonly int _tailCap;
    private readonly StringBuilder _head = new();
    private readonly Queue<string> _tail = new();
    private int _tailChars;
    private long _droppedChars;

    public BoundedLineBuffer(int maxChars)
    {
        _headCap = Math.Max(1, maxChars * 6 / 10);
        _tailCap = Math.Max(1, maxChars - _headCap);
    }

    public void AddLine(string line)
    {
        if (line.Length > MaxLineChars)
        {
            _droppedChars += line.Length - MaxLineChars;
            line = line[..MaxLineChars] + "…";
        }

        if (_tail.Count == 0 && _head.Length + line.Length + 1 <= _headCap)
        {
            _head.Append(line).Append('\n');
            return;
        }

        _tail.Enqueue(line);
        _tailChars += line.Length + 1;
        while (_tailChars > _tailCap && _tail.Count > 1)
        {
            var evicted = _tail.Dequeue();
            _tailChars -= evicted.Length + 1;
            _droppedChars += evicted.Length + 1;
        }
    }

    public override string ToString()
    {
        if (_tail.Count == 0) return _head.ToString();
        var marker = _droppedChars > 0 ? $"…[{_droppedChars:N0} chars of output omitted]…\n" : "";
        return _head + marker + string.Join('\n', _tail) + "\n";
    }
}
