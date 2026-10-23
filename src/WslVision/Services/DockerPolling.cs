namespace WslVision.Services;

/// <summary>
/// Quando pingar o Docker na consulta periódica. Com a distro ligada e o Docker parado (o padrão: o Docker só liga
/// quando o usuário pede), cada ping abre um wsl.exe com o "dial-stdio" que morre na hora. Enquanto nada mudou, basta
/// conferir de vez em quando; ações do usuário, abrir a janela e abrir o painel sempre pingam.
/// </summary>
public static class DockerPolling
{
    public static readonly TimeSpan OffPingInterval = TimeSpan.FromSeconds(30);

    /// <param name="periodic">Consulta do timer (e não de uma ação do usuário).</param>
    /// <param name="wasDistroRunning">A distro já estava ligada na consulta anterior.</param>
    /// <param name="wasDockerUp">O Docker respondia na consulta anterior.</param>
    /// <param name="lastOffPing">Quando o último ping deu "desligado".</param>
    public static bool ShouldPing(bool periodic, bool wasDistroRunning, bool wasDockerUp, DateTimeOffset lastOffPing, DateTimeOffset now) =>
        !periodic || !wasDistroRunning || wasDockerUp || now - lastOffPing >= OffPingInterval;
}
