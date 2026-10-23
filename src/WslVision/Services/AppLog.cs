using System.Diagnostics;
using System.Text;

namespace WslVision.Services;

/// <summary>
/// Registro em arquivo para achar e corrigir problemas: o que o app fez, mudanças de estado, avisos e erros
/// com o stack trace. Um arquivo por dia em %LocalAppData%\WslVision\logs, guardado por 14 dias e com
/// limite de tamanho, para nunca encher o disco. Escrever nunca derruba o app: se falhar, a linha se perde.
/// </summary>
public static class AppLog
{
    private const int KeepDays = 14;
    private const long MaxBytesPerDay = 5 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static StreamWriter? _writer;
    private static DateOnly _day;
    private static bool _full;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslVision", "logs");

    /// <summary>O arquivo de hoje (pode ainda não existir).</summary>
    public static string CurrentFile => FileFor(DateOnly.FromDateTime(DateTime.Now));

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Warn(string area, string message) => Write("WARN", area, message);

    public static void Error(string area, string message) => Write("ERRO", area, message);

    public static void Error(string area, Exception ex, string? context = null) =>
        Write("ERRO", area, context is null ? ex.ToString() : $"{context}{Environment.NewLine}{ex}");

    /// <summary>Apaga os arquivos mais antigos que <see cref="KeepDays"/> dias. Chamado ao abrir o app.</summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;
            var limit = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in Directory.EnumerateFiles(Folder, "wslvision-*.log"))
                if (File.GetLastWriteTime(file) < limit)
                    File.Delete(file);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public static void Flush()
    {
        lock (Gate)
        {
            try { _writer?.Flush(); } catch { /* sem disco: nada a fazer */ }
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string FileFor(DateOnly day) => Path.Combine(Folder, $"wslvision-{day:yyyy-MM-dd}.log");

    private static void Write(string level, string area, string message)
    {
        var now = DateTime.Now;
        // Uma entrada por linha; linhas seguintes (stack trace, saída de comando) ficam recuadas.
        var text = message.ReplaceLineEndings(Environment.NewLine + "    ");
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} {level} [{area}] {text}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                var writer = WriterFor(DateOnly.FromDateTime(now));
                if (writer is null)
                    return;
                if (writer.BaseStream.Length > MaxBytesPerDay)
                {
                    _full = true;
                    writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} WARN [log] Limite de {MaxBytesPerDay / 1024 / 1024} MB do dia atingido; o registro volta amanhã.");
                    writer.Flush();
                    return;
                }
                writer.WriteLine(line);
                writer.Flush();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private static StreamWriter? WriterFor(DateOnly day)
    {
        if (_writer is not null && day == _day)
            return _full ? null : _writer;
        _writer?.Dispose();
        Directory.CreateDirectory(Folder);
        // FileShare.ReadWrite: dá para abrir o log no Bloco de Notas com o app rodando.
        var stream = new FileStream(FileFor(day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, Utf8NoBom);
        _day = day;
        _full = stream.Length > MaxBytesPerDay;
        return _full ? null : _writer;
    }
}
