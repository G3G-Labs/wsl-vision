using System.Collections.ObjectModel;

namespace WslVision.ViewModels;

public static class CollectionSync
{
    /// <summary>
    /// Deixa a coleção igual à lista, na mesma ordem, removendo, inserindo e movendo itens em vez de recriar:
    /// as linhas na tela não piscam nem perdem o hover.
    /// </summary>
    public static void SyncTo<T>(this ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        for (var i = target.Count - 1; i >= 0; i--)
            if (!source.Contains(target[i]))
                target.RemoveAt(i);
        for (var i = 0; i < source.Count; i++)
        {
            var cur = target.IndexOf(source[i]);
            if (cur < 0)
                target.Insert(i, source[i]);
            else if (cur != i)
                target.Move(cur, i);
        }
    }
}
