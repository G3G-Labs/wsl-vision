using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Services;
using WslVision.Services.Docker;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

/// <summary>Um item que dá para limpar, com o tamanho medido na última análise.</summary>
public sealed partial class CleanupItem(string key, string title, string description, bool isChecked, bool isDangerous = false) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Description { get; } = description;

    /// <summary>Apaga dados que não voltam (ex.: volumes): fica desmarcado e em vermelho.</summary>
    public bool IsDangerous { get; } = isDangerous;

    [ObservableProperty]
    private bool _isChecked = isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private long? _size;

    public string SizeText => Size switch
    {
        null => "—",
        0 => "vazio",
        { } s => Formatting.Bytes(s),
    };
}

public sealed partial class MaintenanceViewModel : PageViewModel
{
    private readonly AppState _state;
    private readonly MaintenanceService _maintenance;
    private readonly DockerManager _docker;
    private readonly SettingsService _settings;
    private readonly MetricsService _metrics;
    private readonly DialogService _dialogs;
    private readonly Notifier _notifier;

    public MaintenanceViewModel(AppState state, MaintenanceService maintenance, DockerManager docker, SettingsService settings,
        MetricsService metrics, DialogService dialogs, Notifier notifier)
    {
        _state = state;
        _maintenance = maintenance;
        _docker = docker;
        _settings = settings;
        _metrics = metrics;
        _dialogs = dialogs;
        _notifier = notifier;

        DockerItems =
        [
            new("images", "Imagens sem uso", "Imagens que nenhum container usa. Se precisar de novo, o Docker baixa outra vez.", true),
            new("buildcache", "Cache de build", "Camadas guardadas pelo docker build. O próximo build pode demorar um pouco mais.", true),
            new("dockerlogs", "Logs dos containers", "Esvazia os arquivos de log de todos os containers. Eles continuam rodando.", true),
            new("containers", "Containers parados", "Inclui os de projetos Compose (o compose up recria). O que foi gravado fora de volumes se perde.", false),
            new("volumes", "Volumes sem uso", "Volumes que nenhum container usa, como bancos de projetos removidos. Os dados são apagados para sempre.", false, isDangerous: true),
        ];
        LinuxItems =
        [
            new("apt", "Cache do apt", "Pacotes .deb já instalados, guardados pelo apt.", true),
            new("journal", "Logs antigos do sistema", "Mantém só os 50 MB mais recentes do journal do systemd.", true),
            new("usercache", "Cache dos programas (~/.cache)", "pip, yarn, navegadores de teste e outras ferramentas. É recriado quando preciso.", true),
            new("npm", "Cache do npm", "Pacotes baixados pelo npm. O próximo npm install baixa o que faltar.", true),
            new("tmp", "Temporários antigos (/tmp)", "Arquivos em /tmp sem alteração há mais de 7 dias.", true),
        ];
        foreach (var item in DockerItems)
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(DockerSelectedText));
        foreach (var item in LinuxItems)
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(LinuxSelectedText));

        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.IsDistroRunning) or nameof(AppState.IsDockerUp))
            {
                OnPropertyChanged(nameof(CanAnalyze));
                OnPropertyChanged(nameof(ShowDocker));
                if (IsActive && CanAnalyze && !HasAnalyzed && !IsBusy)
                    _ = AnalyzeAsync();
            }
        };
    }

    public override string Title => "Manutenção";

    public override SymbolRegular Icon => SymbolRegular.Broom24;

    public AppState State => _state;

    public ObservableCollection<CleanupItem> DockerItems { get; }

    public ObservableCollection<CleanupItem> LinuxItems { get; }

    [ObservableProperty]
    private bool _hasAnalyzed;

    [ObservableProperty]
    private string _pageCacheText = "—";

    [ObservableProperty]
    private string _vmmemText = "—";

    [ObservableProperty]
    private string _vhdText = "—";

    [ObservableProperty]
    private string _diskUsedText = "—";

    [ObservableProperty]
    private string? _vhdPath;

    public bool CanAnalyze => _state.IsDistroRunning;

    public bool ShowDocker => _state.IsDockerUp;

    public string? Distro => _settings.Current.Distro;

    public string DockerSelectedText => SelectedText(DockerItems);

    public string LinuxSelectedText => SelectedText(LinuxItems);

    private static string SelectedText(IEnumerable<CleanupItem> items)
    {
        var total = items.Where(i => i.IsChecked && i.Size is > 0).Sum(i => i.Size!.Value);
        return total > 0 ? $"Limpar selecionados · {Formatting.Bytes(total)}" : "Limpar selecionados";
    }

    protected override void OnActivated()
    {
        RefreshDiskAndMemory();
        if (CanAnalyze && !HasAnalyzed)
            _ = AnalyzeAsync();
    }

    private void RefreshDiskAndMemory()
    {
        VhdPath = Distro is { } d ? WslService.GetVhdPath(d) : null;
        VhdText = VhdPath is { } p && File.Exists(p) ? Formatting.Bytes(new FileInfo(p).Length) : "—";
        VmmemText = MetricsService.GetVmmemBytes() is { } vm ? Formatting.Bytes(vm) : "WSL desligado";
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (Distro is not { } distro || !CanAnalyze)
            return;
        IsBusy = true;
        try
        {
            var sizes = await _maintenance.MeasureAsync(distro, Home);
            foreach (var item in LinuxItems)
                item.Size = sizes.GetValueOrDefault(item.Key);
            // O journal mantém 50 MB: só o que passa disso é liberado.
            LinuxItems.First(i => i.Key == "journal").Size = Math.Max(0, sizes.GetValueOrDefault("journal") - 50L * 1024 * 1024);
            DockerItems.First(i => i.Key == "dockerlogs").Size = sizes.GetValueOrDefault("dockerlogs");
            PageCacheText = Formatting.Bytes(sizes.GetValueOrDefault("pagecache"));
            DiskUsedText = Formatting.Bytes(sizes.GetValueOrDefault("diskused"));

            if (_state.IsDockerUp && _docker.Client is { } client)
            {
                var df = await client.GetDiskUsageAsync();
                SetSize(DockerItems, "images", df.ReclaimableImages);
                SetSize(DockerItems, "buildcache", df.ReclaimableBuildCache);
                SetSize(DockerItems, "containers", df.ReclaimableContainers);
                SetSize(DockerItems, "volumes", df.ReclaimableVolumes);
            }
            RefreshDiskAndMemory();
            HasAnalyzed = true;
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void SetSize(IEnumerable<CleanupItem> items, string key, long size) => items.First(i => i.Key == key).Size = size;

    private string Home => _settings.Current.LinuxHome ?? "/root";

    [RelayCommand]
    private async Task CleanDockerAsync()
    {
        if (Distro is not { } distro || _docker.Client is not { } client)
            return;
        var selected = DockerItems.Where(i => i.IsChecked).ToList();
        if (selected.Count == 0 || !await ConfirmAsync("Limpar o Docker?", selected))
            return;
        IsBusy = true;
        try
        {
            var freed = await MaintenanceService.PruneDockerAsync(client, selected.Select(i => i.Key));
            if (selected.Any(i => i.Key == "dockerlogs"))
            {
                freed += DockerItems.First(i => i.Key == "dockerlogs").Size ?? 0;
                await _maintenance.CleanLinuxAsync(distro, Home, ["dockerlogs"]);
            }
            _notifier.Show($"Docker limpo: {Formatting.Bytes(freed)} liberados");
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
        await AnalyzeAsync();
    }

    [RelayCommand]
    private async Task CleanLinuxAsync()
    {
        if (Distro is not { } distro)
            return;
        var selected = LinuxItems.Where(i => i.IsChecked).ToList();
        if (selected.Count == 0 || !await ConfirmAsync($"Limpar a distro {distro}?", selected))
            return;
        var before = selected.Sum(i => i.Size ?? 0);
        IsBusy = true;
        try
        {
            await _maintenance.CleanLinuxAsync(distro, Home, selected.Select(i => i.Key));
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
            IsBusy = false;
            return;
        }
        IsBusy = false;
        await AnalyzeAsync();
        var after = selected.Sum(i => i.Size ?? 0);
        _notifier.Show($"Distro limpa: {Formatting.Bytes(Math.Max(0, before - after))} liberados");
    }

    private Task<bool> ConfirmAsync(string title, IReadOnlyList<CleanupItem> items)
    {
        var list = string.Join("\n", items.Select(i => $"• {i.Title} ({i.SizeText})"));
        var danger = items.Any(i => i.IsDangerous);
        return _dialogs.ConfirmAsync(title,
            $"Vai ser apagado:\n{list}\n\n{(danger ? "Atenção: volumes guardam dados de bancos e aplicações. Isso não pode ser desfeito." : "Isso não pode ser desfeito.")}",
            "Limpar", danger: true);
    }

    [RelayCommand]
    private async Task FreeMemoryAsync()
    {
        if (Distro is not { } distro || !CanAnalyze)
            return;
        IsBusy = true;
        try
        {
            var freed = await _maintenance.FreeMemoryAsync(distro);
            _notifier.Show($"{Formatting.Bytes(freed)} de cache do Linux liberados");
            // Dá um tempo para a VM devolver as páginas ao Windows antes de medir de novo.
            await Task.Delay(3000);
            RefreshDiskAndMemory();
            PageCacheText = "—";
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CompactDiskAsync()
    {
        if (Distro is not { } distro)
            return;
        if (!await _dialogs.ConfirmAsync("Compactar o disco virtual?",
                $"O WSL vai ser desligado (todas as distros, containers e terminais param) e o Windows vai pedir permissão de administrador. "
                + $"Depois, o arquivo {Path.GetFileName(VhdPath ?? "ext4.vhdx")} devolve ao C: o espaço que já está livre dentro do Linux. Pode levar alguns minutos.",
                "Desligar e compactar"))
            return;
        IsBusy = true;
        try
        {
            _metrics.Stop();
            var (before, after) = await _maintenance.CompactDiskAsync(distro, _state.IsDistroRunning, _state.ShutdownWslAsync);
            _notifier.Show(after < before
                ? $"Disco compactado: {Formatting.Bytes(before)} → {Formatting.Bytes(after)} ({Formatting.Bytes(before - after)} devolvidos ao Windows)"
                : "O disco já estava compactado. Limpe a distro e o Docker primeiro para liberar mais espaço.");
            HasAnalyzed = false;
        }
        catch (OperationCanceledException ex)
        {
            _notifier.Show(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            IsBusy = false;
            RefreshDiskAndMemory();
        }
    }

    [RelayCommand]
    private async Task StartDistroAsync()
    {
        try
        {
            await _state.StartDistroAsync();
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }
}
