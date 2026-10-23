using WslVision.Services.Docker;
using Xunit;

namespace WslVision.Tests;

public class DockerTextTests
{
    [Theory]
    [InlineData("running", "Up 2 hours", ContainerStatusKind.Running)]
    [InlineData("exited", "Exited (0) 3 days ago", ContainerStatusKind.Stopped)]
    [InlineData("exited", "Exited (1) 12 minutes ago", ContainerStatusKind.Error)]
    [InlineData("exited", "Exited (137) 5 seconds ago", ContainerStatusKind.Error)]
    [InlineData("created", "Created", ContainerStatusKind.Stopped)]
    [InlineData("dead", "Dead", ContainerStatusKind.Error)]
    [InlineData("restarting", "Restarting (1) 2 seconds ago", ContainerStatusKind.Restarting)]
    [InlineData("paused", "Up 1 hour (Paused)", ContainerStatusKind.Paused)]
    public void Classify(string state, string status, ContainerStatusKind expected) =>
        Assert.Equal(expected, DockerText.Classify(state, status));

    [Theory]
    [InlineData("running", "Up 2 hours", "2 horas")]
    [InlineData("running", "Up About an hour", "1 hora")]
    [InlineData("running", "Up 1 second", "1 segundo")]
    [InlineData("running", "Up Less than a second", "menos de 1 segundo")]
    [InlineData("running", "Up 5 days (healthy)", "5 dias · saudável")]
    [InlineData("running", "Up 3 minutes (unhealthy)", "3 minutos · com problema")]
    [InlineData("running", "Up 10 seconds (health: starting)", "10 segundos · verificando")]
    [InlineData("exited", "Exited (0) 3 days ago", "Parado há 3 dias")]
    [InlineData("exited", "Exited (1) 12 minutes ago", "Saiu há 12 minutos (código 1)")]
    [InlineData("exited", "Exited (0) About an hour ago", "Parado há 1 hora")]
    [InlineData("created", "Created", "Criado, nunca iniciado")]
    [InlineData("running", "", "—")]
    public void Uptime(string state, string status, string expected) =>
        Assert.Equal(expected, DockerText.Uptime(state, status));
}
