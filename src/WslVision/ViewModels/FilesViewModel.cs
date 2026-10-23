using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Models;
using WslVision.Services;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

public sealed partial class FileItemViewModel(LinuxFileEntry entry) : ObservableObject
{
    public LinuxFileEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public bool IsDirectory => Entry.Type == LinuxEntryType.Directory;
    public bool IsFile => !IsDirectory;
    public bool IsHidden => Entry.Name.StartsWith('.');
    public bool IsRootOwned => Entry.Owner == "root";
    public string Owner => Entry.Owner;
    public string Modified => Formatting.Date(Entry.Modified);
    public string Size => IsDirectory ? "—" : Formatting.Bytes(Entry.Size);
    public string? Note => Entry.IsSymlink ? "atalho" : Entry.Name == "node_modules" ? "pasta grande" : null;
}

public sealed record Crumb(string Name, string Path);

public sealed record Place(string Label, string Path, bool IsFavorite, bool IsLocked = false);

public sealed partial class FilesViewModel : PageViewModel
{
    private readonly AppState _state;
    private readonly SettingsService _settings;
    private readonly FileService _files;
    private readonly LauncherService _launcher;
    private readonly DialogService _dialogs;
    private readonly Notifier _notifier;
    private readonly TerminalViewModel _terminal;
    private readonly Stack<string> _back = new();
    private int _limit = FileService.PageSize;
    private bool _listAsRoot;
    private int _loadGeneration;
    private (string Path, bool Move)? _clipboard;
    private DirectoryListing? _listing;

