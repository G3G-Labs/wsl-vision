using WslVision.Models;
using WslVision.Services.Docker;
using Xunit;

namespace WslVision.Tests;

public class DockerEventTextTests
{
    private static ContainerEvent Ev(string action, string? service = "db", string? project = "obraclick", string? exitCode = null) =>
        new(action, "obraclick-db-1", service, project, exitCode, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("start", "db iniciado · obraclick", Tone.Ok)]
    [InlineData("stop", "db parado · obraclick", Tone.Off)]
    [InlineData("oom", "db ficou sem memória · obraclick", Tone.Error)]
    [InlineData("pause", "db pausado · obraclick", Tone.Work)]
    [InlineData("health_status: healthy", "db ficou saudável · obraclick", Tone.Ok)]
    [InlineData("health_status: unhealthy", "db com problema no healthcheck · obraclick", Tone.Work)]
    public void Describe_Acoes(string action, string text, Tone tone) =>
        Assert.Equal((text, tone), DockerText.Describe(Ev(action)));

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("137")]
    [InlineData("143")]
    public void Describe_SaidaNormal_NaoAparece(string? exitCode) =>
        Assert.Null(DockerText.Describe(Ev("die", exitCode: exitCode)));

    [Fact]
    public void Describe_SaidaComErro() =>
        Assert.Equal(("db saiu com erro (código 1) · obraclick", Tone.Error), DockerText.Describe(Ev("die", exitCode: "1")));

    [Fact]
    public void Describe_SemCompose_UsaONomeDoContainer() =>
        Assert.Equal(("obraclick-db-1 iniciado", Tone.Ok), DockerText.Describe(Ev("start", service: null, project: null)));

    [Theory]
    [InlineData("kill")]
    [InlineData("update")]
    public void Describe_Ruido_Ignorado(string action) =>
        Assert.Null(DockerText.Describe(Ev(action)));

    [Theory]
    [InlineData(ContainerStatusKind.Running, Tone.Ok)]
    [InlineData(ContainerStatusKind.Error, Tone.Error)]
    [InlineData(ContainerStatusKind.Partial, Tone.Work)]
    [InlineData(ContainerStatusKind.Restarting, Tone.Work)]
    [InlineData(ContainerStatusKind.Stopped, Tone.Off)]
    public void ToneOf(ContainerStatusKind kind, Tone tone) => Assert.Equal(tone, DockerText.ToneOf(kind));
}
