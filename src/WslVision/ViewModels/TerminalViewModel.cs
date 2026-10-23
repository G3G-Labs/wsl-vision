using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WslVision.Models;
using WslVision.Services;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

/// <summary>Um terminal embutido aberto. O controle nativo fica na view; aqui só o que a interface precisa saber.</summary>
public sealed partial class TerminalSessionViewModel(string title, string commandLine, bool asRoot) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>Linha de comando do wsl.exe que a aba executa.</summary>
    public string CommandLine { get; } = commandLine;

    public bool AsRoot { get; } = asRoot;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Envia texto ao terminal como se fosse digitado. Ligado pela view quando o controle da aba é criado.</summary>
    public Action<string>? SendInput { get; set; }
}

/// <summary>
/// Terminal com workspaces: grupos de abas por projeto que continuam lá depois de fechar o app ou reiniciar o PC.
/// Cada aba guarda pasta, usuário e comando; só ganha processo quando é iniciada (abas paradas não gastam memória).
/// As abas abertas de fora (Containers, Arquivos, paleta) ficam em "Abertas agora" e não são salvas.
/// </summary>
public sealed partial class TerminalViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly LauncherService _launcher;
    private readonly DialogService _dialogs;
    private readonly Notifier _notifier;
    private readonly WorkspaceStore _store;
    private readonly WslService _wsl;
    private readonly ContainerInventory _inventory;
    private readonly AppState _state;

    // Abas com "iniciar automaticamente" que já subiram nesta sessão do app (elas esperam a distro ligar).
    private readonly HashSet<string> _autostarted = [];
    private CancellationTokenSource? _probeCts;
    private bool _probing;

    public TerminalViewModel(SettingsService settings, LauncherService launcher, DialogService dialogs, Notifier notifier,
        WorkspaceStore store, WslService wsl, ContainerInventory inventory, AppState state)
    {
        _settings = settings;
        _launcher = launcher;
        _dialogs = dialogs;
        _notifier = notifier;
        _store = store;
        _wsl = wsl;
        _inventory = inventory;
        _state = state;

        Load();
        // A gravação acontece fora do thread da interface; o aviso volta para ele.
        _store.SaveFailed += (_, ex) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            _notifier.Show($"Não foi possível salvar os workspaces agora ({ex.Message}). O app tenta de novo em alguns segundos.", isError: true));
        _inventory.Changed += (_, _) => RefreshCompose();
        _state.EnvironmentReady += async (_, _) => await StartEnvironmentGroupsAsync();
        _state.PropertyChanged += OnStateChanged;
        Groups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        Loose.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasLoose));
        };
    }

    public override string Title => "Terminal";

    public override SymbolRegular Icon => SymbolRegular.WindowConsole20;

    public ObservableCollection<WorkspaceGroupViewModel> Groups { get; } = [];

    /// <summary>Abas avulsas ("Abertas agora"): não são salvas até irem para um workspace.</summary>
    public ObservableCollection<WorkspaceTabViewModel> Loose { get; } = [];

    /// <summary>Terminais abertos (uma por aba iniciada). A view cria um controle nativo para cada um.</summary>
    public ObservableCollection<TerminalSessionViewModel> Sessions { get; } = [];

    /// <summary>Projetos do Compose que um workspace pode ligar ("" = nenhum).</summary>
    public ObservableCollection<string> ComposeProjects { get; } = [""];

    /// <summary>Projetos do Compose com pasta e ainda sem workspace: sugestões para criar um.</summary>
    public ObservableCollection<ContainerGroupViewModel> ComposeSuggestions { get; } = [];

    public bool HasLoose => Loose.Count > 0;

    public bool HasComposeSuggestions => ComposeSuggestions.Count > 0;

    public bool HasComposeProjects => _inventory.Groups.Any(p => p.IsCompose && p.HasWorkingDir);

    public bool IsEmpty => Groups.Count == 0 && Loose.Count == 0;

    public string? Distro => _settings.Current.Distro;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(StatusUser), nameof(StatusFolder), nameof(StatusCompose), nameof(HasStatusCompose))]
    private WorkspaceTabViewModel? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    private TerminalSessionViewModel? _currentSession;

    /// <summary>Painel de configuração da aba (pasta, comando, usuário, início automático).</summary>
    [ObservableProperty]
    private bool _showConfig;

    /// <summary>Workspace cujo painel de configuração está aberto (nome, cor, pasta, Compose).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingGroup))]
    private WorkspaceGroupViewModel? _editingGroup;

    public bool IsEditingGroup => EditingGroup is not null;

    /// <summary>Destino de "Guardar no workspace" para uma aba avulsa.</summary>
    [ObservableProperty]
    private WorkspaceGroupViewModel? _saveTarget;

    [ObservableProperty]
    private bool _sidebarCollapsed;

    partial void OnSidebarCollapsedChanged(bool value)
    {
        _store.Current.SidebarCollapsed = value;
        _store.ScheduleSave();
    }

    // Barra de status
    public string StatusUser => Selected?.Root == true ? "root" : _settings.Current.LinuxUser ?? "usuário";

    public string StatusFolder => Selected is null ? "" : Pretty(Expand(Selected.ResolvedFolder));

    public string StatusCompose => Selected?.ComposeLabel ?? "";

    public bool HasStatusCompose => StatusCompose.Length > 0;

    private IEnumerable<WorkspaceTabViewModel> AllTabs => Loose.Concat(Groups.SelectMany(g => g.Tabs));

    // ---- Carregar e salvar ----

    private void Load()
    {
        _store.Load();
        if (_store.LoadProblem is { } problem)
            _notifier.Show(problem, isError: true);

        // Primeira vez: os comandos rápidos antigos viram o grupo "Comandos rápidos". A lista antiga só sai
        // do settings.json depois que o workspaces.json foi gravado.
        if (_store.IsNew && _settings.Current.QuickCommands.Count > 0)
        {
            _store.Current.Groups.Add(WorkspaceStore.FromQuickCommands(_settings.Current.QuickCommands));
            try
            {
                _store.SaveNow();
                _settings.Update(s => s.QuickCommands.Clear());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fica para a próxima: os comandos continuam no settings.json.
            }
        }

        foreach (var g in _store.Current.Groups)
            Groups.Add(Wrap(g));
        SidebarCollapsed = _store.Current.SidebarCollapsed;
        Selected = AllTabs.FirstOrDefault(t => t.Model.Id == _store.Current.Selected) ?? Groups.SelectMany(g => g.Tabs).FirstOrDefault();
    }

    private WorkspaceGroupViewModel Wrap(WorkspaceGroup model)
    {
        var g = new WorkspaceGroupViewModel(model);
        g.Edited += (_, _) =>
        {
            Save();
            RefreshCompose();
        };
        foreach (var t in model.Tabs)
            g.Tabs.Add(WrapTab(t, g));
        return g;
    }

    private WorkspaceTabViewModel WrapTab(WorkspaceTab model, WorkspaceGroupViewModel? group)
    {
        var t = new WorkspaceTabViewModel(model, group);
        t.Edited += (_, _) =>
        {
            if (!t.IsLoose)
                Save();
            // A pasta ou a escolha do projeto mudou: o projeto Compose da aba pode ser outro.
            RefreshCompose();
        };
        t.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceTabViewModel.State))
                t.Group?.RefreshCount();
        };
        return t;
    }

    private void Save() => _store.ScheduleSave();

    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(StatusUser));
        OnPropertyChanged(nameof(StatusFolder));
        OnPropertyChanged(nameof(StatusCompose));
        OnPropertyChanged(nameof(HasStatusCompose));
    }

    partial void OnSelectedChanged(WorkspaceTabViewModel? oldValue, WorkspaceTabViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
        CurrentSession = newValue?.Session;
        SaveTarget = Groups.FirstOrDefault();
        if (newValue is { IsLoose: false })
        {
            _store.Current.Selected = newValue.Model.Id;
            Save();
        }
        _ = ProbeAsync();
    }

    partial void OnCurrentSessionChanged(TerminalSessionViewModel? oldValue, TerminalSessionViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
    }

    private string Expand(string path) => LinuxPaths.Expand(path, _settings.Current.LinuxHome);

    private string Pretty(string path) => LinuxPaths.Pretty(path, _settings.Current.LinuxHome);

    // ---- Abas ----

    [RelayCommand]
    private void SelectTab(WorkspaceTabViewModel tab) => Selected = tab;

    /// <summary>Inicia a aba (ou reinicia, se já estiver aberta).</summary>
    [RelayCommand]
    private Task StartTab(WorkspaceTabViewModel? tab) => (tab ?? Selected) is { } t ? StartTabsAsync([t]) : Task.CompletedTask;

    /// <summary>Clique duplo na barra lateral: seleciona e, se a aba estiver parada, inicia. Nunca reinicia o que está rodando.</summary>
    [RelayCommand]
    private Task OpenTab(WorkspaceTabViewModel tab)
    {
        Selected = tab;
        return tab.IsIdle ? StartTabsAsync([tab]) : Task.CompletedTask;
    }

    /// <summary>Envia Ctrl+C: para o comando e deixa o shell aberto.</summary>
    [RelayCommand]
    private void StopTab(WorkspaceTabViewModel? tab)
    {
        if ((tab ?? Selected) is not { Session.SendInput: { } send } t)
            return;
        send("\x03");
        if (t.State == TabState.Running)
            t.State = TabState.Shell;
    }

    /// <summary>Fecha o terminal da aba. A aba de um workspace continua salva (volta a "não iniciada"); a avulsa some.</summary>
    [RelayCommand]
    private void CloseTab(WorkspaceTabViewModel? tab)
    {
        if ((tab ?? Selected) is not { } t)
            return;
        EndSession(t);
        if (t.IsLoose)
            RemoveFromList(t);
    }

    [RelayCommand]
    private async Task RemoveTab(WorkspaceTabViewModel? tab)
    {
        if ((tab ?? Selected) is not { } t)
            return;
        if (t.IsLoose)
        {
            CloseTab(t);
            return;
        }
        if (!await _dialogs.ConfirmAsync($"Remover a aba \"{t.Name}\"?",
                t.IsStarted ? "O terminal dela é fechado, junto com o que estiver rodando nele. A pasta e os arquivos não mudam." : "A aba sai do workspace. A pasta e os arquivos não mudam.",
                "Remover"))
            return;
        EndSession(t);
        RemoveFromList(t);
    }

    private void RemoveFromList(WorkspaceTabViewModel t)
    {
        var list = t.Group?.Tabs ?? Loose;
        var index = list.IndexOf(t);
        list.Remove(t);
        t.Group?.Model.Tabs.Remove(t.Model);
        if (!t.IsLoose)
            Save();
        if (Selected == t)
            Selected = list.Count > 0 ? list[Math.Min(index, list.Count - 1)] : AllTabs.FirstOrDefault();
    }

    [RelayCommand]
    private void DuplicateTab(WorkspaceTabViewModel? tab)
    {
        if ((tab ?? Selected) is not { } t)
            return;
        var copy = new WorkspaceTab { Name = t.Name + " (cópia)", Folder = t.Folder, Startup = t.Startup, Autostart = false, Root = t.Root };
        var vm = WrapTab(copy, t.Group);
        if (t.Group is { } g)
        {
            g.Model.Tabs.Insert(g.Model.Tabs.IndexOf(t.Model) + 1, copy);
            g.Tabs.Insert(g.Tabs.IndexOf(t) + 1, vm);
            Save();
        }
        else
        {
            Loose.Insert(Loose.IndexOf(t) + 1, vm);
        }
        Selected = vm;
    }

    [RelayCommand]
    private void MoveTabUp(WorkspaceTabViewModel tab) => MoveTab(tab, -1);

    [RelayCommand]
    private void MoveTabDown(WorkspaceTabViewModel tab) => MoveTab(tab, +1);

    private void MoveTab(WorkspaceTabViewModel tab, int delta)
    {
        if (tab.Group is not { } g)
            return;
        var i = g.Tabs.IndexOf(tab);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= g.Tabs.Count)
            return;
        g.Tabs.Move(i, j);
        g.Model.Tabs.RemoveAt(i);
        g.Model.Tabs.Insert(j, tab.Model);
        Save();
    }

    /// <summary>Nova aba num workspace (o do item selecionado, se nenhum for indicado), na pasta base dele.</summary>
    [RelayCommand]
    private void AddTab(WorkspaceGroupViewModel? group)
    {
        group ??= Selected?.Group ?? Groups.FirstOrDefault();
        if (group is null)
        {
            _ = OpenLooseAsync();
            return;
        }
        var model = new WorkspaceTab { Name = "nova aba", Folder = "." };
        group.Model.Tabs.Add(model);
        var vm = WrapTab(model, group);
        group.Tabs.Add(vm);
        group.IsExpanded = true;
        Save();
        Selected = vm;
        EditingGroup = null;
        ShowConfig = true;
    }

    /// <summary>Terminal avulso na pasta pessoal (ou na pasta da aba selecionada).</summary>
    [RelayCommand]
    private Task OpenLoose() => OpenLooseAsync();

    private Task OpenLooseAsync() => OpenSessionAsync(Selected?.ResolvedFolder ?? "~");

    /// <summary>Guarda a aba avulsa selecionada no workspace escolhido: daí em diante ela é salva.</summary>
    [RelayCommand]
    private void SaveLoose()
    {
        if (Selected is not { IsLoose: true } t || SaveTarget is not { } g)
            return;
        Loose.Remove(t);
        t.Group = g;
        g.Model.Tabs.Add(t.Model);
        g.Tabs.Add(t);
        g.IsExpanded = true;
        g.RefreshCount();
        Save();
        Selected = t;
        _notifier.Show($"\"{t.Name}\" agora faz parte de {g.Name}.");
    }

    [RelayCommand]
    private void ToggleConfig()
    {
        EditingGroup = null;
        ShowConfig = !ShowConfig;
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    [RelayCommand]
    private void BrowseFolder()
    {
        if (Selected is not { } t || Distro is not { } distro)
            return;
        var picked = PickFolder(distro, t.ResolvedFolder);
        if (picked is not null)
            t.Folder = picked;
    }

    [RelayCommand]
    private void BrowseGroupFolder()
    {
        if (EditingGroup is not { } g || Distro is not { } distro)
            return;
        var picked = PickFolder(distro, g.Folder);
        if (picked is not null)
            g.Folder = picked;
    }

    private string? PickFolder(string distro, string start)
    {
        var dialog = new OpenFolderDialog { Title = "Escolha uma pasta da distro", InitialDirectory = LinuxPaths.ToUnc(distro, Expand(start)) };
        if (dialog.ShowDialog() != true)
            return null;
        var linux = LinuxPaths.FromUnc(distro, dialog.FolderName) ?? LinuxPaths.FromWindowsDrive(dialog.FolderName);
        if (linux is null)
        {
            _notifier.Show("Escolha uma pasta dentro da distro ou de uma unidade do Windows.", isError: true);
            return null;
        }
        return Pretty(linux);
    }

    [RelayCommand]
    private void OpenInVsCode()
    {
        if (Selected is { } t && Distro is { } distro)
            Try(() => _launcher.OpenInVsCode(distro, Expand(t.ResolvedFolder)));
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        if (Selected is { } t && Distro is { } distro)
            Try(() => _launcher.OpenInExplorer(distro, Expand(t.ResolvedFolder)));
    }

    [RelayCommand]
    private void CopyPath()
    {
        if (Selected is not { } t)
            return;
        Try(() =>
        {
            LauncherService.CopyText(Expand(t.ResolvedFolder));
            _notifier.Show("Caminho copiado.");
        });
    }

    /// <summary>A mesma aba, numa janela separada do Windows Terminal.</summary>
    [RelayCommand]
    private async Task OpenExternal()
    {
        if (Selected is not { } t || Distro is not { } distro)
            return;
        try
        {
            await _launcher.OpenTerminalAsync(distro, Expand(t.ResolvedFolder), t.Root, t.HasStartup ? t.Startup : null, t.Name);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }

    // Atalhos (Ctrl+Tab, Alt+1…9)

    [RelayCommand]
    private void NextTab() => Step(+1);

    [RelayCommand]
    private void PreviousTab() => Step(-1);

    private void Step(int delta)
    {
        var tabs = Loose.Concat(Groups.Where(g => g.IsExpanded).SelectMany(g => g.Tabs)).ToList();
        if (tabs.Count == 0)
            return;
        var i = Selected is null ? -1 : tabs.IndexOf(Selected);
        Selected = tabs[((i + delta) % tabs.Count + tabs.Count) % tabs.Count];
    }

    /// <summary>Alt+1…9: a aba n do workspace atual.</summary>
    public void GoToTab(int n)
    {
        var list = (IList<WorkspaceTabViewModel>?)Selected?.Group?.Tabs ?? Loose;
        if (n >= 1 && n <= list.Count)
            Selected = list[n - 1];
    }

    // ---- Iniciar e encerrar terminais ----

    /// <summary>
    /// Inicia (ou reinicia) as abas. Prepara todas num wsl.exe só e abre uma de cada vez, com um intervalo curto:
    /// muitos wsl.exe de uma vez já esgotaram os canais da VM do WSL.
    /// </summary>
    private async Task StartTabsAsync(IReadOnlyList<WorkspaceTabViewModel> tabs, bool confirmRoot = true)
    {
        if (Distro is not { } distro || tabs.Count == 0)
            return;
        // Cada aba guarda uma geração: se ela for fechada, removida ou iniciada de novo enquanto esta preparação espera,
        // esta aqui desiste dela (senão sobraria um terminal rodando sem aba).
        var generation = tabs.ToDictionary(t => t, t => ++t.Generation);
        var roots = tabs.Where(t => t.Root).ToList();
        if (confirmRoot && roots.Count > 0 && !await _dialogs.ConfirmAsync(
                roots.Count == 1 ? $"Abrir \"{roots[0].Name}\" como root?" : $"Abrir {roots.Count} abas como root?",
                "Um terminal como root pode alterar qualquer arquivo da distro. Use só quando precisar.",
                "Abrir como root", danger: true))
            tabs = tabs.Except(roots).ToList();
        if (tabs.Count == 0)
            return;

        IReadOnlyList<string> lines;
        try
        {
            var requests = tabs.Select(t => new TerminalRequest(Expand(t.ResolvedFolder), t.Root, t.HasStartup ? t.Startup : null, t.Model.Id)).ToList();
            lines = await _launcher.PrepareEmbeddedTerminalsAsync(distro, requests, _settings.Current.TerminalHelpers);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
            return;
        }

        for (var i = 0; i < tabs.Count; i++)
        {
            var t = tabs[i];
            if (t.Generation != generation[t])
                continue;
            EndSession(t);
            t.Generation = generation[t];
            var session = new TerminalSessionViewModel(t.Name, lines[i], t.Root);
            t.Session = session;
            t.StartedAt = DateTime.UtcNow;
            t.ExitCode = null;
            // Como root, o arquivo com o código de saída não é legível pelo app: o estado fica em "shell aberto".
            t.State = t.HasStartup && !t.Root ? TabState.Running : TabState.Shell;
            Sessions.Add(session);
            if (t == Selected)
                CurrentSession = session;
            if (i < tabs.Count - 1)
                await Task.Delay(250);
        }
        _ = ProbeAsync();
    }

    private void EndSession(WorkspaceTabViewModel t)
    {
        t.Generation++;
        if (t.Session is { } s)
        {
            Sessions.Remove(s);
            if (CurrentSession == s)
                CurrentSession = null;
        }
        t.Session = null;
        t.State = TabState.Idle;
        t.ExitCode = null;
    }

    /// <summary>O processo da aba terminou (ex.: "exit" no shell). Chamado pela view.</summary>
    public void OnSessionExited(TerminalSessionViewModel session)
    {
        var t = AllTabs.FirstOrDefault(x => x.Session == session);
        if (t is null)
        {
            Sessions.Remove(session);
            return;
        }
        EndSession(t);
        if (t.IsLoose)
            RemoveFromList(t);
        else
            _notifier.Show($"O terminal de \"{t.Name}\" foi fechado.");
    }

    /// <summary>Abre uma aba avulsa (usado pelas outras telas, ex.: pasta de um projeto Compose em Containers).</summary>
    public async Task OpenSessionAsync(string linuxPath, string? command = null, string? title = null)
    {
        // Pretty normaliza o caminho: "~/x" sem expandir viraria "/~/x".
        var folder = Pretty(Expand(linuxPath));
        var model = new WorkspaceTab { Name = title ?? folder, Folder = folder, Startup = command ?? "" };
        var t = WrapTab(model, null);
        Loose.Add(t);
        Selected = t;
        RequestShow();
        await StartTabsAsync([t]);
        if (t.IsStarted)
            _settings.RememberRecentFolder(Expand(linuxPath));
    }

    /// <summary>
    /// Terminal na pasta de um projeto Compose: se uma aba é desse projeto, mostra essa aba (e a inicia, se estiver
    /// parada); senão, abre uma aba avulsa na pasta.
    /// </summary>
    public async Task OpenProjectAsync(string project, string linuxPath)
    {
        RefreshCompose();
        var tabs = Groups.SelectMany(g => g.Tabs).Where(t => t.EffectiveCompose == project).ToList();
        if ((tabs.FirstOrDefault(t => t.IsStarted) ?? tabs.FirstOrDefault()) is { } tab)
        {
            if (tab.Group is { } g)
                g.IsExpanded = true;
            Selected = tab;
            RequestShow();
            if (tab.IsIdle)
                await StartTabsAsync([tab]);
            return;
        }
        await OpenSessionAsync(linuxPath, title: project);
    }

    /// <summary>Mostra o workspace na janela (usado pela bandeja).</summary>
    public void Show(WorkspaceGroupViewModel g)
    {
        g.IsExpanded = true;
        Selected = g.Tabs.FirstOrDefault(t => t.IsStarted) ?? g.Tabs.FirstOrDefault() ?? Selected;
        RequestShow();
    }

    // ---- Workspaces ----

    [RelayCommand]
    private async Task AddGroup()
    {
        var name = await _dialogs.PromptAsync("Novo workspace", "Nome do workspace (ex.: o cliente ou o produto)", "Novo workspace", "Criar");
        if (name is null)
            return;
        // O workspace é um agrupamento: a pasta dele não é a de um projeto. Parte da pasta acima da aba selecionada.
        var folder = Selected is { } t ? Pretty(LinuxPaths.Parent(Expand(t.ResolvedFolder))) : "~";
        var model = new WorkspaceGroup { Name = name, Color = WorkspaceColors.Next(Groups.Count), Folder = folder };
        var g = AddGroupModel(model);
        EditingGroup = g;
        ShowConfig = false;
    }

    /// <summary>Workspace a partir de um projeto Compose: uma aba na pasta do projeto; o workspace fica na pasta acima.</summary>
    [RelayCommand]
    private void AddGroupFromCompose(ContainerGroupViewModel project)
    {
        if (project.Project is not { } name || project.WorkingDir is not { } dir)
            return;
        var model = new WorkspaceGroup
        {
            Name = name,
            Color = WorkspaceColors.Next(Groups.Count),
            Folder = Pretty(LinuxPaths.Parent(dir)),
            Tabs = [new WorkspaceTab { Name = name, Folder = Pretty(dir) }],
        };
        var g = AddGroupModel(model);
        Selected = g.Tabs[0];
        EditingGroup = g;
        ShowConfig = false;
    }

    /// <summary>Mais um projeto no workspace aberto no painel (ex.: o back-end ao lado do front-end): uma aba na pasta dele.</summary>
    [RelayCommand]
    private void AddComposeTab(ContainerGroupViewModel project)
    {
        if ((EditingGroup ?? Selected?.Group) is not { } g || project.Project is not { } name || project.WorkingDir is not { } dir)
            return;
        var model = new WorkspaceTab { Name = name, Folder = Pretty(dir) };
        g.Model.Tabs.Add(model);
        var vm = WrapTab(model, g);
        g.Tabs.Add(vm);
        g.IsExpanded = true;
        Save();
        RefreshCompose();
        Selected = vm;
    }

    /// <summary>Projetos Compose com pasta que ainda não têm aba no workspace (para "Adicionar projeto").</summary>
    public IReadOnlyList<ContainerGroupViewModel> ProjectsMissingFrom(WorkspaceGroupViewModel g)
    {
        var present = g.Tabs.Select(t => t.EffectiveCompose).OfType<string>().ToHashSet();
        return _inventory.Groups.Where(p => p.IsCompose && p.HasWorkingDir && !present.Contains(p.Project!)).ToList();
    }

    private WorkspaceGroupViewModel AddGroupModel(WorkspaceGroup model)
    {
        _store.Current.Groups.Add(model);
        var g = Wrap(model);
        Groups.Add(g);
        Save();
        RefreshCompose();
        return g;
    }

    [RelayCommand]
    private void EditGroup(WorkspaceGroupViewModel g)
    {
        ShowConfig = false;
        EditingGroup = EditingGroup == g ? null : g;
    }

    [RelayCommand]
    private void CloseGroupEditor() => EditingGroup = null;

    [RelayCommand]
    private async Task RenameGroup(WorkspaceGroupViewModel g)
    {
        if (await _dialogs.PromptAsync("Renomear workspace", "Nome", g.Name, "Renomear") is { } name)
            g.Name = name;
    }

    [RelayCommand]
    private async Task RemoveGroup(WorkspaceGroupViewModel g)
    {
        var running = g.Tabs.Count(t => t.IsStarted);
        if (!await _dialogs.ConfirmAsync($"Remover o workspace \"{g.Name}\"?",
                running > 0
                    ? $"As {g.Tabs.Count} abas saem da lista e {(running == 1 ? "o terminal aberto é fechado" : $"os {running} terminais abertos são fechados")}. Pastas, arquivos e containers não mudam."
                    : $"As {g.Tabs.Count} abas saem da lista. Pastas, arquivos e containers não mudam.",
                "Remover", danger: true))
            return;
        foreach (var t in g.Tabs)
            EndSession(t);
        Groups.Remove(g);
        _store.Current.Groups.Remove(g.Model);
        if (EditingGroup == g)
            EditingGroup = null;
        if (Selected?.Group == g)
            Selected = AllTabs.FirstOrDefault();
        Save();
        RefreshCompose();
    }

    [RelayCommand]
    private void MoveGroupUp(WorkspaceGroupViewModel g) => MoveGroup(g, -1);

    [RelayCommand]
    private void MoveGroupDown(WorkspaceGroupViewModel g) => MoveGroup(g, +1);

    private void MoveGroup(WorkspaceGroupViewModel g, int delta)
    {
        var i = Groups.IndexOf(g);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Groups.Count)
            return;
        Groups.Move(i, j);
        _store.Current.Groups.RemoveAt(i);
        _store.Current.Groups.Insert(j, g.Model);
        Save();
    }

    /// <summary>
    /// Sobe o workspace: primeiro os containers do projeto ligado (se estiverem parados), depois as abas com comando
    /// ou com início automático. Abas como root ficam de fora (pedem confirmação uma a uma).
    /// </summary>
    [RelayCommand]
    private Task StartGroup(WorkspaceGroupViewModel? group) =>
        (group ?? Selected?.Group) is { } g ? StartGroupAsync(g, onlyIdle: false) : Task.CompletedTask;

    /// <param name="onlyIdle">
    /// Só as abas paradas (o "Iniciar ambiente" não pode fechar um shell em que você está digitando). Sem isso, as abas
    /// cujo comando já terminou ou foi parado são reiniciadas.
    /// </param>
    private async Task StartGroupAsync(WorkspaceGroupViewModel g, bool onlyIdle)
    {
        var projects = await StartComposeAsync(g);
        var tabs = g.Tabs.Where(t => !t.Root && (t.HasStartup || t.Autostart) && (onlyIdle ? t.IsIdle : t.State != TabState.Running)).ToList();
        if (tabs.Count == 0)
        {
            // No Iniciar ambiente, tudo já aberto não é novidade: só avisa quando foi um pedido direto.
            if (!onlyIdle)
                _notifier.Show(projects > 0 ? $"{g.Name}: containers no ar. Nenhuma aba com comando para iniciar." : $"{g.Name}: nenhuma aba com comando para iniciar.");
            return;
        }
        await StartTabsAsync(tabs, confirmRoot: false);
        _notifier.Show($"{g.Name}: {tabs.Count} {(tabs.Count == 1 ? "aba iniciada" : "abas iniciadas")}.");
    }

    /// <summary>Ctrl+C em todas as abas do workspace; se os projetos das abas estiverem no ar, oferece parar os containers.</summary>
    [RelayCommand]
    private async Task StopGroup(WorkspaceGroupViewModel? group)
    {
        if ((group ?? Selected?.Group) is not { } g)
            return;
        foreach (var t in g.Tabs.Where(t => t.State == TabState.Running))
            StopTab(t);
        var running = ProjectsOf(g).Where(p => p.AnyRunning).ToList();
        if (running.Count == 0)
            return;
        var names = string.Join(", ", running.Select(p => p.Title));
        if (!await _dialogs.ConfirmAsync(running.Count == 1 ? $"Parar também os containers de {names}?" : $"Parar também os containers de {running.Count} projetos?",
                $"Os comandos das abas já receberam Ctrl+C. Os containers de {names} continuam no ar até você pará-los.",
                "Parar containers"))
            return;
        foreach (var p in running)
            await _inventory.StopGroupCommand.ExecuteAsync(p);
    }

    /// <summary>Sobe os containers parados dos projetos das abas (cada aba tem o seu). Devolve quantos projetos o grupo tem.</summary>
    private async Task<int> StartComposeAsync(WorkspaceGroupViewModel g)
    {
        if (!_state.IsDockerUp)
        {
            if (g.Tabs.Any(t => t.HasCompose))
                _notifier.Show($"O Docker está desligado: as abas de {g.Name} sobem sem os containers. Use Iniciar ambiente para ligar tudo.", isError: true);
            return 0;
        }
        // A lista pode estar desatualizada (nenhuma tela de containers aberta): atualiza e recalcula os projetos das abas.
        await _inventory.RefreshAsync();
        RefreshCompose();
        var projects = ProjectsOf(g);
        foreach (var p in projects.Where(p => p.AnyStopped))
            await _inventory.StartGroupCommand.ExecuteAsync(p);
        return projects.Count;
    }

    /// <summary>Os projetos Compose das abas do workspace, sem repetir.</summary>
    private List<ContainerGroupViewModel> ProjectsOf(WorkspaceGroupViewModel g)
    {
        var names = g.Tabs.Select(t => t.EffectiveCompose).OfType<string>().ToHashSet();
        return _inventory.Groups.Where(p => p.Project is { } n && names.Contains(n)).ToList();
    }

    /// <summary>"Iniciar ambiente" terminou: sobem os workspaces marcados para isso.</summary>
    private async Task StartEnvironmentGroupsAsync()
    {
        foreach (var g in Groups.Where(g => g.StartWithEnvironment).ToList())
            await StartGroupAsync(g, onlyIdle: true);
    }

    private async void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.IsDistroRunning) && _state.IsDistroRunning)
            await StartAutostartAsync();
        else if (e.PropertyName == nameof(AppState.IsDockerUp))
            RefreshCompose();
    }

    /// <summary>
    /// As abas com "iniciar automaticamente" sobem assim que a distro estiver ligada: o app não liga a distro sozinho
    /// (o padrão em Ajustes é "só o WSL Vision"), então elas esperam por ela.
    /// </summary>
    private async Task StartAutostartAsync()
    {
        var tabs = Groups.SelectMany(g => g.Tabs).Where(t => t.Autostart && !t.Root && t.IsIdle && _autostarted.Add(t.Model.Id)).ToList();
        if (tabs.Count > 0)
            await StartTabsAsync(tabs, confirmRoot: false);
    }

    /// <summary>
    /// O projeto Compose de cada aba (o escolhido ou o da pasta dela) e o estado dos containers dele; a lista de
    /// projetos para escolher e as sugestões de workspace.
    /// </summary>
    private void RefreshCompose()
    {
        var projects = _inventory.Groups.Where(p => p.IsCompose).ToList();
        var folders = projects.Where(p => p.HasWorkingDir).Select(p => (p.Project!, p.WorkingDir!)).ToList();
        var tabs = AllTabs.ToList();
        foreach (var t in tabs)
        {
            t.EffectiveCompose = t.Model.ComposeProject ?? WorkspaceStore.ProjectFor(Expand(t.ResolvedFolder), folders);
            var p = t.EffectiveCompose is { } name ? projects.FirstOrDefault(x => x.Project == name) : null;
            var running = p?.All.Count(c => c.IsRunning) ?? 0;
            t.ComposeStatus = p is null ? (_state.IsDockerUp ? "não encontrado" : "Docker desligado") : $"{running}/{p.All.Count}";
            t.ComposeTone = p is null ? Tone.Off : running == p.All.Count ? Tone.Ok : running > 0 ? Tone.Work : Tone.Off;
        }

        // Os projetos escolhidos ficam na lista mesmo com o Docker desligado: sem isso, a escolha no painel da aba
        // ficaria vazia e voltaria para "automático" sem ninguém pedir.
        var names = projects.Select(p => p.Project!).Concat(tabs.Select(t => t.Model.ComposeProject).OfType<string>()).Distinct().ToList();
        foreach (var name in ComposeProjects.Skip(1).Except(names).ToList())
            ComposeProjects.Remove(name);
        foreach (var name in names.Except(ComposeProjects))
            ComposeProjects.Add(name);

        var covered = tabs.Select(t => t.EffectiveCompose).OfType<string>().ToHashSet();
        ComposeSuggestions.Clear();
        foreach (var p in projects.Where(p => p.HasWorkingDir && !covered.Contains(p.Project!)))
            ComposeSuggestions.Add(p);
        OnPropertyChanged(nameof(HasComposeSuggestions));
        OnPropertyChanged(nameof(HasComposeProjects));
        NotifyStatus();
    }

    // ---- Git e estado dos comandos ----

    protected override void OnActivated()
    {
        if (_state.IsDockerUp && !_inventory.HasLoaded)
            _ = _inventory.RefreshAsync();
        RefreshCompose();
        _probeCts = new CancellationTokenSource();
        _ = ProbeLoopAsync(_probeCts.Token);
    }

    protected override void OnDeactivated()
    {
        _probeCts?.Cancel();
        _probeCts = null;
    }

    /// <summary>Branch e estado dos comandos a cada 10 s, só com a tela do Terminal à vista.</summary>
    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            do
                await ProbeAsync();
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Saiu da tela.
        }
    }

    /// <summary>Uma consulta só (um wsl.exe) para todas as abas abertas.</summary>
    private async Task ProbeAsync()
    {
        if (!IsActive || _probing || Distro is not { } distro || !_state.IsDistroRunning)
            return;
        var tabs = AllTabs.Where(t => t.IsStarted).ToList();
        if (tabs.Count == 0)
            return;
        _probing = true;
        try
        {
            var input = WorkspaceProbe.Input(tabs.Select(t => (t.Model.Id, Expand(t.ResolvedFolder))));
            var r = await _wsl.RunScriptAsync(distro, WorkspaceProbe.Script, [input], timeout: TimeSpan.FromSeconds(10));
            var byId = WorkspaceProbe.Parse(r.StdOut).ToDictionary(p => p.TabId);
            foreach (var t in tabs)
            {
                if (!byId.TryGetValue(t.Model.Id, out var p))
                    continue;
                t.Branch = p.Branch;
                t.GitText = WorkspaceProbe.GitSummary(p.Changed, p.Ahead, p.Behind);
                // Logo depois de iniciar, o arquivo ainda pode ser o do comando anterior.
                if (t.State == TabState.Running && p.ExitCode is { } code && DateTime.UtcNow - t.StartedAt > TimeSpan.FromSeconds(3))
                {
                    t.ExitCode = code;
                    t.State = code is 0 or 130 ? TabState.Shell : TabState.Error;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A consulta é só informativa: se falhar, tenta de novo no próximo ciclo.
            Debug.WriteLine(ex);
        }
        finally
        {
            _probing = false;
        }
    }

    // ---- Paleta ----

    public override IEnumerable<PaletteItem> PaletteItems()
    {
        foreach (var g in Groups)
        {
            var running = g.Tabs.Any(t => t.State == TabState.Running);
            var projects = string.Join(", ", g.Tabs.Select(t => t.EffectiveCompose).OfType<string>().Distinct());
            yield return running
                ? PaletteItem.For(PaletteSection.Terminal, $"Parar workspace {g.Name}", "Ctrl+C nas abas", SymbolRegular.Stop24, StopGroupCommand, g)
                : PaletteItem.For(PaletteSection.Terminal, $"Iniciar workspace {g.Name}",
                    projects.Length > 0 ? $"Sobe {projects} e as abas com comando" : "Abas com comando", SymbolRegular.Play24, StartGroupCommand, g);
        }
        foreach (var t in AllTabs)
            yield return new PaletteItem(PaletteSection.Terminal, $"Ir para a aba {t.Name}", t.HasStartup ? $"{t.GroupName} · {t.Startup}" : t.GroupName,
                SymbolRegular.WindowConsole20, () =>
                {
                    Selected = t;
                    RequestShow();
                    return Task.CompletedTask;
                });
        if (Selected is { Session.SendInput: not null } current)
            foreach (var t in AllTabs.Where(t => t.HasStartup && t != current).DistinctBy(t => t.Startup))
                yield return new PaletteItem(PaletteSection.Terminal, $"Rodar {t.Startup} na aba atual", $"Comando de {t.Name}", SymbolRegular.Send24, () =>
                {
                    current.Session?.SendInput?.Invoke(t.Startup + "\r");
                    RequestShow();
                    return Task.CompletedTask;
                });
        yield return PaletteItem.For(PaletteSection.Terminal, "Novo terminal avulso", "Na pasta da aba selecionada", SymbolRegular.Add24, OpenLooseCommand);
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }
}