    public FilesViewModel(AppState state, SettingsService settings, FileService files, LauncherService launcher, DialogService dialogs, Notifier notifier,
        TerminalViewModel terminal)
    {
        _state = state;
        _settings = settings;
        _files = files;
        _launcher = launcher;
        _dialogs = dialogs;
        _notifier = notifier;
        _terminal = terminal;
        _currentPath = settings.Current.LinuxHome ?? "/";
        settings.Changed += (_, _) => BuildPlaces();
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsDistroRunning) && IsActive && state.IsDistroRunning && !HasListing)
                _ = LoadAsync();
        };
        BuildPlaces();
    }

    public override string Title => "Arquivos";

    public override SymbolRegular Icon => SymbolRegular.Folder24;

    public override IEnumerable<PaletteItem> PaletteItems() => Favorites.Select(f =>
        new PaletteItem(PaletteSection.Folders, f.Path, "Favorito", SymbolRegular.Folder24, async () =>
        {
            RequestShow();
            await NavigateAsync(f.Path);
        }));

    public AppState State => _state;

    public ObservableCollection<FileItemViewModel> Items { get; } = [];

    public ObservableCollection<Crumb> Breadcrumbs { get; } = [];

    public ObservableCollection<Place> Favorites { get; } = [];

    public ObservableCollection<Place> Places { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowsPath), nameof(IsFavorite), nameof(PrettyPath))]
    private string _currentPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeLabel), nameof(CountLabel))]
    private FileItemViewModel? _selected;

    /// <summary>Formato do caminho mostrado e copiado na barra: Linux (/home/...) ou Windows (\\wsl.localhost\...).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinuxPath))]
    private bool _isWindowsPath;

    public bool IsLinuxPath
    {
        get => !IsWindowsPath;
        set => IsWindowsPath = !value;
    }

    /// <summary>A ação principal segue a seleção: pasta selecionada, senão a pasta aberta.</summary>
    public string CodeLabel => Selected is { IsDirectory: true } s ? $"Abrir {s.Name} no VS Code" : "Abrir no VS Code";

    public string CountLabel => Selected is { } s ? $"{StatusText} · {s.Name} selecionado" : StatusText;

    [ObservableProperty]
    private bool _isProtected;

    [ObservableProperty]
    private bool _isTruncated;

    [ObservableProperty]
    private bool _permissionDenied;

    [ObservableProperty]
    private bool _hasListing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HiddenToggleLabel), nameof(HiddenToggleIcon))]
    private bool _showHidden = true;

    public string HiddenToggleLabel => ShowHidden ? "Ocultar arquivos ocultos" : "Mostrar arquivos ocultos";

    public SymbolRegular HiddenToggleIcon => ShowHidden ? SymbolRegular.EyeOff24 : SymbolRegular.Eye24;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountLabel))]
    private string _statusText = "";

    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".msi", ".msp", ".scr", ".pif", ".lnk", ".url", ".reg", ".cpl", ".jar", ".appref-ms",
    };

    public string? Distro => _settings.Current.Distro;

    public string PrettyPath => LinuxPaths.Pretty(CurrentPath, _settings.Current.LinuxHome);

    public string WindowsPath => Distro is null ? CurrentPath : LinuxPaths.ToUnc(Distro, CurrentPath);

    public bool IsFavorite => _settings.Current.Favorites.Contains(CurrentPath);

    public bool HasClipboard => _clipboard is not null;

    public bool HasNoFavorites => Favorites.Count == 0;

    protected override void OnActivated()
    {
        if (!HasListing && _settings.Current.LinuxHome is { } home)
            CurrentPath = home;
        if (_state.IsDistroRunning && !HasListing)
            _ = LoadAsync();
    }

    private void BuildPlaces()
    {
        Favorites.Clear();
        foreach (var f in _settings.Current.Favorites)
            Favorites.Add(new Place(LinuxPaths.Name(f), f, true));
        OnPropertyChanged(nameof(HasNoFavorites));
        Places.Clear();
        if (_settings.Current.LinuxHome is { } home)
            Places.Add(new Place("Pasta pessoal", home, false));
        Places.Add(new Place("Raiz /", "/", false, IsLocked: true));
        Places.Add(new Place("/etc", "/etc", false, IsLocked: true));
        Places.Add(new Place("Windows (C:)", "/mnt/c", false));
        OnPropertyChanged(nameof(IsFavorite));
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (Distro is not { } distro)
            return;
        // Cada listagem tem um número: se o usuário já foi para outra pasta, o resultado antigo é descartado.
        var generation = ++_loadGeneration;
        IsBusy = true;
        try
        {
            var listing = await _files.ListAsync(distro, CurrentPath, _limit, _listAsRoot);
            if (generation != _loadGeneration)
                return;
            _listing = listing;
            ShowListing();
            IsProtected = !listing.Writable;
            PermissionDenied = false;
            HasListing = true;
        }
        catch (PermissionRequiredException) when (generation == _loadGeneration)
        {
            _listing = null;
            Items.Clear();
            PermissionDenied = true;
            HasListing = true;
            StatusText = "Sem permissão";
        }
        catch (Exception ex) when (generation == _loadGeneration)
        {
            _notifier.Error(ex);
        }
        catch
        {
            // Listagem antiga que falhou depois que o usuário mudou de pasta: ignora.
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsBusy = false;
                BuildBreadcrumbs();
            }
        }
    }

    /// <summary>Mostra a última listagem, aplicando o filtro de ocultos (sem consultar a distro de novo).</summary>
    private void ShowListing()
    {
        if (_listing is not { } listing)
            return;
        Items.Clear();
        foreach (var e in listing.Entries)
            if (ShowHidden || !e.Name.StartsWith('.'))
                Items.Add(new FileItemViewModel(e));
        IsTruncated = listing.Truncated;
        StatusText = $"{Items.Count}{(IsTruncated ? "+" : "")} {(Items.Count == 1 ? "item" : "itens")}";
    }

    private void BuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new Crumb(Distro ?? "/", "/"));
        var acc = "";
        foreach (var seg in CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc += "/" + seg;
            Breadcrumbs.Add(new Crumb(seg, acc));
        }
    }

    [RelayCommand]
    public async Task NavigateAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var target = LinuxPaths.Expand(path, _settings.Current.LinuxHome);
        if (target != CurrentPath)
            _back.Push(CurrentPath);
        CurrentPath = target;
        _limit = FileService.PageSize;
        _listAsRoot = false;
        Selected = null;
        await LoadAsync();
    }

    [RelayCommand]
    private Task GoToPlace(Place p) => NavigateAsync(p.Path);

    [RelayCommand]
    private Task GoToCrumb(Crumb c) => NavigateAsync(c.Path);

    [RelayCommand]
    private Task Up() => NavigateAsync(LinuxPaths.Parent(CurrentPath));

    [RelayCommand]
    private async Task Back()
    {
        if (_back.Count == 0)
            return;
        CurrentPath = _back.Pop();
        _limit = FileService.PageSize;
        // "Ver como root" vale só para a pasta em que foi confirmado.
        _listAsRoot = false;
        Selected = null;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task Open(FileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null || Distro is not { } distro)
            return;
        if (item.IsDirectory)
        {
            await NavigateAsync(item.FullPath);
            return;
        }
        // Abrir pelo Windows EXECUTA programas e scripts: só com confirmação.
        if (RunnableExtensions.Contains(Path.GetExtension(item.Name))
            && !await _dialogs.ConfirmAsync($"Executar {item.Name}?",
                "Este arquivo é um programa ou script do Windows. Abrir vai executá-lo no seu computador. Continue só se confiar na origem.",
                "Executar", danger: true))
            return;
        Try(() => _launcher.OpenWithWindows(distro, item.FullPath));
    }

    [RelayCommand]
    private async Task ShowMore()
    {
        _limit += FileService.PageSize;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ViewAsRoot()
    {
        if (!await _dialogs.ConfirmAsync("Ver como root?",
                $"A pasta {CurrentPath} não pode ser aberta pelo seu usuário. O app vai listá-la por dentro do Linux como root.",
                "Ver como root"))
            return;
        _listAsRoot = true;
        await LoadAsync();
    }

    [RelayCommand]
    private void ToggleHidden()
    {
        ShowHidden = !ShowHidden;
        ShowListing();
    }

    // ---- Ponte com o Windows ----

    private string TargetPath(FileItemViewModel? item) => item is { IsDirectory: true } ? item.FullPath : CurrentPath;

    [RelayCommand]
    private void OpenInVsCode(FileItemViewModel? item) =>
        Try(() => _launcher.OpenInVsCode(Distro!, TargetPath(item)), $"Abrindo {LinuxPaths.Pretty(TargetPath(item), _settings.Current.LinuxHome)} no VS Code");

    [RelayCommand]
    private async Task OpenTerminal(FileItemViewModel? item)
    {
        // Abre numa aba do terminal embutido (que já guarda a pasta nas recentes).
        await TryAsync(() => _terminal.OpenSessionAsync(TargetPath(item)));
    }

    [RelayCommand]
    private void OpenInExplorer(FileItemViewModel? item) => Try(() => _launcher.OpenInExplorer(Distro!, TargetPath(item)));

    [RelayCommand]
    private void CopyLinuxPath(FileItemViewModel? item)
    {
        var p = item?.FullPath ?? CurrentPath;
        Try(() => LauncherService.CopyText(p), $"Copiado: {p}");
    }

    /// <summary>Copia a pasta aberta no formato escolhido na barra de caminho.</summary>
    [RelayCommand]
    private void CopyCurrentPath()
    {
        if (IsWindowsPath)
            CopyWindowsPath(null);
        else
            CopyLinuxPath(null);
    }

    [RelayCommand]
    private void CopyWindowsPath(FileItemViewModel? item)
    {
        var p = LinuxPaths.ToUnc(Distro!, item?.FullPath ?? CurrentPath);
        Try(() => LauncherService.CopyText(p), $"Copiado: {p}");
    }

    [RelayCommand]
    private void ToggleFavorite(FileItemViewModel? item)
    {
        var path = TargetPath(item);
        var added = false;
        _settings.Update(s =>
        {
            if (!s.Favorites.Remove(path))
            {
                s.Favorites.Add(path);
                added = true;
            }
        });
        _notifier.Show(added ? $"{LinuxPaths.Name(path)} fixado nos favoritos e no menu da bandeja" : $"{LinuxPaths.Name(path)} removido dos favoritos");
    }

    // ---- Operações de arquivo ----

    [RelayCommand]
    private async Task NewFolder()
    {
        var name = await _dialogs.PromptAsync("Nova pasta", $"Nome da pasta em {PrettyPath}:", "nova-pasta", "Criar");
        if (name is null)
            return;
        await WithRootFallback("criar a pasta", root => _files.CreateFolderAsync(Distro!, CurrentPath, name, root));
    }

    [RelayCommand]
    private async Task Rename(FileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null)
            return;
        var name = await _dialogs.PromptAsync("Renomear", $"Novo nome para {item.Name}:", item.Name, "Renomear");
        if (name is null || name == item.Name)
            return;
        await WithRootFallback("renomear", root => _files.RenameAsync(Distro!, item.FullPath, name, _settings.Current.LinuxHome, root));
    }

    [RelayCommand]
    private async Task Delete(FileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null)
            return;
        var what = item.IsDirectory ? "a pasta" : "o arquivo";
        if (!await _dialogs.ConfirmAsync($"Excluir {item.Name}?",
                $"Isto apaga {what} {item.FullPath} de forma definitiva. No Linux não existe lixeira.",
                "Excluir", danger: true))
            return;
        await WithRootFallback("excluir", root => _files.DeleteAsync(Distro!, item.FullPath, _settings.Current.LinuxHome, root));
    }

    [RelayCommand]
    private void Copy(FileItemViewModel? item) => SetClipboard(item ?? Selected, move: false);

    [RelayCommand]
    private void Cut(FileItemViewModel? item) => SetClipboard(item ?? Selected, move: true);

    private void SetClipboard(FileItemViewModel? item, bool move)
    {
        if (item is null)
            return;
        _clipboard = (item.FullPath, move);
        OnPropertyChanged(nameof(HasClipboard));
        _notifier.Show(move ? $"{item.Name} pronto para mover. Abra a pasta de destino e clique em Colar." : $"{item.Name} copiado. Abra a pasta de destino e clique em Colar.");
    }

    [RelayCommand]
    private async Task Paste()
    {
        if (_clipboard is not { } clip)
            return;
        var ok = await WithRootFallback(clip.Move ? "mover" : "colar", root => _files.CopyOrMoveAsync(Distro!, clip.Path, CurrentPath, clip.Move, _settings.Current.LinuxHome, root));
        // Se mover falhou ou foi cancelado, o item continua recortado para tentar de novo.
        if (ok && clip.Move)
        {
            _clipboard = null;
            OnPropertyChanged(nameof(HasClipboard));
        }
    }

    /// <summary>Arquivos arrastados do Windows para a janela.</summary>
    public async Task ImportAsync(string[] windowsPaths)
    {
        var count = 0;
        foreach (var p in windowsPaths)
        {
            var ok = await WithRootFallback("copiar", root => _files.ImportFromWindowsAsync(Distro!, p, CurrentPath, root), reload: false, quiet: true);
            if (ok)
                count++;
        }
        if (count > 0)
            _notifier.Show(count == 1 ? $"{Path.GetFileName(windowsPaths[0])} copiado para {PrettyPath}" : $"{count} itens copiados para {PrettyPath}");
        await LoadAsync();
    }

    /// <summary>Caminhos \\wsl.localhost\... para arrastar itens daqui para o Explorer.</summary>
    public string[] GetDragPaths(IEnumerable<FileItemViewModel> items) =>
        Distro is null ? [] : items.Select(i => LinuxPaths.ToUnc(Distro, i.FullPath)).ToArray();

    /// <summary>
    /// Tenta como o usuário; se faltar permissão, avisa e, com confirmação explícita, refaz como root dentro do Linux.
    /// </summary>
    private async Task<bool> WithRootFallback(string verb, Func<bool, Task> op, bool reload = true, bool quiet = false)
    {
        if (Distro is null)
            return false;
        IsBusy = true;
        try
        {
            try
            {
                await op(false);
            }
            catch (PermissionRequiredException)
            {
                var ok = await _dialogs.ConfirmAsync("Permissão de root necessária",
                    $"Seu usuário não pode {verb} em {PrettyPath}. O app pode fazer a operação por dentro do Linux como root. Continue só se tiver certeza.",
                    "Fazer como root", danger: true);
                if (!ok)
                    return false;
                await op(true);
            }
            if (!quiet)
                _notifier.Show("Pronto");
            return true;
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
            if (reload)
                await LoadAsync();
        }
    }

    private void Try(Action a, string? done = null)
    {
        if (Distro is null)
            return;
        try
        {
            a();
            if (done is not null)
                _notifier.Show(done);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }

    private async Task TryAsync(Func<Task> a)
    {
        if (Distro is null)
            return;
        try
        {
            await a();
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }
}
