namespace WslVision.Services;

/// <summary>
/// Lê em segundo plano um fluxo ao vivo do Docker (eventos, logs). O fim é sempre avisado, uma vez, pelo
/// <c>post</c> (no app, o thread da interface): com o erro que derrubou o fluxo, ou null quando ele acabou sozinho ou
/// foi cancelado. Qualquer exceção conta, para quem usa poder sempre liberar o fluxo e decidir se religa.
/// </summary>
public static class LiveStream
{
    /// <param name="source">O fluxo, aberto com o token de cancelamento.</param>
    /// <param name="post">Executa uma ação no thread certo (no app, o Dispatcher).</param>
    /// <param name="onItem">Chamado em segundo plano para cada item, nunca depois do cancelamento.</param>
    /// <param name="onEnded">Chamado pelo <paramref name="post"/> quando o fluxo termina.</param>
    public static Task RunAsync<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> source,
        Func<Action, Task> post,
        Func<T, Task> onItem,
        Action<Exception?> onEnded,
        CancellationToken ct) =>
        Task.Run(async () =>
        {
            Exception? error = null;
            try
            {
                await foreach (var item in source(ct).WithCancellation(ct))
                {
                    // Trocou de container ou fechou a tela: o que ainda vinha no buffer não pode aparecer.
                    if (ct.IsCancellationRequested)
                        break;
                    await onItem(item);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = ex;
            }
            catch
            {
                // Cancelado por quem usa: não é erro.
            }
            try
            {
                await post(() => onEnded(error));
            }
            catch
            {
                // O app está fechando e o thread da interface já não recebe nada.
            }
        });
}

/// <summary>Espera crescente entre tentativas de religar (dobra a cada falha, até o máximo), zerada quando volta a funcionar.</summary>
public sealed class RetryBackoff(TimeSpan first, TimeSpan max)
{
    private int _failures;

    public TimeSpan Next()
    {
        var delay = first * Math.Pow(2, Math.Min(_failures, 20));
        _failures++;
        return delay < max ? delay : max;
    }

    public void Reset() => _failures = 0;
}
