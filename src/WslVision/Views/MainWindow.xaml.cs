using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WslVision.Services;
using WslVision.ViewModels;
using WslVision.Views.Layout;

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

        // O redesign é escuro de propósito: o tema e a cor de destaque são fixados no App, sem seguir o Windows.
        vm.Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaletteViewModel.IsOpen))
                TerminalPage.HideTerminals(vm.Palette.IsOpen);
            if (e.PropertyName == nameof(PaletteViewModel.IsOpen) && vm.Palette.IsOpen)
                Dispatcher.BeginInvoke(() => { PaletteInput.Focus(); Keyboard.Focus(PaletteInput); }, DispatcherPriority.Input);
            if (e.PropertyName == nameof(PaletteViewModel.Selected) && vm.Palette.Selected is { } sel)
                PaletteList.ScrollIntoView(sel);
        };

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
                ToastIcon.Foreground = Brush(vm.Notifier.IsError ? "Wv.Red" : "Wv.Green");
            }
        };

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
        ToastIcon.Foreground = Brush("Wv.Green");

        SearchButton.Loaded += (_, _) => CenterSearch();
        Brand.SizeChanged += (_, _) => CenterSearch();
        SizeChanged += (_, _) => CenterSearch();
    }

    /// <summary>
    /// O TitleBar põe o CenterContent logo depois do título, à esquerda. A margem leva a busca ao centro da janela,
    /// sem nunca invadir a marca e o status.
    /// </summary>
    private void CenterSearch()
    {
        if (!SearchButton.IsLoaded)
            return;
        // Janela estreita (meia tela): a busca vira só o ícone, para não cobrir a marca nem os botões da janela.
        var brandRight = Brand.TranslatePoint(new Point(Brand.ActualWidth, 0), this).X + 16;
        SetSearchCompact(ActualWidth - brandRight - CaptionButtonsWidth < FullSearchWidth + 16);
        var m = SearchButton.Margin;
        var slotLeft = SearchButton.TranslatePoint(new Point(0, 0), this).X - m.Left;
        var target = Math.Max((ActualWidth - SearchButton.Width) / 2, brandRight);
        var left = Math.Max(0, target - slotLeft);
        if (Math.Abs(left - m.Left) > 0.5)
            SearchButton.Margin = new Thickness(left, m.Top, m.Right, m.Bottom);
    }

    private const double FullSearchWidth = 340;

    // Minimizar, maximizar e fechar do TitleBar (46 px cada).
    private const double CaptionButtonsWidth = 3 * 46;

    private void SetSearchCompact(bool compact)
    {
        var width = compact ? 36 : FullSearchWidth;
        if (SearchButton.Width == width)
            return;
        SearchButton.Width = width;
        SearchButton.Padding = compact ? new Thickness(0) : new Thickness(10, 0, 8, 0);
        SearchButton.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        SearchIcon.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        SearchText.Visibility = SearchHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
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
        // A 100%, cabe em meia tela de 1366 px (Snap); as telas se reorganizam abaixo de 1100 e de 840 de largura útil.
        MinWidth = Math.Min(LayoutMath.WindowMinWidth * scale, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(520 * scale, SystemParameters.WorkArea.Height);
        // Ajustes são salvos o tempo todo (favoritos, pastas recentes): só refaz o layout se a escala mudou.
        if (scale == _appliedScale)
            return;
        _appliedScale = scale;
        var transform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);
        ContentRoot.LayoutTransform = transform;
        // A paleta fica fora do ContentRoot (cobre a barra de título), mas acompanha o tamanho da interface.
        PaletteRoot.LayoutTransform = transform;
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
        StatusDot.Fill = Brush(!s.IsDistroRunning ? "Wv.Gray" : s.IsDockerUp ? "Wv.Green" : "Wv.Amber");
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

    // ---- Paleta de comandos ----

    private void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _vm.Palette.Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                _vm.Palette.Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = _vm.Palette.RunAsync(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.Palette.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(PaletteList, d) is ListBoxItem { DataContext: PaletteItem item })
            _ = _vm.Palette.RunAsync(item);
    }

    private void OnPaletteBackdrop(object sender, MouseButtonEventArgs e) => _vm.Palette.Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Esc fecha a paleta mesmo com o foco fora da caixa de busca.
        if (e.Key == Key.Escape && _vm.Palette.IsOpen)
        {
            _vm.Palette.Close();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
