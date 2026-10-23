using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Services;

namespace WslVision.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private bool _windowVisible;

    public MainWindowViewModel(
        AppState state,
        Notifier notifier,
        ContainersViewModel containers,
        FilesViewModel files,
        MonitorViewModel monitor,
        TerminalViewModel terminal,
        MaintenanceViewModel maintenance,
        SettingsViewModel settings)
    {
        State = state;
        Notifier = notifier;
        Containers = containers;
        Files = files;
        Monitor = monitor;
        Terminal = terminal;
        Maintenance = maintenance;
        Settings = settings;
        // A Visão geral (o antigo Monitor, com o estado do ambiente) é a tela inicial.
        Pages = [monitor, containers, files, terminal, maintenance];
        _current = monitor;
        monitor.IsCurrent = true;
        // Uma aba pede para aparecer (terminal aberto de outra aba, logs escolhidos na paleta…).
        foreach (var page in Pages.Append(settings))
            page.ShowRequested += (s, _) => Current = (PageViewModel)s!;
        Palette = new PaletteViewModel(BuildPalette);
    }

    /// <summary>Paleta de comandos (Ctrl+K).</summary>
    public PaletteViewModel Palette { get; }

    /// <summary>O que cada aba oferece, e por último a navegação com os atalhos de teclado.</summary>
    private IEnumerable<PaletteItem> BuildPalette()
    {
        foreach (var item in Pages.SelectMany(p => p.PaletteItems()))
            yield return item;
        var n = 1;
        foreach (var page in Pages)
            yield return GoTo(page, $"Ctrl {n++}");
        yield return GoTo(Settings, "Ctrl ,");
    }

    private PaletteItem GoTo(PageViewModel page, string keys) =>
        new(PaletteSection.GoTo, page.Title, null, page.Icon, () =>
        {
            Current = page;
            return Task.CompletedTask;
        })
        { Keys = keys };

    public AppState State { get; }
    public Notifier Notifier { get; }
    public ContainersViewModel Containers { get; }
    public FilesViewModel Files { get; }
    public MonitorViewModel Monitor { get; }
    public TerminalViewModel Terminal { get; }
    public MaintenanceViewModel Maintenance { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>As abas do menu lateral (Ajustes fica separado, no rodapé).</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty]
    private PageViewModel _current;

    partial void OnCurrentChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsCurrent = false;
            oldValue.Deactivate();
        }
        newValue.IsCurrent = true;
        if (_windowVisible)
            newValue.Activate();
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => Current = page;

    /// <summary>Janela apareceu: volta a consultar o estado e reativa a aba atual.</summary>
    public void OnWindowShown()
    {
        _windowVisible = true;
        State.StartPolling();
        Current.Activate();
    }

    /// <summary>Janela minimizada ou na bandeja: nada de consultas nem métricas em segundo plano.</summary>
    public void OnWindowHidden()
    {
        _windowVisible = false;
        State.StopPolling();
        Current.Deactivate();
    }
}
