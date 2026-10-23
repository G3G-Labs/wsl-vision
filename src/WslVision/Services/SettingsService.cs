using System.Text.Json;
using System.Text.Json.Serialization;
using WslVision.Models;

namespace WslVision.Services;

/// <summary>Configurações do app em %AppData%\WslVision\settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WslVision");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            Migrate(Current);
        }
        catch
        {
            // Arquivo corrompido: guarda uma cópia e começa do zero.
            try { File.Copy(FilePath, FilePath + ".corrompido", overwrite: true); } catch { /* sem cópia */ }
            Current = new AppSettings();
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

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
        File.Move(tmp, FilePath, overwrite: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AppSettings> change)
    {
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
