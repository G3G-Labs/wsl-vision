using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace WslVision.Services;

/// <summary>Ícone e menu da bandeja: favoritos, VS Code, terminal, Docker e WSL sem abrir a janela.</summary>
public sealed class TrayService(
    AppState state,
    SettingsService settings,
    LauncherService launcher,
    DialogService dialogs,
    Notifier notifier) : IDisposable
{
    private TaskbarIcon? _icon;

    public event EventHandler? ShowWindowRequested;
    public event EventHandler? ExitRequested;

    public void Initialize(bool efficiencyMode = false)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "WSL Vision",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = new ContextMenu(),
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
        _icon.PreviewTrayContextMenuOpen += async (_, _) =>
        {
            BuildMenu();
            await state.RefreshAsync();
            BuildMenu();
        };
        BuildMenu();
        _icon.ForceCreate(enablesEfficiencyMode: efficiencyMode);
    }

    private void BuildMenu()
    {
        if (_icon?.ContextMenu is not { } menu)
            return;
        menu.Items.Clear();

        menu.Items.Add(new MenuItem { Header = $"WSL Vision · {state.StatusText}", IsEnabled = false });
        menu.Items.Add(new Separator());

        if (settings.Current.Distro is { } distro)
        {
            // Atalho principal: deixa tudo pronto para trabalhar com um clique.
            var startEnv = Item(state.IsStartingEnvironment ? state.EnvironmentStep! : "Iniciar ambiente", SymbolRegular.Play24,
                () => _ = StartEnvironmentAsync());
            startEnv.FontWeight = FontWeights.SemiBold;
            startEnv.IsEnabled = !state.IsStartingEnvironment;
            menu.Items.Add(startEnv);
            menu.Items.Add(new Separator());

            var favorites = settings.Current.Favorites;
            foreach (var fav in favorites)
            {
                var item = Item(LinuxPaths.Pretty(fav, settings.Current.LinuxHome), SymbolRegular.Folder24, null);
                item.Items.Add(Item("Abrir no VS Code", SymbolRegular.Code24, () => launcher.OpenInVsCode(distro, fav)));
                item.Items.Add(Item("Abrir terminal aqui", SymbolRegular.WindowConsole20, () => _ = Safe(() => launcher.OpenTerminalAsync(distro, fav))));
                item.Items.Add(Item("Abrir no Explorer", SymbolRegular.FolderOpen24, () => launcher.OpenInExplorer(distro, fav)));
                menu.Items.Add(item);
            }
            if (favorites.Count > 0)
                menu.Items.Add(new Separator());

            var home = settings.Current.LinuxHome ?? "/";
            menu.Items.Add(Item("Abrir terminal", SymbolRegular.WindowConsole20, () => _ = Safe(() => launcher.OpenTerminalAsync(distro, home))));
            menu.Items.Add(state.IsDockerUp
                ? Item("Desligar o Docker", SymbolRegular.Stop24, () => _ = Safe(() => state.SetDockerAsync(false)))
                : Item("Ligar o Docker", SymbolRegular.Play24, () => _ = Safe(() => state.SetDockerAsync(true))));
            if (state.IsDistroRunning)
                menu.Items.Add(Item("Desligar o WSL", SymbolRegular.Power24, () => _ = ShutdownWslAsync()));
            menu.Items.Add(new Separator());
        }

        var open = Item("Abrir o painel", SymbolRegular.Window24, () => ShowWindowRequested?.Invoke(this, EventArgs.Empty));
        open.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(open);
        menu.Items.Add(Item("Sair do WSL Vision", SymbolRegular.ArrowExit20, () => ExitRequested?.Invoke(this, EventArgs.Empty)));
    }

    private async Task StartEnvironmentAsync()
    {
        try
        {
            Notify(await state.StartEnvironmentAsync());
        }
        catch (Exception ex)
        {
            notifier.Error(ex);
            Notify($"Não foi possível iniciar o ambiente: {ex.Message}");
        }
    }

    /// <summary>Notificação do Windows (a janela pode estar escondida).</summary>
    public void Notify(string message) => _icon?.ShowNotification("WSL Vision", message);

    private async Task ShutdownWslAsync()
    {
        if (await dialogs.ConfirmAsync("Desligar o WSL?",
                "Todas as distros param, junto com os containers e terminais abertos nelas. A memória volta para o Windows.",
                "Desligar"))
            await Safe(state.ShutdownWslAsync);
    }

    private async Task Safe(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            notifier.Error(ex);
            _icon?.ShowNotification("WSL Vision", ex.Message);
        }
    }

    private MenuItem Item(string header, SymbolRegular icon, Action? onClick)
    {
        var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = icon } };
        if (onClick is not null)
        {
            item.Click += (_, _) =>
            {
                // A janela pode estar escondida: erros também aparecem como notificação do Windows.
                try
                {
                    onClick();
                }
                catch (Exception ex)
                {
                    notifier.Error(ex);
                    _icon?.ShowNotification("WSL Vision", ex.Message);
                }
            };
        }
        return item;
    }

    public void Dispose() => _icon?.Dispose();
}
