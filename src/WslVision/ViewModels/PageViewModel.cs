using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

/// <summary>Uma aba da janela. Só trabalha (consulta Docker, lê métricas) enquanto está ativa e visível.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    public abstract SymbolRegular Icon { get; }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Aba escolhida no menu (mesmo com a janela escondida).</summary>
    [ObservableProperty]
    private bool _isCurrent;

    public void Activate()
    {
        if (IsActive)
            return;
        IsActive = true;
        OnActivated();
    }

    public void Deactivate()
    {
        if (!IsActive)
            return;
        IsActive = false;
        OnDeactivated();
    }

    protected virtual void OnActivated()
    {
    }

    protected virtual void OnDeactivated()
    {
    }
}
