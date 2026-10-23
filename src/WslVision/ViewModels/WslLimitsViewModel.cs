using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Models;
using WslVision.Services;

namespace WslVision.ViewModels;

/// <summary>
/// Limites do WSL no .wslconfig (memória, processadores e devolver memória ociosa), editados em Ajustes.
/// Salvar reinicia o WSL; desfazer volta o arquivo para antes da primeira alteração do app.
/// </summary>
public sealed partial class WslLimitsViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly MetricsService _metrics;
    private readonly WslConfigService _wslConfig;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly Notifier _notifier;
    private WslLimits _savedLimits = new(null, null, false);
    // O que os controles mostraram ao carregar (já com o padrão e os limites do PC aplicados).
    private WslLimits _shownLimits = new(null, null, false);
    private bool _loading;

    public WslLimitsViewModel(AppState state, MetricsService metrics, WslConfigService wslConfig, SettingsService settings,
        DialogService dialogs, Notifier notifier)
    {
        _state = state;
        _metrics = metrics;
        _wslConfig = wslConfig;
        _settings = settings;
        _dialogs = dialogs;
        _notifier = notifier;
        TotalRamGb = Math.Max(1, (int)Math.Round(MetricsService.GetTotalPhysicalMemory() / 1024d / 1024 / 1024));
        CpuCount = Environment.ProcessorCount;
    }

    public int TotalRamGb { get; }

    public int CpuCount { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryLimitText), nameof(IsDirty), nameof(IsRecommended))]
    private double _memoryLimitGb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProcessorsText), nameof(IsDirty), nameof(IsRecommended))]
    private double _processors;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(IsRecommended))]
    private bool _autoMemoryReclaim;

    [ObservableProperty]
    private bool _savedMessageVisible;

    /// <summary>Salvando e reiniciando o WSL (Ajustes mostra o progresso da aba).</summary>
    [ObservableProperty]
    private bool _isBusy;

    public string MemoryLimitText => $"{MemoryLimitGb:0} GB";

    public string ProcessorsText => $"{Processors:0} de {CpuCount}";

    public string RamHint => $"Este PC tem {TotalRamGb} GB de RAM. Sem limite, o WSL pode usar até metade.";

    public bool IsDirty =>
        (int)MemoryLimitGb != _shownLimits.MemoryGb
        || (int)Processors != _shownLimits.Processors
        || AutoMemoryReclaim != _shownLimits.AutoMemoryReclaim;

    public bool CanUndoConfig => _wslConfig.HasBackup;

    private int DefaultMemoryGb => Math.Max(1, TotalRamGb / 2);

    /// <summary>Limites sugeridos para este PC (veja <see cref="WslConfigService.Recommend"/>).</summary>
    public WslLimits Recommended => WslConfigService.Recommend(TotalRamGb, CpuCount);

    public string RecommendationTitle => $"Recomendado para este PC: {Recommended.MemoryGb} GB e {Recommended.Processors} processadores";

    public string RecommendationReason =>
        $"Um terço dos {CpuCount} processadores lógicos e cerca de um quarto dos {TotalRamGb} GB de RAM (de 4 a 8 GB). "
        + "Sobra folga para o Windows, e o Docker de desenvolvimento roda bem. Devolver memória ociosa fica ligado.";

    public bool IsRecommended =>
        (int)MemoryLimitGb == Recommended.MemoryGb && (int)Processors == Recommended.Processors && AutoMemoryReclaim;

    [RelayCommand]
    private void ApplyRecommended()
    {
        var r = Recommended;
        MemoryLimitGb = r.MemoryGb ?? MemoryLimitGb;
        Processors = r.Processors ?? Processors;
        AutoMemoryReclaim = r.AutoMemoryReclaim;
        _notifier.Show(IsDirty ? "Valores recomendados aplicados. Clique em Salvar e reiniciar o WSL para valer." : "Os limites já estão no recomendado.");
    }

    /// <summary>Lê o .wslconfig de novo (ao abrir Ajustes ou depois de desfazer).</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            _savedLimits = _wslConfig.Read();
            MemoryLimitGb = Math.Clamp(_savedLimits.MemoryGb ?? DefaultMemoryGb, 1, TotalRamGb);
            Processors = Math.Clamp(_savedLimits.Processors ?? CpuCount, 1, CpuCount);
            AutoMemoryReclaim = _savedLimits.AutoMemoryReclaim;
            _shownLimits = new WslLimits((int)MemoryLimitGb, (int)Processors, AutoMemoryReclaim);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            _loading = false;
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(CanUndoConfig));
        }
    }

    partial void OnMemoryLimitGbChanged(double value)
    {
        if (!_loading)
            SavedMessageVisible = false;
    }

    [RelayCommand]
    private async Task SaveLimits()
    {
        var ok = await _dialogs.ConfirmAsync("Aplicar os novos limites?",
            "Para aplicar, o WSL é desligado e ligado de novo. Containers e terminais abertos na distro param por alguns segundos.",
            "Salvar e reiniciar o WSL");
        if (!ok)
            return;
        IsBusy = true;
        try
        {
            // Controle que o usuário não mexeu mantém o valor original do arquivo (inclusive "sem limite").
            var limits = new WslLimits(
                (int)MemoryLimitGb == _shownLimits.MemoryGb ? _savedLimits.MemoryGb : (int)MemoryLimitGb,
                (int)Processors == _shownLimits.Processors ? _savedLimits.Processors : (int)Processors,
                AutoMemoryReclaim);
            _wslConfig.Write(limits, _savedLimits);
            _settings.RecordChange(ConfigChangeKind.WslConfigEdited, null);
            AppLog.Info("limites", $"Novos limites no .wslconfig: memória {limits.MemoryGb?.ToString() ?? "padrão"} GB, "
                + $"processadores {limits.Processors?.ToString() ?? "padrão"}, devolver memória ociosa {(limits.AutoMemoryReclaim ? "sim" : "não")}");
            // Nada pode consultar a distro durante o desligamento (isso a religaria).
            _metrics.Stop();
            var wasRunning = _state.IsDistroRunning;
            await _state.ShutdownWslAsync();
            if (wasRunning)
                await _state.StartDistroAsync();
            Load();
            SavedMessageVisible = true;
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
    private async Task UndoLimits()
    {
        if (!await _dialogs.ConfirmAsync("Desfazer alterações no .wslconfig?",
                "O arquivo volta a ser como era antes da primeira alteração feita pelo WSL Vision. Vale na próxima vez que o WSL ligar.",
                "Desfazer"))
            return;
        try
        {
            if (_wslConfig.RestoreBackup())
            {
                _settings.Update(s => s.Changes.RemoveAll(c => c.Kind == ConfigChangeKind.WslConfigEdited));
                _notifier.Show(".wslconfig restaurado. Desligue o WSL para aplicar.");
            }
            Load();
            SavedMessageVisible = false;
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }
}
