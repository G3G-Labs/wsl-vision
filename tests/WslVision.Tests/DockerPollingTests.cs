using WslVision.Services;
using Xunit;

namespace WslVision.Tests;

public class DockerPollingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static bool Ping(bool periodic, bool distroWasOn, bool dockerWasUp, double secondsSinceOffPing) =>
        DockerPolling.ShouldPing(periodic, distroWasOn, dockerWasUp, Now - TimeSpan.FromSeconds(secondsSinceOffPing), Now);

    [Fact]
    public void DockerParado_ConsultaPeriodica_EsperaOIntervalo()
    {
        Assert.False(Ping(periodic: true, distroWasOn: true, dockerWasUp: false, secondsSinceOffPing: 10));
        Assert.True(Ping(periodic: true, distroWasOn: true, dockerWasUp: false, secondsSinceOffPing: 30));
    }

    [Fact]
    public void AcaoDoUsuario_SemprePinga() =>
        Assert.True(Ping(periodic: false, distroWasOn: true, dockerWasUp: false, secondsSinceOffPing: 1));

    [Fact]
    public void DockerLigado_SemprePinga() =>
        Assert.True(Ping(periodic: true, distroWasOn: true, dockerWasUp: true, secondsSinceOffPing: 1));

    [Fact]
    public void DistroAcabouDeLigar_Pinga() =>
        Assert.True(Ping(periodic: true, distroWasOn: false, dockerWasUp: false, secondsSinceOffPing: 1));

    [Fact]
    public void NuncaPingou_Pinga() =>
        Assert.True(DockerPolling.ShouldPing(true, true, false, DateTimeOffset.MinValue, Now));
}
