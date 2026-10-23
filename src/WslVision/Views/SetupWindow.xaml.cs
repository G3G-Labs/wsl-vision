using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WslVision.ViewModels;

namespace WslVision.Views;

public partial class SetupWindow : FluentWindow
{
    public SetupWindow(SetupViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    /// <summary>true se o assistente terminou; false se a janela foi fechada antes.</summary>
    public bool Finished { get; set; }
}
