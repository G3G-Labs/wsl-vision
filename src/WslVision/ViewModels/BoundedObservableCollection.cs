using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WslVision.ViewModels;

/// <summary>
/// Lista com tamanho máximo (ex.: últimas linhas de log). Quando um lote passa do limite, as linhas mais antigas
/// saem todas de uma vez, com uma única notificação, em vez de uma remoção (e um aviso à interface) por linha.
/// </summary>
public sealed class BoundedObservableCollection<T>(int capacity) : ObservableCollection<T>
{
    public int Capacity { get; } = capacity;

    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
            return;
        if (Count + items.Count <= Capacity)
        {
            // Cabe tudo: avisos de inclusão normais, que a lista virtualizada trata sem refazer nada.
            foreach (var item in items)
                Add(item);
            return;
        }

        CheckReentrancy();
        var list = (List<T>)Items; // ObservableCollection() guarda os itens numa List<T>
        var keep = Math.Max(0, Capacity - items.Count);
        list.RemoveRange(0, list.Count - Math.Min(keep, list.Count));
        list.AddRange(items.Count > Capacity ? items.Skip(items.Count - Capacity) : items);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
