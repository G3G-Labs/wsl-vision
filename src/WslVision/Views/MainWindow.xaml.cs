using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WslVision.Services;
using WslVision.ViewModels;

namespace WslVision.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _vm;
    private readonly SettingsService _settings;

    // Depois de alguns segundos escondida, a janela devolve ao Windows a memória que não está usando.
    private readonly DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private double _appliedScale = double.NaN;

    public MainWindowViewModel ViewModel => _vm;

    /// <summary>Quando true, fechar a janela encerra o app de verdade (botão Sair da bandeja).</summary>
    public bool AllowClose { get; set; }

    public MainWindow(MainWindowViewModel vm, SettingsService settings)
    {
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        InitializeComponent();

        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);

        vm.State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.StatusText) or nameof(AppState.IsDockerUp))
                UpdateStatusDot();
        };
        vm.Notifier.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Notifier.IsError))
            {
                ToastIcon.Symbol = vm.Notifier.IsError ? SymbolRegular.ErrorCircle24 : SymbolRegular.CheckmarkCircle24;
                ToastIcon.Foreground = Brush(vm.Notifier.IsError ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush");
            }
        };
        ApplicationThemeManager.Changed += (_, _) => UpdateStatusDot();

        // Escala da interface: segue o "Tamanho do texto" do Windows ou o valor escolhido em Ajustes.
        settings.Changed += (_, _) => ApplyScale();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        ApplyScale();

        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            TrimMemory();
        };
        IsVisibleChanged += (_, _) => UpdateActivity();
        StateChanged += (_, _) => UpdateActivity();
        UpdateStatusDot();
        ToastIcon.Foreground = Brush("SystemFillColorSuccessBrush");
    }

    private void UpdateActivity()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            _trimTimer.Stop();
            _vm.OnWindowShown();
        }
        else
        {
            _vm.OnWindowHidden();
            _trimTimer.Start();
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Accessibility or UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(ApplyScale);
    }

    private void ApplyScale()
    {
        var scale = _settings.Current.UiScale > 0 ? _settings.Current.UiScale : WindowsTextScale();
        scale = Math.Clamp(scale, 1.0, 2.25);
        MinWidth = Math.Min(900 * scale, SystemParameters.WorkArea.Width);
        // Ajustes são salvos o tempo todo (favoritos, pastas recentes): só refaz o layout se a escala mudou.
        if (scale == _appliedScale)
            return;
        _appliedScale = scale;
        ContentRoot.LayoutTransform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);
    }

    /// <summary>Configurações › Acessibilidade › Tamanho do texto (100% a 225%).</summary>
    public static double WindowsTextScale()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
        return key?.GetValue("TextScaleFactor") is int v && v >= 100 ? v / 100.0 : 1.0;
    }

    private void UpdateStatusDot()
    {
        var s = _vm.State;
        StatusDot.Fill = Brush(!s.IsDistroRunning ? "SystemFillColorNeutralBrush"
            : s.IsDockerUp ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    public void BringToFront()
    {
        if (!IsVisible)
            H.NotifyIcon.WindowExtensions.Show(this, disableEfficiencyMode: true);
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _settings.Current.CloseToTray)
        {
            e.Cancel = true;
            // Na bandeja: esconde e liga o modo eficiência (prioridade baixa / EcoQoS no Windows 11).
            H.NotifyIcon.WindowExtensions.Hide(this, enableEfficiencyMode: true);
            return;
        }
        base.OnClosing(e);
        if (!AllowClose)
            Application.Current.Shutdown();
    }

    /// <summary>
    /// Compacta o heap e devolve as páginas ociosas ao Windows. É o que faz o app ocupar poucos MB
    /// no Gerenciador de Tarefas enquanto fica só na bandeja.
    /// </summary>
    private static void TrimMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, -1, -1);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);

    private void OnDismissToast(object sender, RoutedEventArgs e) => _vm.Notifier.Dismiss();
}
