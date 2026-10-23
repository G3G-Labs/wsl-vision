using System.Text.Json;
using WslVision.Models;

namespace WslVision.Services;

/// <summary>
/// Lê e grava o workspaces.json. Cada alteração agenda uma gravação 500 ms depois (junta mudanças em sequência);
/// a gravação é atômica (temporário + renomear) e o último arquivo válido fica como .bak, então desligar o PC no
/// meio dela não perde os workspaces. Uma gravação que falha (arquivo em uso, disco cheio) fica na fila e é tentada
/// de novo; nunca derruba o app.
/// </summary>
public sealed class WorkspaceStore(string? folder = null, TimeSpan? delay = null, TimeSpan? retryDelay = null) : IDisposable
{
    private const int ReadAttempts = 5;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly TimeSpan _delay = delay ?? TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);
    private readonly object _gate = new();
    private Timer? _timer;
    private string? _pending;
    private bool _failing;
    private bool _disposed;

    // O arquivo do disco não foi usado na abertura (em uso ou corrompido): antes de gravar por cima, ele é guardado
    // como .ilegivel, e não vira o .bak.
    private bool _mainUnusable;

    public string Folder { get; } = folder ?? SettingsService.DefaultFolder;

    private string FilePath => Path.Combine(Folder, "workspaces.json");

    public WorkspaceFile Current { get; private set; } = new();

    /// <summary>O arquivo ainda não existia ao abrir (primeira vez com workspaces).</summary>
    public bool IsNew { get; private set; }

    /// <summary>Aviso para o usuário quando o arquivo não pôde ser usado na abertura (ou null).</summary>
    public string? LoadProblem { get; private set; }

    /// <summary>Uma gravação agendada falhou (avisa uma vez por sequência de falhas). Vem de outro thread.</summary>
    public event EventHandler<Exception>? SaveFailed;

    public void Load()
    {
        LoadProblem = null;
        _mainUnusable = false;
        IsNew = !File.Exists(FilePath);
        if (IsNew)
        {
            Current = new WorkspaceFile();
            return;
        }
        if (!TryRead(FilePath, out var json))
        {
            // Em uso por outro programa (antivírus, sincronização): o arquivo provavelmente está bom e não pode se perder.
            _mainUnusable = true;
            Current = TryBackup() ?? new WorkspaceFile();
            Migrate(Current);
            LoadProblem = "Os workspaces do Terminal estavam em uso por outro programa. O app abriu a última cópia boa e, antes de gravar, guarda o arquivo original como workspaces.json.ilegivel.";
            return;
        }
        try
        {
            Current = Deserialize(json) ?? new WorkspaceFile();
            Migrate(Current);
        }
        catch (JsonException)
        {
            _mainUnusable = true;
            try { File.Copy(FilePath, FilePath + ".ilegivel", overwrite: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* copia na gravação */ }
            Current = TryBackup() ?? new WorkspaceFile();
            Migrate(Current);
            LoadProblem = "Os workspaces do Terminal estavam corrompidos. O app voltou à última cópia boa; o arquivo original ficou como workspaces.json.ilegivel.";
        }
    }

    /// <summary>Lê o arquivo, tentando de novo por cerca de um segundo se ele estiver bloqueado.</summary>
    private bool TryRead(string path, out string json)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                json = File.ReadAllText(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == ReadAttempts)
                {
                    json = "";
                    return false;
                }
                Thread.Sleep(_retryDelay);
            }
        }
    }

    private WorkspaceFile? TryBackup()
    {
        try
        {
            return File.Exists(FilePath + ".bak") && TryRead(FilePath + ".bak", out var json) ? Deserialize(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Agenda a gravação do estado atual. Chame no thread da interface, depois de mudar <see cref="Current"/>.</summary>
    public void ScheduleSave()
    {
        // O texto sai agora, no thread que mudou os dados; só a escrita no disco fica para depois.
        var json = Serialize(Current);
        lock (_gate)
        {
            _pending = json;
            if (_disposed)
                return;
            _timer ??= new Timer(_ => OnTimer());
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Gravação agendada. Roda fora do thread da interface: uma exceção aqui fecharia o app.</summary>
    private void OnTimer()
    {
        try
        {
            Flush();
            _failing = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // O que não foi gravado voltou para a fila: tenta de novo daqui a pouco.
            lock (_gate)
                _timer?.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            if (!_failing)
            {
                _failing = true;
                SaveFailed?.Invoke(this, ex);
            }
        }
    }

    /// <summary>Grava agora o que estiver pendente (ao fechar o app, ou para não depender do atraso). Se falhar, continua pendente.</summary>
    public void Flush()
    {
        string? json;
        lock (_gate)
        {
            json = _pending;
            _pending = null;
        }
        if (json is null)
            return;
        try
        {
            Write(json);
        }
        catch
        {
            // Só volta se nada mais novo foi agendado enquanto isso.
            lock (_gate)
                _pending ??= json;
            throw;
        }
    }

    /// <summary>Grava o estado atual imediatamente (ex.: antes de apagar os dados antigos de uma migração).</summary>
    public void SaveNow()
    {
        lock (_gate)
            _pending = null;
        Write(Serialize(Current));
    }

    private void Write(string json)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath))
            {
                // Se esta cópia falhar (arquivo ainda em uso), a gravação para aqui e o original fica intacto.
                if (_mainUnusable)
                    File.Copy(FilePath, FilePath + ".ilegivel", overwrite: true);
                else
                    File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            }
            File.Move(tmp, FilePath, overwrite: true);
            _mainUnusable = false;
        }
    }

    public static string Serialize(WorkspaceFile f) => JsonSerializer.Serialize(f, Json);

    public static WorkspaceFile? Deserialize(string json) => JsonSerializer.Deserialize<WorkspaceFile>(json, Json);

    /// <summary>
    /// Os antigos comandos rápidos viram o grupo "Comandos rápidos": uma aba por comando, que só roda quando pedida.
    /// </summary>
    public static WorkspaceGroup FromQuickCommands(IEnumerable<QuickCommand> commands) => new()
    {
        Name = "Comandos rápidos",
        Color = WorkspaceColors.All[^1],
        Collapsed = true,
        Folder = "~",
        Tabs = commands.Select(c => new WorkspaceTab
        {
            Name = c.Name,
            Folder = string.IsNullOrWhiteSpace(c.Folder) ? "~" : c.Folder,
            Startup = c.Command,
        }).ToList(),
    };

    /// <summary>
    /// Formato antigo: o projeto Compose era do workspace inteiro. Ele passa para as abas que abrem na pasta do
    /// workspace ("." ou vazia); sem nenhuma assim, para a primeira. As outras abas acham o projeto delas pela pasta.
    /// </summary>
    public static void Migrate(WorkspaceFile file)
    {
        foreach (var g in file.Groups)
        {
            if (g.LegacyComposeProject is not { } project)
                continue;
            var atRoot = g.Tabs.Where(t => t.Folder.Trim() is "" or "." or "./").ToList();
            foreach (var t in atRoot.Count > 0 ? atRoot : g.Tabs.Take(1))
                t.ComposeProject ??= project;
            g.LegacyComposeProject = null;
        }
    }

    /// <summary>
    /// O projeto Compose de uma pasta: o de pasta igual ou a mais funda que a contém (front-end e back-end, cada um
    /// com o seu, mesmo dentro da mesma pasta de agrupamento). Pastas já expandidas (sem "~").
    /// </summary>
    public static string? ProjectFor(string folder, IEnumerable<(string Project, string Folder)> projects)
    {
        var f = folder.TrimEnd('/');
        return projects
            .Select(p => (p.Project, Dir: p.Folder.TrimEnd('/')))
            .Where(p => p.Dir.Length > 0 && (f == p.Dir || f.StartsWith(p.Dir + "/", StringComparison.Ordinal)))
            .OrderByDescending(p => p.Dir.Length)
            .Select(p => p.Project)
            .FirstOrDefault();
    }

    /// <summary>Pasta real da aba: "." e caminhos relativos partem da pasta do grupo; "/…" e "~…" valem como estão.</summary>
    public static string ResolveFolder(string groupFolder, string tabFolder)
    {
        var t = tabFolder.Trim();
        if (t.Length == 0 || t == ".")
            return groupFolder;
        if (t.StartsWith('/') || t == "~" || t.StartsWith("~/", StringComparison.Ordinal))
            return t;
        if (t.StartsWith("./", StringComparison.Ordinal))
            t = t[2..];
        return groupFolder.TrimEnd('/') + "/" + t;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        try
        {
            Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // O app está fechando: não há como tentar de novo. Fica a versão anterior, que continua íntegra.
        }
    }
}
