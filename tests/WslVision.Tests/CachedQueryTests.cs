using WslVision.Services;
using Xunit;

namespace WslVision.Tests;

public class CachedQueryTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task DentroDoPrazo_ReaproveitaOResultado()
    {
        var time = new FakeTime();
        var calls = 0;
        var q = new CachedQuery<int>(() => Task.FromResult(++calls), Ttl, time);

        Assert.Equal(1, await q.GetAsync());
        time.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(1, await q.GetAsync());
        time.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(2, await q.GetAsync());
    }

    [Fact]
    public async Task ChamadasDuranteAConsulta_EsperamAMesma()
    {
        var gate = new TaskCompletionSource<int>();
        var calls = 0;
        var q = new CachedQuery<int>(() => { calls++; return gate.Task; }, Ttl, new FakeTime());

        var a = q.GetAsync();
        var b = q.GetAsync();
        gate.SetResult(7);

        Assert.Equal(7, await a);
        Assert.Equal(7, await b);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OPrazoContaDoFimDaConsulta()
    {
        var time = new FakeTime();
        var gate = new TaskCompletionSource<int>();
        var calls = 0;
        var q = new CachedQuery<int>(() => { calls++; return gate.Task; }, Ttl, time);

        var first = q.GetAsync();
        time.Now += TimeSpan.FromSeconds(10); // a consulta demorou (ex.: wsl.exe lento)
        gate.SetResult(1);
        await first;
        time.Now += TimeSpan.FromSeconds(1);
        await q.GetAsync();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Falha_NaoFicaGuardada()
    {
        var calls = 0;
        var q = new CachedQuery<int>(() => ++calls == 1 ? Task.FromException<int>(new TimeoutException()) : Task.FromResult(calls), Ttl, new FakeTime());

        await Assert.ThrowsAsync<TimeoutException>(q.GetAsync);
        Assert.Equal(2, await q.GetAsync());
    }

    [Fact]
    public async Task Invalidate_ConsultaDeNovo()
    {
        var calls = 0;
        var q = new CachedQuery<int>(() => Task.FromResult(++calls), Ttl, new FakeTime());

        await q.GetAsync();
        q.Invalidate();

        Assert.Equal(2, await q.GetAsync());
    }

    [Fact]
    public async Task Invalidate_DuranteAConsulta_NaoReaproveitaOResultadoAntigo()
    {
        var gates = new List<TaskCompletionSource<int>>();
        var q = new CachedQuery<int>(() =>
        {
            var g = new TaskCompletionSource<int>();
            gates.Add(g);
            return g.Task;
        }, Ttl, new FakeTime());

        var old = q.GetAsync();
        q.Invalidate(); // ex.: a distro foi desligada enquanto a lista era consultada
        var fresh = q.GetAsync();
        gates[0].SetResult(1);
        gates[1].SetResult(2);
        await old;

        Assert.Equal(2, await fresh);
        Assert.Equal(2, await q.GetAsync());
        Assert.Equal(2, gates.Count);
    }
}
