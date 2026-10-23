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
        Pages = [containers, files, monitor, terminal, maintenance];
        _current = containers;
        containers.IsCurrent = true;
        // Terminal aberto por outra aba (pasta de um projeto, menu de arquivos): mostra a aba Terminal.
        terminal.ShowRequested += (_, _) => Current = terminal;
    }

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
