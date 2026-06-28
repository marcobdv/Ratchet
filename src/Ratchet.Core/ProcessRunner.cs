using System.Diagnostics;
using System.Text;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Runs an external process to completion, capturing combined stdout+stderr. Centralises
/// two things every shell-spawning tool needs and used to get wrong:
///
///   1. <b>Kill on cancel.</b> <c>Process.Dispose()</c> does NOT terminate a running child —
///      so a cancelled command (a build, a test run, a hung gate) would otherwise keep
///      running orphaned. On cancellation we kill the whole process tree before rethrowing.
///   2. <b>Drain on exit.</b> <c>WaitForExitAsync</c> does not guarantee the async output
///      handlers have flushed; the synchronous <c>WaitForExit()</c> afterwards does, so we
///      never lose the tail of a short-lived command's output.
/// </summary>
public static class ProcessRunner
{
    public static async Task<(int exitCode, string output)> RunAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The managed wrapper would be disposed by `using` without killing the OS process
            // tree; do it explicitly so nothing is left running detached.
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone / no access */ }
            throw;
        }

        proc.WaitForExit();   // flush the async output handlers before reading the buffer
        lock (output) return (proc.ExitCode, output.ToString());
    }
}
