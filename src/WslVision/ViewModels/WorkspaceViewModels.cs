using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Models;
using WslVision.Services;

namespace WslVision.ViewModels;

/// <summary>Estado de uma aba. A cor do ponto sai daqui: anel cinza, azul, verde e vermelho.</summary>
public enum TabState
{
    /// <summary>Não iniciada: nenhum processo e nenhum controle de terminal.</summary>
    Idle,

    /// <summary>Shell aberto, sem comando de inicialização rodando.</summary>
    Shell,

    /// <summary>O comando de inicialização está rodando.</summary>
    Running,

    /// <summary>O comando terminou com erro; o shell continua aberto.</summary>
    Error,
}

/// <summary>Uma aba do Terminal: a configuração salva (<see cref="Model"/>) e o estado de agora.</summary>
public sealed partial class WorkspaceTabViewModel : ObservableObject
{
    public WorkspaceTabViewModel(WorkspaceTab model, WorkspaceGroupViewModel? group)
    {
        Model = model;
        _group = group;
        _name = model.Name;
    }

    public WorkspaceTab Model { get; }

    /// <summary>Grupo da aba; null numa aba avulsa ("Abertas agora"), que não é salva.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoose), nameof(GroupName), nameof(GroupBrush), nameof(ResolvedFolder), nameof(IdleText))]
    private WorkspaceGroupViewModel? _group;

    public bool IsLoose => Group is null;

    public string GroupName => Group?.Name ?? "Abertas agora";

    public Brush GroupBrush => Group?.Brush ?? WorkspaceGroupViewModel.LooseBrush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private string _name;

