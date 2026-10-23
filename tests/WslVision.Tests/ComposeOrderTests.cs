using WslVision.Services.Docker;
using Xunit;

namespace WslVision.Tests;

public class ComposeOrderTests
{
    private static ComposeMember M(string service, string? dependsOn = null, string project = "obra") =>
        new($"id-{project}-{service}", project, service, dependsOn);

    private static List<List<string>> Names(List<List<ComposeMember>> waves) =>
        waves.Select(w => w.Select(m => m.Service!).OrderBy(s => s).ToList()).ToList();

    [Fact]
    public void ParseDependsOn_LeServicoECondicao()
    {
        var deps = ComposeOrder.ParseDependsOn("redis:service_started:false,db:service_healthy:true");
        Assert.Equal([new ComposeDependency("redis", "service_started"), new ComposeDependency("db", "service_healthy")], deps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ParseDependsOn_Vazio(string? label) => Assert.Empty(ComposeOrder.ParseDependsOn(label));

    [Fact]
    public void Waves_SegueODependsOn()
    {
        // Mesmo grafo do projeto real: frontend e nginx → app → redis e db.
        var waves = ComposeOrder.Waves(
        [
            M("frontend", "app:service_started:false"),
            M("nginx", "app:service_started:false"),
            M("app", "redis:service_started:false,db:service_started:false"),
            M("phpmyadmin", "db:service_started:false"),
            M("redis"),
            M("db"),
        ]);
        Assert.Equal([["db", "redis"], ["app", "phpmyadmin"], ["frontend", "nginx"]], Names(waves));
    }

    [Fact]
    public void Waves_DependenciaForaDaLista_NaoEspera()
    {
        // O db já está rodando (não está na lista): o app sobe na primeira onda.
        var waves = ComposeOrder.Waves([M("app", "db:service_started:false"), M("nginx", "app:service_started:false")]);
        Assert.Equal([["app"], ["nginx"]], Names(waves));
    }

    [Fact]
    public void Waves_ProjetosDiferentesNaoSeMisturam()
    {
        var waves = ComposeOrder.Waves([M("app", "db:service_started:false", "a"), M("db", project: "b")]);
        Assert.Single(waves);
    }

    [Fact]
    public void Waves_Ciclo_NaoTrava()
    {
        var waves = ComposeOrder.Waves([M("a", "b:service_started:false"), M("b", "a:service_started:false"), M("c")]);
        Assert.Equal([["c"], ["a", "b"]], Names(waves));
    }
}
