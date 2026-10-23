using System.Text.Json;
using System.Text.Json.Serialization;
using WslVision.Models;

namespace WslVision.Services;

/// <summary>Configurações do app em %AppData%\WslVision\settings.json.</summary>
public sealed class SettingsService(string? folder = null, TimeSpan? retryDelay = null)
{
    private const int ReadAttempts = 5;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WslVision");

    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);

    // O arquivo existe mas não pôde ser lido ao abrir: a próxima gravação tenta lê-lo de novo antes de gravar por cima.
    private bool _unreadable;

    public string Folder { get; } = folder ?? DefaultFolder;

    private string FilePath => Path.Combine(Folder, "settings.json");

    public AppSettings Current { get; private set; } = new();

    /// <summary>Aviso para o usuário quando o arquivo não pôde ser usado na abertura (ou null).</summary>
    public string? LoadProblem { get; private set; }

    public event EventHandler? Changed;

    public void Load()
    {
        LoadProblem = null;
        _unreadable = false;
        if (!File.Exists(FilePath))
        {
            Current = new AppSettings();
            return;
        }
        if (!TryRead(out var json))
        {
            // Bloqueado por outro programa (antivírus, sincronização…): o arquivo está bom e não pode ser perdido.
            _unreadable = true;
            Current = new AppSettings();
            LoadProblem = "Não foi possível ler as configurações agora. O app usa o padrão e tenta de novo na próxima alteração, sem apagar o arquivo.";
            return;
        }
        try
        {
            Current = Deserialize(json) ?? new AppSettings();
            Migrate(Current);
        }
        catch (JsonException)
        {
            // Arquivo corrompido: guarda uma cópia e começa do zero.
            try { File.Copy(FilePath, FilePath + ".corrompido", overwrite: true); } catch { /* sem cópia */ }
            Current = new AppSettings();
            LoadProblem = "As configurações estavam corrompidas e foram recriadas. O arquivo antigo ficou como settings.json.corrompido.";
        }
    }

    /// <summary>Lê o arquivo, tentando de novo por cerca de um segundo se ele estiver bloqueado.</summary>
    private bool TryRead(out string json)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                json = File.ReadAllText(FilePath);
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

    /// <summary>Converte campos de versões anteriores do settings.json.</summary>
    public static void Migrate(AppSettings s)
    {
        // Antes havia só "iniciar o ambiente ao abrir" (sim ou não).
        if (s.LegacyStartEnvironmentOnLaunch == true && s.OnLaunch == LaunchAction.None)
            s.OnLaunch = LaunchAction.Environment;
        s.LegacyStartEnvironmentOnLaunch = null;
    }

    public static string Serialize(AppSettings s) => JsonSerializer.Serialize(s, Json);

    public static AppSettings? Deserialize(string json) => JsonSerializer.Deserialize<AppSettings>(json, Json);

    private void Save()
    {
        Directory.CreateDirectory(Folder);
        if (_unreadable)
        {
            // Continua ilegível: guarda o original ao lado antes de substituí-lo. Se nem a cópia der certo, a gravação
            // falha aqui e o arquivo fica intacto.
            if (File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".ilegivel", overwrite: true);
            _unreadable = false;
        }
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, Serialize(Current));
        File.Move(tmp, FilePath, overwrite: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AppSettings> change)
    {
        // As configurações não puderam ser lidas ao abrir: se o arquivo já está disponível, a alteração vale sobre ele,
        // e não sobre o padrão que estava na memória.
        if (_unreadable && File.Exists(FilePath) && TryRead(out var json))
        {
            try
            {
                Current = Deserialize(json) ?? new AppSettings();
                Migrate(Current);
                LoadProblem = null;
                _unreadable = false;
            }
            catch (JsonException)
            {
                // Agora deu para ler e está corrompido: a gravação abaixo guarda o original como .ilegivel.
            }
        }
        change(Current);
        Save();
    }

    /// <summary>Põe a pasta no topo das recentes (sem repetir), guardando no máximo 6.</summary>
    public void RememberRecentFolder(string path) =>
        Update(s =>
        {
            s.RecentFolders.Remove(path);
            s.RecentFolders.Insert(0, path);
            if (s.RecentFolders.Count > 6)
                s.RecentFolders.RemoveRange(6, s.RecentFolders.Count - 6);
        });

    public void RecordChange(ConfigChangeKind kind, string? distro, string? detail = null)
    {
        if (Current.Changes.Any(c => c.Kind == kind && c.Distro == distro))
            return;
        Update(s => s.Changes.Add(new ConfigChange { Kind = kind, Distro = distro, Detail = detail }));
    }
}