    partial void OnNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Name = Model.Name.Length > 0 ? Model.Name : "aba";
            return;
        }
        Model.Name = value.Trim();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    public string Folder
    {
        get => Model.Folder;
        set => Set(Model.Folder, value, v => Model.Folder = v, nameof(Folder), nameof(ResolvedFolder), nameof(IdleText));
    }

    public string Startup
    {
        get => Model.Startup;
        set => Set(Model.Startup, value, v => Model.Startup = v, nameof(Startup), nameof(HasStartup), nameof(IdleText));
    }

    public bool Autostart
    {
        get => Model.Autostart;
        set => Set(Model.Autostart, value, v => Model.Autostart = v, nameof(Autostart));
    }

    public bool Root
    {
        get => Model.Root;
        set => Set(Model.Root, value, v => Model.Root = v, nameof(Root), nameof(AccessibleName));
    }

    public bool HasStartup => !string.IsNullOrWhiteSpace(Model.Startup);

    /// <summary>Projeto Compose escolhido à mão ("" na lista = automático, pela pasta da aba).</summary>
    public string ComposeProject
    {
        get => Model.ComposeProject ?? "";
        set => Set(Model.ComposeProject, string.IsNullOrWhiteSpace(value) ? null : value, v => Model.ComposeProject = v, nameof(ComposeProject));
    }

    /// <summary>O projeto Compose desta aba: o escolhido ou, sem escolha, o da pasta (null: nenhum).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompose), nameof(ComposeTip), nameof(ComposeLabel))]
    private string? _effectiveCompose;

    public bool HasCompose => EffectiveCompose is not null;

    /// <summary>"3/3": containers do projeto rodando, de todos.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposeTip), nameof(ComposeLabel))]
    private string _composeStatus = "";

    [ObservableProperty]
    private Tone _composeTone;

    public string ComposeLabel => HasCompose ? $"{EffectiveCompose} · {ComposeStatus}" : "";

    public string ComposeTip => HasCompose
        ? $"Projeto Compose {EffectiveCompose}: {ComposeStatus} containers rodando{(Model.ComposeProject is null ? " (achado pela pasta)" : "")}"
        : "";

    /// <summary>O que acontece ao iniciar, para a tela da aba parada.</summary>
    public string IdleText => $"Abre o shell em {ResolvedFolder}{(HasStartup ? $" e roda {Model.Startup}" : "")}. Abas paradas não gastam memória.";

    /// <summary>A pasta de verdade (as relativas partem da pasta do grupo), ainda com "~".</summary>
    public string ResolvedFolder => Group is null ? Model.Folder : WorkspaceStore.ResolveFolder(Group.Folder, Model.Folder);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsStarted), nameof(CanStop), nameof(StateLabel), nameof(SubText), nameof(HasSub), nameof(AccessibleName), nameof(StartLabel))]
    private TabState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubText), nameof(HasSub))]
    private int? _exitCode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubText), nameof(HasSub), nameof(HasBranch), nameof(AccessibleName))]
    private string? _branch;

    /// <summary>"●2 ↑1": alterações e commits à frente e atrás.</summary>
    [ObservableProperty]
    private string _gitText = "";

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>O terminal aberto agora (null enquanto a aba não foi iniciada).</summary>
    public TerminalSessionViewModel? Session { get; set; }

    /// <summary>Muda a cada início e a cada fim do terminal: um início que ficou esperando sabe que perdeu a vez.</summary>
    public int Generation { get; set; }

    /// <summary>Quando o terminal foi iniciado: o estado do comando anterior ainda pode estar no arquivo nos primeiros segundos.</summary>
    public DateTime StartedAt { get; set; }

    public bool IsIdle => State == TabState.Idle;

    public bool IsStarted => State != TabState.Idle;

    public bool CanStop => State == TabState.Running;

    public bool HasBranch => !string.IsNullOrEmpty(Branch);

    public string StartLabel => IsIdle ? "Iniciar" : "Reiniciar";

    public string StateLabel => State switch
    {
        TabState.Shell => "shell aberto",
        TabState.Running => "comando rodando",
        TabState.Error => ExitCode is { } c ? $"terminou com erro (código {c})" : "terminou com erro",
        _ => "não iniciada",
    };

    /// <summary>Segunda linha na barra lateral: o código do erro ou a branch.</summary>
    public string SubText => State == TabState.Error && ExitCode is { } code
        ? HasBranch ? $"código {code} · {Branch}" : $"código {code}"
        : Branch ?? "";

    public bool HasSub => SubText.Length > 0;

    public string AccessibleName => $"{Name}, {StateLabel}{(Root ? ", root" : "")}{(HasBranch ? $", branch {Branch}" : "")}";

    /// <summary>O grupo mudou de nome, cor ou pasta base.</summary>
    public void NotifyGroupChanged()
    {
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(GroupBrush));
        OnPropertyChanged(nameof(ResolvedFolder));
        OnPropertyChanged(nameof(IdleText));
    }

    /// <summary>Algo que é salvo mudou (nome, pasta, comando…).</summary>
    public event EventHandler? Edited;

    private void Set<T>(T current, T value, Action<T> apply, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;
        apply(value);
        foreach (var n in names)
            OnPropertyChanged(n);
        Edited?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Um workspace: um agrupamento de abas (ex.: um cliente ou produto), com nome, cor e uma pasta de partida opcional.
/// Os projetos são as abas: front-end e back-end, cada um na sua pasta e com o seu Compose, ficam no mesmo workspace.
/// </summary>
public sealed partial class WorkspaceGroupViewModel : ObservableObject
{
    public static readonly Brush LooseBrush = Frozen("#5F646C");

    public WorkspaceGroupViewModel(WorkspaceGroup model)
    {
        Model = model;
        _name = model.Name;
        _isExpanded = !model.Collapsed;
        Brush = Frozen(model.Color);
        Tabs.CollectionChanged += (_, _) => RefreshCount();
    }

    public WorkspaceGroup Model { get; }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];

    /// <summary>As cores que o grupo pode ter, para o seletor no painel do grupo.</summary>
    public IReadOnlyList<string> ColorChoices => WorkspaceColors.All;

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Name = Model.Name.Length > 0 ? Model.Name : "Workspace";
            return;
        }
        Model.Name = value.Trim();
        foreach (var t in Tabs)
            t.NotifyGroupChanged();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        Model.Collapsed = !value;
        Edited?.Invoke(this, EventArgs.Empty);
    }

    public Brush Brush { get; private set; }

    public string Color => Model.Color;

    public string Folder
    {
        get => Model.Folder;
        set
        {
            if (Model.Folder == value || string.IsNullOrWhiteSpace(value))
                return;
            Model.Folder = value.Trim();
            OnPropertyChanged();
            foreach (var t in Tabs)
                t.NotifyGroupChanged();
            Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool StartWithEnvironment
    {
        get => Model.StartWithEnvironment;
        set
        {
            if (Model.StartWithEnvironment == value)
                return;
            Model.StartWithEnvironment = value;
            OnPropertyChanged();
            Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>"2/4": abas iniciadas, de todas.</summary>
    public string CountText => $"{Tabs.Count(t => t.IsStarted)}/{Tabs.Count}";

    public string CountTip => $"{Tabs.Count(t => t.IsStarted)} de {Tabs.Count} abas iniciadas";

    /// <summary>Algum comando de aba rodando (o botão da bandeja vira "Parar").</summary>
    public bool AnyRunning => Tabs.Any(t => t.State == TabState.Running);

    /// <summary>Resumo para a bandeja: "2 de 4 abas rodando".</summary>
    public string Summary => Tabs.Count(t => t.State == TabState.Running) is var n and > 0
        ? $"{n} de {Tabs.Count} {(Tabs.Count == 1 ? "aba rodando" : "abas rodando")}"
        : Tabs.Any(t => t.IsStarted) ? "Abas abertas, nada rodando" : "Parado";

    public void RefreshCount()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CountTip));
        OnPropertyChanged(nameof(AnyRunning));
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void SetColor(string color)
    {
        if (Model.Color == color)
            return;
        Model.Color = color;
        Brush = Frozen(color);
        OnPropertyChanged(nameof(Brush));
        OnPropertyChanged(nameof(Color));
        foreach (var t in Tabs)
            t.NotifyGroupChanged();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Edited;

    private static Brush Frozen(string hex)
    {
        Brush b;
        try
        {
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch (FormatException)
        {
            b = new SolidColorBrush(Colors.SlateGray);
        }
        b.Freeze();
        return b;
    }
}
