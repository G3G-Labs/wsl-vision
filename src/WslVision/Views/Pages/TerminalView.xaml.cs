using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;
using Wpf.Ui.Appearance;
using WslVision.ViewModels;

namespace WslVision.Views.Pages;

/// <summary>
/// Terminais embutidos: um controle do Windows Terminal (ConPTY + renderização nativa) por aba.
/// Esta view é criada uma vez só pela janela e nunca recriada, para os terminais sobreviverem à troca de abas do app.
/// </summary>
public partial class TerminalView : UserControl
{
    private readonly Dictionary<TerminalSessionViewModel, EasyTerminalControl> _terminals = [];
    private TerminalViewModel? _vm;

    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TerminalViewModel);
        ApplicationThemeManager.Changed += (_, _) => ApplyTheme();
        // O app está fechando: os processos dos terminais (wsl.exe e o console) encerram junto.
        Dispatcher.ShutdownStarted += (_, _) =>
        {
            foreach (var term in _terminals.Values)
                Stop(term);
        };
    }

    private void Attach(TerminalViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Sessions.CollectionChanged -= OnSessionsChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
        }
        _vm = vm;
        if (vm is null)
            return;
        vm.Sessions.CollectionChanged += OnSessionsChanged;
        vm.PropertyChanged += OnViewModelChanged;
        foreach (var s in vm.Sessions)
            Add(s);
        ShowCurrent();
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TerminalSessionViewModel s in e.OldItems)
                Remove(s);
        if (e.NewItems is not null)
            foreach (TerminalSessionViewModel s in e.NewItems)
                Add(s);
        ShowCurrent();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.CurrentSession))
            ShowCurrent();
    }

    private void Add(TerminalSessionViewModel session)
    {
        if (_terminals.ContainsKey(session))
            return;
        var term = new EasyTerminalControl
        {
            StartupCommandLine = session.CommandLine,
            FontFamilyWhenSettingTheme = new FontFamily("Cascadia Mono"),
            FontSizeWhenSettingTheme = 12,
            Visibility = Visibility.Collapsed,
        };
        // O tema vale depois da fonte: o controle aplica os dois juntos.
        term.Theme = CurrentTheme();
        session.SendInput = text => term.ConPTYTerm?.WriteToTerm(text.AsSpan());
        _terminals[session] = term;
        TermHost.Children.Add(term);
    }

    private void Remove(TerminalSessionViewModel session)
    {
        if (!_terminals.Remove(session, out var term))
            return;
        session.SendInput = null;
        Stop(term);
        TermHost.Children.Remove(term);
    }

    private static void Stop(EasyTerminalControl term)
    {
        try { term.ConPTYTerm?.CloseStdinToApp(); } catch { /* já fechado */ }
        try { term.ConPTYTerm?.StopExternalTermOnly(); } catch { /* já fechado */ }
    }

    private void ShowCurrent()
    {
        var current = _vm?.CurrentSession;
        foreach (var (session, term) in _terminals)
            term.Visibility = session == current ? Visibility.Visible : Visibility.Collapsed;
        if (current is not null && _terminals.TryGetValue(current, out var visible))
            Dispatcher.BeginInvoke(() => visible.Focus(), DispatcherPriority.Input);
    }

    private void ApplyTheme()
    {
        var theme = CurrentTheme();
        foreach (var term in _terminals.Values)
            term.Theme = theme;
    }

    private static TerminalTheme CurrentTheme() =>
        ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light ? LightTheme : DarkTheme;

    // Campbell, o esquema padrão do Windows Terminal, com o fundo dos cartões do app (Wv.Surface).
    private static readonly TerminalTheme DarkTheme = Theme(0x17181B, 0xD4D7DC, 0x264F78,
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
    ]);

    // One Half Light: legível em fundo claro (o Campbell some no branco).
    private static readonly TerminalTheme LightTheme = Theme(0xFAFAFA, 0x383A42, 0xADD6FF,
    [
        0x383A42, 0xE45649, 0x50A14F, 0xC18301, 0x0184BC, 0xA626A4, 0x0997B3, 0xFAFAFA,
        0x4F525D, 0xDF6C75, 0x98C379, 0xE4C07A, 0x61AFEF, 0xC577DD, 0x56B5C1, 0xFFFFFF,
    ]);

    /// <summary>Cores em 0xRRGGBB; o controle usa COLORREF (0x00BBGGRR).</summary>
    private static TerminalTheme Theme(uint background, uint foreground, uint selection, uint[] palette) => new()
    {
        DefaultBackground = ToColorRef(background),
        DefaultForeground = ToColorRef(foreground),
        DefaultSelectionBackground = ToColorRef(selection),
        CursorStyle = CursorStyle.BlinkingBar,
        ColorTable = palette.Select(ToColorRef).ToArray(),
    };

    private static uint ToColorRef(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
}
