using System.Runtime.CompilerServices;
using WslVision.Services;
using Xunit;

namespace WslVision.Tests;

public class LiveStreamTests
{
    private static Task Post(Action a)
    {
        a();
        return Task.CompletedTask;
    }

    private static async IAsyncEnumerable<int> Items(Exception? failAtEnd, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 1; i <= 3; i++)
        {
            await Task.Yield();
            yield return i;
        }
        if (failAtEnd is not null)
            throw failAtEnd;
    }

    private static async Task<(List<int> Items, List<Exception?> Ends)> Run(Func<CancellationToken, IAsyncEnumerable<int>> source, CancellationToken ct = default)
    {
        var items = new List<int>();
        var ends = new List<Exception?>();
        await LiveStream.RunAsync(source, Post, i => { items.Add(i); return Task.CompletedTask; }, ends.Add, ct);
        return (items, ends);
    }

    [Fact]
    public async Task AcabouSozinho_AvisaSemErro()
    {
        var (items, ends) = await Run(ct => Items(null, ct));

        Assert.Equal([1, 2, 3], items);
        Assert.Equal([null], ends);
    }

    [Fact]
    public async Task Caiu_AvisaComOErro()
    {
        var error = new IOException("pipe fechado");
        var (items, ends) = await Run(ct => Items(error, ct));

        Assert.Equal(3, items.Count);
        Assert.Same(error, Assert.Single(ends));
    }

    [Fact]
    public async Task ClienteDescartado_TambemAvisaOFim()
    {
        // Antes, uma exceção fora da lista do catch pulava a limpeza e o fluxo nunca mais religava.
        var (_, ends) = await Run(ct => Items(new ObjectDisposedException("HttpClient"), ct));

        Assert.IsType<ObjectDisposedException>(Assert.Single(ends));
    }

    [Fact]
    public async Task FalhaAoAbrir_AvisaComOErro()
    {
        var (items, ends) = await Run(_ => throw new HttpRequestException("A distro está desligada."));

        Assert.Empty(items);
        Assert.IsType<HttpRequestException>(Assert.Single(ends));
    }

    [Fact]
    public async Task Cancelado_AvisaSemErroENaoEntregaMaisNada()
    {
        using var cts = new CancellationTokenSource();
        var items = new List<int>();
        var ends = new List<Exception?>();

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            yield return 2; // chega já com o cancelamento pedido: não pode aparecer
            await Task.Delay(Timeout.Infinite, ct);
        }

        await LiveStream.RunAsync(Source, Post, i =>
        {
            items.Add(i);
            cts.Cancel();
            return Task.CompletedTask;
        }, ends.Add, cts.Token);

        Assert.Equal([1], items);
        Assert.Equal([null], ends);
    }

    [Fact]
    public void Espera_DobraAteOMaximoEZera()
    {
        var b = new RetryBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        var delays = Enumerable.Range(0, 7).Select(_ => b.Next().TotalSeconds).ToList();
        Assert.Equal([1, 2, 4, 8, 16, 30, 30], delays);

        b.Reset();
        Assert.Equal(1, b.Next().TotalSeconds);
    }
}
