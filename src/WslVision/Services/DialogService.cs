using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace WslVision.Services;

/// <summary>Confirmações e perguntas no estilo do Windows 11. Exclusões e ações como root sempre passam por aqui.</summary>
public sealed class DialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false)
    {
        var box = Create(title, Text(message));
        box.PrimaryButtonText = confirmText;
        box.PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary;
        box.CloseButtonText = "Cancelar";
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    public async Task<string?> PromptAsync(string title, string label, string initial = "", string confirmText = "OK")
    {
        var input = new TextBox { Text = initial, MinWidth = 320, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(Text(label));
        panel.Children.Add(input);

        var box = Create(title, panel);
        box.PrimaryButtonText = confirmText;
        box.PrimaryButtonAppearance = ControlAppearance.Primary;
        box.CloseButtonText = "Cancelar";
        input.Loaded += (_, _) =>
        {
            input.Focus();
            // Seleciona só o nome, sem a extensão, como no Explorer.
            var dot = initial.LastIndexOf('.');
            input.Select(0, dot > 0 ? dot : initial.Length);
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                box.TemplateButtonCommand.Execute(Wpf.Ui.Controls.MessageBoxButton.Primary);
        };

        var result = await box.ShowDialogAsync();
        return result == MessageBoxResult.Primary && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }

    public async Task AlertAsync(string title, string message)
    {
        var box = Create(title, Text(message));
        box.CloseButtonText = "OK";
        await box.ShowDialogAsync();
    }

    private static MessageBox Create(string title, object content)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = content,
            MaxWidth = 520,
        };
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != box)
                    ?? Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            box.Owner = owner;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            box.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return box;
    }

    private static TextBlock Text(string s) => new() { Text = s, TextWrapping = TextWrapping.Wrap };
}
