using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WslVision.Services;

/// <summary>Aviso curto no rodapé da janela (substitui caixas de diálogo para mensagens que não pedem decisão). Tudo vai também para o <see cref="AppLog"/>.</summary>
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
        if (isError)
            AppLog.Warn("aviso", message);
        else
            AppLog.Info("aviso", message);
        Display(message, isError);
    }

    /// <summary>Mostra só a mensagem; o log guarda o erro inteiro, com o stack trace.</summary>
    public void Error(Exception ex)
    {
        AppLog.Error("erro", ex);
        Display(ex.Message, isError: true);
    }

    public void Dismiss()
    {
        _timer.Stop();
        Message = null;
    }

    private void Display(string message, bool isError)
    {
        _timer.Stop();
        IsError = isError;
        Message = message;
        _timer.Interval = TimeSpan.FromSeconds(isError ? 9 : 5);
        _timer.Start();
    }
}
