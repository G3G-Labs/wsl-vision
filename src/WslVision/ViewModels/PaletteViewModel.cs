using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

/// <summary>Seções da paleta, na ordem em que aparecem.</summary>
public static class PaletteSection
{
    public const string Actions = "Ações";
    public const string Projects = "Projetos";
    public const string Containers = "Containers";
    public const string Terminal = "Terminal";
    public const string Folders = "Pastas";
    public const string GoTo = "Ir para";
}

/// <summary>Uma linha da paleta: o que aparece e o que acontece ao escolher.</summary>
public sealed record PaletteItem(string Section, string Label, string? Detail, SymbolRegular Icon, Func<Task> Run)
{
    /// <summary>Atalho de teclado mostrado à direita ("Ctrl 1").</summary>
    public string? Keys { get; init; }

    /// <summary>Destaca a ação principal do momento.</summary>
    public bool Accent { get; init; }

    /// <summary>Item que executa um comando (assíncrono ou não) com um parâmetro.</summary>
    public static PaletteItem For(string section, string label, string? detail, SymbolRegular icon, ICommand command, object? parameter = null) =>
        new(section, label, detail, icon, () =>
        {
            if (command is IAsyncRelayCommand asyncCommand)
                return asyncCommand.ExecuteAsync(parameter);
            command.Execute(parameter);
            return Task.CompletedTask;
        });

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool HasKeys => !string.IsNullOrEmpty(Keys);

    public string AccessibleName => HasDetail ? $"{Label}, {Detail}" : Label;
}

/// <summary>
/// Paleta de comandos (Ctrl+K): uma caixa de busca que encontra e executa ações, projetos, containers,
/// pastas e abas. As opções são montadas na hora, a partir do estado atual do app.
/// </summary>
public sealed partial class PaletteViewModel : ObservableObject
{
    private readonly Func<IEnumerable<PaletteItem>> _source;
    private List<PaletteItem> _all = [];

    public PaletteViewModel(Func<IEnumerable<PaletteItem>> source)
    {
        _source = source;
        ResultsView = CollectionViewSource.GetDefaultView(Results);
        ResultsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PaletteItem.Section)));
    }

    public ObservableCollection<PaletteItem> Results { get; } = [];

    public ICollectionView ResultsView { get; }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private PaletteItem? _selected;

    public bool IsEmpty => Results.Count == 0;

    partial void OnQueryChanged(string value) => Filter();

    [RelayCommand]
    public void Open()
    {
        _all = _source().ToList();
        Query = "";
        Filter();
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    private void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    /// <summary>Setas para cima/baixo mudam a seleção, pulando entre seções.</summary>
    public void Move(int delta)
    {
        var ordered = ResultsView.Cast<PaletteItem>().ToList();
        if (ordered.Count == 0)
            return;
        var i = Selected is null ? -1 : ordered.IndexOf(Selected);
        Selected = ordered[Math.Clamp(i + delta, 0, ordered.Count - 1)];
    }

    [RelayCommand]
    public async Task RunAsync(PaletteItem? item)
    {
        item ??= Selected;
        if (item is null)
            return;
        Close();
        await item.Run();
    }

    private void Filter()
    {
        // Todas as palavras precisam aparecer (em qualquer ordem) no rótulo, no detalhe ou na seção.
        var words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Results.Clear();
        foreach (var it in _all)
        {
            var hay = $"{it.Label} {it.Detail} {it.Section}";
            if (words.All(w => hay.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
                Results.Add(it);
        }
        Selected = ResultsView.Cast<PaletteItem>().FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
