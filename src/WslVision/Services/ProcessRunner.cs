using System.Diagnostics;
using System.Text;

namespace WslVision.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public static class ProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi };
        p.Start();

        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);

        if (stdin is not null)
        {
            // Scripts vão para o Linux: sem BOM e com quebras de linha LF.
            var bytes = Utf8NoBom.GetBytes(stdin.Replace("\r\n", "\n"));
            await p.StandardInput.BaseStream.WriteAsync(bytes, ct);
            await p.StandardInput.BaseStream.FlushAsync(ct);
            p.StandardInput.Close();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(p);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"O comando {Path.GetFileName(fileName)} demorou demais para responder.");
        }

        return new ProcessResult(p.ExitCode, await outTask, await errTask);
    }

    public static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);
        }
        catch
        {
            // O processo já terminou.
        }
    }
}
