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
        CancellationToken ct = default,
        bool logFailure = true)
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
        var started = Stopwatch.StartNew();
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("processo", ex, $"Não abriu: {Describe(fileName, psi.ArgumentList)}");
            throw;
        }

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
            AppLog.Error("processo", $"Sem resposta em {started.Elapsed.TotalSeconds:0} s, encerrado: {Describe(fileName, psi.ArgumentList)}");
            throw new TimeoutException($"O comando {Path.GetFileName(fileName)} demorou demais para responder.");
        }

        var result = new ProcessResult(p.ExitCode, await outTask, await errTask);
        if (!result.Success && logFailure)
        {
            var err = result.StdErr.Trim();
            AppLog.Warn("processo", $"Código {result.ExitCode} em {started.ElapsedMilliseconds} ms: {Describe(fileName, psi.ArgumentList)}"
                + (err.Length > 0 ? $"{Environment.NewLine}{Truncate(err, 1500)}" : ""));
        }
        return result;
    }

    /// <summary>O comando numa linha para o log (os scripts vão pelo stdin e não aparecem aqui).</summary>
    private static string Describe(string fileName, IEnumerable<string> args) =>
        Truncate($"{Path.GetFileName(fileName)} {string.Join(' ', args)}", 400);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

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
