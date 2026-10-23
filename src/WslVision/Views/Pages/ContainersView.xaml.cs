using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WslVision.ViewModels;

namespace WslVision.Views.Pages;

public partial class ContainersView : UserControl
{
    private ContainersViewModel? _vm;
    private ScrollViewer? _logScroll;
    private bool _followLog = true;

    public ContainersView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ContainersViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as ContainersViewModel);
    }

    private void Attach(ContainersViewModel? vm)
    {
        if (_vm is not null)
            _vm.LogsAppended -= OnLogsAppended;
        _vm = vm;
        if (_vm is not null)
            _vm.LogsAppended += OnLogsAppended;
    }

    /// <summary>Acompanha o fim do log, a menos que o usuário tenha rolado para cima para ler.</summary>
    private void OnLogsAppended(object? sender, EventArgs e)
    {
        if (_logScroll is null && FindScrollViewer(LogList) is { } sv)
        {
            _logScroll = sv;
            sv.ScrollChanged += (_, args) =>
            {
                // Só rolagens do usuário (sem mudança no tamanho do conteúdo) mudam o modo "acompanhar".
                if (args.ExtentHeightChange == 0)
                    _followLog = sv.VerticalOffset >= sv.ScrollableHeight - 2;
            };
        }
        if (_followLog && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    /// <summary>Ctrl+F vai para a busca; F5 atualiza a lista.</summary>
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.F5 && _vm is not null)
        {
            _vm.RefreshCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv)
            return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } found)
                return found;
        return null;
    }
}
