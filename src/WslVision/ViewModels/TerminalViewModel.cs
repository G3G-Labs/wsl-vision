using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WslVision.Models;
using WslVision.Services;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

public sealed partial class QuickCommandViewModel(QuickCommand model) : ObservableObject
{
    public QuickCommand Model { get; } = model;
    public string Name => Model.Name;
    public string Command => Model.Command;
    public string Folder => Model.Folder;
    public bool NeedsSudo => Model.Command.TrimStart().StartsWith("sudo ", StringComparison.Ordinal) || Model.Command.Contains("&& sudo ", StringComparison.Ordinal);
    public string SudoNote => NeedsSudo ? "· pede senha do sudo" : "";

    [ObservableProperty]
    private bool _justRan;
}

/// <summary>Uma aba de terminal embutido. O controle nativo fica na view; aqui só o que a interface precisa saber.</summary>
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

public sealed partial class TerminalViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly LauncherService _launcher;
    private readonly DialogService _dialogs;
    private readonly Notifier _notifier;

    public TerminalViewModel(SettingsService settings, LauncherService launcher, DialogService dialogs, Notifier notifier)
    {
        _settings = settings;
        _launcher = launcher;
        _dialogs = dialogs;
        _notifier = notifier;
        _folder = settings.Current.RecentFolders.FirstOrDefault() ?? "~";
        _newFolder = _folder;
        settings.Changed += (_, _) => Reload();
        Reload();
        if (settings.Current.QuickCommands.Count == 0 && !settings.Current.SetupCompleted)
            SeedExamples();
    }

    public override string Title => "Terminal";

    public override SymbolRegular Icon => SymbolRegular.WindowConsole20;

    public ObservableCollection<string> Recents { get; } = [];

    public ObservableCollection<QuickCommandViewModel> Commands { get; } = [];

    /// <summary>Abas de terminal abertas dentro do app.</summary>
    public ObservableCollection<TerminalSessionViewModel> Sessions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentSession))]
    private TerminalSessionViewModel? _currentSession;

    public bool HasSessions => Sessions.Count > 0;

    public bool HasNoSessions => Sessions.Count == 0;

    public bool HasCurrentSession => CurrentSession is not null;

    [ObservableProperty]
    private string _folder;

    [ObservableProperty]
    private bool _showForm;

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private string _newCommand = "";

    [ObservableProperty]
    private string _newFolder;

    public string? Distro => _settings.Current.Distro;

    public bool HasNoCommands => Commands.Count == 0;

    private void Reload()
    {
        Recents.Clear();
        foreach (var r in _settings.Current.RecentFolders)
            Recents.Add(LinuxPaths.Pretty(r, _settings.Current.LinuxHome));
        if (!Recents.Contains("~"))
            Recents.Add("~");

        Commands.Clear();
        foreach (var c in _settings.Current.QuickCommands)
            Commands.Add(new QuickCommandViewModel(c));
        OnPropertyChanged(nameof(HasNoCommands));
    }

    private void SeedExamples()
    {
        var home = "~";
        _settings.Current.QuickCommands.AddRange(
        [
            new QuickCommand { Name = "Subir ambiente", Command = "docker compose up -d", Folder = home },
            new QuickCommand { Name = "Derrubar ambiente", Command = "docker compose down", Folder = home },
            new QuickCommand { Name = "Limpar imagens sem uso", Command = "docker image prune", Folder = home },
            new QuickCommand { Name = "Atualizar pacotes", Command = "sudo apt update && sudo apt upgrade", Folder = home },
        ]);
        Reload();
    }

    private string Expand(string path) => LinuxPaths.Expand(path, _settings.Current.LinuxHome);

    [RelayCommand]
    private void PickRecent(string path) => Folder = path;

    [RelayCommand]
    private void Browse()
    {
        if (Distro is not { } distro)
            return;
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha uma pasta da distro",
            InitialDirectory = LinuxPaths.ToUnc(distro, Expand(Folder)),
        };
        if (dialog.ShowDialog() != true)
            return;
        var linux = LinuxPaths.FromUnc(distro, dialog.FolderName) ?? LinuxPaths.FromWindowsDrive(dialog.FolderName);
        if (linux is null)
        {
            _notifier.Show("Escolha uma pasta dentro da distro ou de uma unidade do Windows.", isError: true);
            return;
        }
        Folder = LinuxPaths.Pretty(linux, _settings.Current.LinuxHome);
    }

    [RelayCommand]
    private Task Open() => OpenEmbedded(Folder, asRoot: false, command: null);

    [RelayCommand]
    private async Task OpenAsRoot()
    {
        if (await _dialogs.ConfirmAsync("Abrir terminal como root?",
                "Um terminal como root pode alterar qualquer arquivo da distro. Use só quando precisar.",
                "Abrir como root", danger: true))
            await OpenEmbedded(Folder, asRoot: true, command: null);
    }

    /// <summary>A mesma pasta, mas no Windows Terminal (janela separada).</summary>
    [RelayCommand]
    private Task OpenExternal() => OpenAt(Folder, asRoot: false, command: null);

    /// <summary>Comando rápido numa aba nova, na pasta dele.</summary>
    [RelayCommand]
    private async Task Run(QuickCommandViewModel c)
    {
        await OpenEmbedded(c.Folder, asRoot: false, command: c.Command, title: c.Name);
        MarkRan(c);
    }

    /// <summary>Comando rápido na aba que está aberta, como se fosse digitado (sem trocar de pasta).</summary>
    [RelayCommand]
    private void RunHere(QuickCommandViewModel c)
    {
        if (CurrentSession?.SendInput is not { } send)
            return;
        send(c.Command + "\r");
        MarkRan(c);
    }

    private void MarkRan(QuickCommandViewModel c)
    {
        foreach (var other in Commands)
            other.JustRan = other.Model.Id == c.Model.Id;
    }

    [RelayCommand]
    private void SelectSession(TerminalSessionViewModel s) => CurrentSession = s;

    [RelayCommand]
    private void CloseSession(TerminalSessionViewModel s)
    {
        var index = Sessions.IndexOf(s);
        if (index < 0)
            return;
        Sessions.RemoveAt(index);
        if (CurrentSession == s)
            CurrentSession = Sessions.Count == 0 ? null : Sessions[Math.Min(index, Sessions.Count - 1)];
        NotifySessions();
    }

    partial void OnCurrentSessionChanged(TerminalSessionViewModel? oldValue, TerminalSessionViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
    }

    private void NotifySessions()
    {
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasNoSessions));
    }

    /// <summary>Abre uma aba de terminal embutido (usado também pelas outras abas, ex.: pasta de um projeto Compose).</summary>
    public async Task OpenSessionAsync(string linuxPath, string? command = null, string? title = null)
    {
        await OpenEmbedded(linuxPath, asRoot: false, command, title);
        RequestShow();
    }

    private async Task OpenEmbedded(string folder, bool asRoot, string? command, string? title = null)
    {
        if (Distro is not { } distro)
            return;
        try
        {
            var path = Expand(folder);
            var commandLine = await _launcher.PrepareEmbeddedTerminalAsync(distro, path, asRoot, command);
            var name = title ?? LinuxPaths.Pretty(path, _settings.Current.LinuxHome);
            var session = new TerminalSessionViewModel(asRoot ? $"{name} (root)" : name, commandLine, asRoot);
            Sessions.Add(session);
            CurrentSession = session;
            NotifySessions();
            _settings.RememberRecentFolder(path);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }

    [RelayCommand]
    private async Task Remove(QuickCommandViewModel c)
    {
        if (!await _dialogs.ConfirmAsync($"Remover \"{c.Name}\"?", "O atalho sai da lista. Nada é alterado na distro.", "Remover"))
            return;
        _settings.Update(s => s.QuickCommands.RemoveAll(q => q.Id == c.Model.Id));
    }

    [RelayCommand]
    private void ToggleForm()
    {
        ShowForm = !ShowForm;
        if (ShowForm)
            NewFolder = Folder;
    }

    [RelayCommand]
    private void AddCommand()
    {
        if (string.IsNullOrWhiteSpace(NewCommand))
        {
            _notifier.Show("Digite o comando.", isError: true);
            return;
        }
        var cmd = new QuickCommand
        {
            Name = string.IsNullOrWhiteSpace(NewName) ? NewCommand.Trim() : NewName.Trim(),
            Command = NewCommand.Trim(),
            Folder = string.IsNullOrWhiteSpace(NewFolder) ? "~" : NewFolder.Trim(),
        };
        _settings.Update(s => s.QuickCommands.Add(cmd));
        NewName = "";
        NewCommand = "";
        ShowForm = false;
    }

    private async Task OpenAt(string folder, bool asRoot, string? command, string? title = null)
    {
        if (Distro is not { } distro)
            return;
        try
        {
            var path = Expand(folder);
            await _launcher.OpenTerminalAsync(distro, path, asRoot, command, title);
            _settings.RememberRecentFolder(path);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }
}
