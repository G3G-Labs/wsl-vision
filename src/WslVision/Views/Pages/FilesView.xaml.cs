using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WslVision.ViewModels;

namespace WslVision.Views.Pages;

public partial class FilesView : UserControl
{
    private Point? _dragStart;
    private FileItemViewModel? _dragItem;

    public FilesView()
    {
        InitializeComponent();
    }

    private FilesViewModel? Vm => DataContext as FilesViewModel;

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is { } item)
            Vm?.OpenCommand.Execute(item);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm?.Selected is { } item)
        {
            Vm.OpenCommand.Execute(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            Vm?.UpCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Clique direito seleciona o item antes de abrir o menu, como no Explorer.</summary>
    private void OnListRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm)
            vm.Selected = ItemUnder(e.OriginalSource);
    }

    // ---- Arrastar daqui para o Windows ----

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = ItemUnder(e.OriginalSource);
        _dragStart = _dragItem is null ? null : e.GetPosition(FileList);
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _dragItem is null || Vm is not { } vm)
            return;
        var pos = e.GetPosition(FileList);
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var paths = vm.GetDragPaths([_dragItem]);
        _dragStart = null;
        _dragItem = null;
        if (paths.Length == 0)
            return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy);
    }

    // ---- Arrastar do Windows para cá ----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var fromOutside = e.Data.GetDataPresent(DataFormats.FileDrop)
                          && e.Data.GetData(DataFormats.FileDrop) is string[] files
                          && !files.All(f => f.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase));
        e.Effects = fromOutside ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is { } vm && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var outside = files.Where(f => !f.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (outside.Length > 0)
                await vm.ImportAsync(outside);
        }
    }

    private async void OnStartDistro(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        try
        {
            await vm.State.StartDistroAsync();
            await vm.LoadAsync();
        }
        catch (Exception ex)
        {
            ((App)Application.Current).Notifier.Error(ex);
        }
    }

    private static FileItemViewModel? ItemUnder(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem { DataContext: FileItemViewModel item })
                return item;
        }
        return null;
    }
}
