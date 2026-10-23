using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WslVision.Services;

/// <summary>Aviso curto no rodapé da janela (substitui caixas de diálogo para mensagens que não pedem decisão).</summary>
public sealed partial class Notifier : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isError;

    public Notifier()
    {
        _timer.Tick += (_, _) => Dismiss();
    }

    public void Show(string message, bool isError = false)
    {
        _timer.Stop();
        IsError = isError;
        Message = message;
        _timer.Interval = TimeSpan.FromSeconds(isError ? 9 : 5);
        _timer.Start();
    }

    public void Error(Exception ex) => Show(ex.Message, isError: true);

    public void Dismiss()
    {
        _timer.Stop();
        Message = null;
    }
}
