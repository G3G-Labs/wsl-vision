using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;
using Wpf.Ui.Appearance;
using WslVision.ViewModels;

namespace WslVision.Views.Pages;

/// <summary>
/// Terminais embutidos: um controle do Windows Terminal (ConPTY + renderização nativa) por aba iniciada.
/// Esta view é criada uma vez só pela janela e nunca recriada, para os terminais sobreviverem à troca de abas do app.
/// </summary>
public partial class TerminalView : UserControl
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private readonly Dictionary<TerminalSessionViewModel, EasyTerminalControl> _terminals = [];

    // O shell de uma aba pode terminar sozinho ("exit"): verifica de tempos em tempos, só enquanto há terminais.
    private readonly DispatcherTimer _exitWatch = new() { Interval = TimeSpan.FromSeconds(2) };
    private TerminalViewModel? _vm;

    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TerminalViewModel);
        ApplicationThemeManager.Changed += (_, _) => ApplyTheme();
        _exitWatch.Tick += (_, _) => CheckExited();
        // Com o foco dentro do terminal (uma janela nativa), o WPF não vê as teclas: os atalhos são lidos antes.
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadMessage;
        // O app está fechando: os processos dos terminais (wsl.exe e o console) encerram junto.
        Dispatcher.ShutdownStarted += (_, _) =>
        {
            ComponentDispatcher.ThreadPreprocessMessage -= OnThreadMessage;
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
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var s in _terminals.Keys.Except(_vm?.Sessions ?? []).ToList())
                Remove(s);
        }
        if (e.OldItems is not null)
            foreach (TerminalSessionViewModel s in e.OldItems)
                Remove(s);
        if (e.NewItems is not null)
            foreach (TerminalSessionViewModel s in e.NewItems)
                Add(s);
        _exitWatch.IsEnabled = _terminals.Count > 0;
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

    private void CheckExited()
    {
        if (_vm is null)
            return;
        foreach (var (session, term) in _terminals.ToList())
        {
            bool exited;
            try { exited = term.ConPTYTerm?.Process?.HasExited == true; }
            catch (InvalidOperationException) { exited = false; }
            if (exited)
                _vm.OnSessionExited(session);
        }
    }

    private void ShowCurrent()
    {
        var current = _vm?.CurrentSession;
        foreach (var (session, term) in _terminals)
            term.Visibility = session == current ? Visibility.Visible : Visibility.Collapsed;
        if (current is not null && _terminals.TryGetValue(current, out var visible))
            Dispatcher.BeginInvoke(() => visible.Focus(), DispatcherPriority.Input);
    }

    /// <summary>O terminal é uma janela nativa e ficaria por cima da paleta (Ctrl+K): some enquanto ela está aberta.</summary>
    public void HideTerminals(bool hide) => TermHost.Visibility = hide ? Visibility.Hidden : Visibility.Visible;

    /// <summary>
    /// Atalhos com o foco dentro de um terminal. O WPF não recebe essas teclas (vão direto para a janela nativa),
    /// então elas são lidas na fila de mensagens. Só as combinações do app; o resto (Ctrl+C, Ctrl+R, Ctrl+W…) é do shell.
    /// </summary>
    private void OnThreadMessage(ref MSG msg, ref bool handled)
    {
        if (handled || _vm is null || (msg.message != WM_KEYDOWN && msg.message != WM_SYSKEYDOWN) || !IsVisible)
            return;
        // Só teclas de uma janela filha da janela do app (o terminal). Na própria janela, o WPF e os KeyBindings da view
        // já tratam; e diálogos e o painel da bandeja são outras janelas.
        var main = Window.GetWindow(this) is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
        if (main == IntPtr.Zero || msg.hwnd == main || !IsChild(main, msg.hwnd))
            return;
        var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
        var mods = Keyboard.Modifiers;
        handled = (mods, key) switch
        {
            (ModifierKeys.Control | ModifierKeys.Shift, Key.T) => Run(_vm.AddTabCommand),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.W) => Run(_vm.CloseTabCommand),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.R) => Run(_vm.StartTabCommand),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.Enter) => Run(_vm.StartGroupCommand),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.N) => Run(_vm.AddGroupCommand),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.E) => Run(_vm.OpenInVsCodeCommand),
            (ModifierKeys.Control, Key.Tab) => Run(_vm.NextTabCommand),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.Tab) => Run(_vm.PreviousTabCommand),
            (ModifierKeys.Control, Key.K) => OpenPalette(),
            (ModifierKeys.Alt, >= Key.D1 and <= Key.D9) => GoTo(key - Key.D0),
            _ => false,
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    /// <summary>Roda o comando depois de a tecla ser consumida, fora do filtro de mensagens (ele pode abrir diálogos).</summary>
    private bool Run(ICommand command)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (command.CanExecute(null))
                command.Execute(null);
        });
        return true;
    }

    private bool GoTo(int n)
    {
        Dispatcher.BeginInvoke(() => _vm?.GoToTab(n));
        return true;
    }

    private bool OpenPalette()
    {
        if (Window.GetWindow(this)?.DataContext is MainWindowViewModel main)
            Dispatcher.BeginInvoke(() => main.Palette.OpenCommand.Execute(null));
        return true;
    }

    private void OnMoreClick(object sender, RoutedEventArgs e) => OpenMenu((FrameworkElement)sender);

    /// <summary>Os projetos Compose sem workspace, montados na hora (o menu fica com o estilo padrão do app).</summary>
    private void OnFromComposeClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { ContextMenu: { } menu } owner)
            return;
        menu.Items.Clear();
        foreach (var project in _vm.ComposeSuggestions)
            menu.Items.Add(new MenuItem { Header = project.Title, Command = _vm.AddGroupFromComposeCommand, CommandParameter = project, ToolTip = project.WorkingDirLabel });
        OpenMenu(owner);
    }

    /// <summary>Projetos Compose que ainda não têm aba no workspace aberto no painel.</summary>
    private void OnAddComposeTabClick(object sender, RoutedEventArgs e)
    {
        if (_vm?.EditingGroup is not { } group || sender is not FrameworkElement { ContextMenu: { } menu } owner)
            return;
        menu.Items.Clear();
        foreach (var project in _vm.ProjectsMissingFrom(group))
            menu.Items.Add(new MenuItem { Header = project.Title, Command = _vm.AddComposeTabCommand, CommandParameter = project, ToolTip = project.WorkingDirLabel });
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Todos os projetos já têm aba neste workspace", IsEnabled = false });
        OpenMenu(owner);
    }

    private static void OpenMenu(FrameworkElement owner)
    {
        if (owner.ContextMenu is not { } menu)
            return;
        menu.PlacementTarget = owner;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.DataContext = owner.DataContext;
        menu.IsOpen = true;
    }

    /// <summary>Enter confirma o nome da aba e devolve o foco ao terminal; Esc desiste.</summary>
    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        if (e.Key == Key.Escape)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        else if (e.Key != Key.Enter)
            return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
        ShowCurrent();
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
