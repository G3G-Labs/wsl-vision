using WslVision.Models;
using WslVision.Services.Docker;

namespace WslVision.Services;

/// <summary>Passos do assistente de configuração. Toda alteração fica registrada para poder ser desfeita.</summary>
public sealed class SetupService(WslService wsl, SettingsService settings, LauncherService launcher)
{
    private const string TcpOverridePath = "/etc/systemd/system/docker.service.d/wslvision-tcp.conf";

    private const string DetectScript = """
        u="$(id -un)"
        echo "user=$u"
        echo "home=$HOME"
        if command -v dockerd >/dev/null 2>&1 && command -v docker >/dev/null 2>&1; then echo docker=1; else echo docker=0; fi
        if [ "$(ps -p 1 -o comm= 2>/dev/null)" = systemd ]; then
          echo systemd=1
          if systemctl is-enabled --quiet docker.service 2>/dev/null || systemctl is-enabled --quiet docker.socket 2>/dev/null; then echo enabled=1; else echo enabled=0; fi
          if systemctl is-active --quiet docker.service 2>/dev/null; then echo active=1; else echo active=0; fi
        else
          echo systemd=0; echo enabled=0
          if pgrep -x dockerd >/dev/null 2>&1; then echo active=1; else echo active=0; fi
        fi
        if getent group docker 2>/dev/null | cut -d: -f4 | tr ',' '\n' | grep -qx "$u"; then echo group=1; else echo group=0; fi
        """;

    public async Task<SetupStatus> DetectAsync(string distro)
    {
        var r = await wsl.RunScriptAsync(distro, DetectScript, timeout: TimeSpan.FromMinutes(2));
        if (!r.Success && r.StdOut.Length == 0)
            throw new InvalidOperationException(WslService.ErrorText(r, $"Não foi possível verificar a distro {distro}."));
        var kv = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => g.Last()[1]);
        bool B(string k) => kv.GetValueOrDefault(k) == "1";
        return new SetupStatus(
            kv.GetValueOrDefault("user", "root"),
            kv.GetValueOrDefault("home", "/root"),
            B("docker"), B("systemd"), B("enabled"), B("active"), B("group"));
    }

    /// <summary>Ativa o systemd no /etc/wsl.conf (com backup). A distro precisa ser reiniciada depois.</summary>
    public async Task EnableSystemdAsync(string distro)
    {
        await RunRoot(distro, """
            f=/etc/wsl.conf
            [ -f "$f" ] && [ ! -f "$f.wslvision.bak" ] && cp -p "$f" "$f.wslvision.bak"
            [ -f "$f" ] || { : > "$f"; touch "$f.wslvision.created"; }
            if grep -q '^\[boot\]' "$f"; then
              if sed -n '/^\[boot\]/,/^\[/p' "$f" | grep -q '^systemd='; then
                sed -i '/^\[boot\]/,/^\[/ s/^systemd=.*/systemd=true/' "$f"
              else
                sed -i '/^\[boot\]/a systemd=true' "$f"
              fi
            else
              printf '\n[boot]\nsystemd=true\n' >> "$f"
            fi
            """, "ativar o systemd");
        settings.RecordChange(ConfigChangeKind.SystemdEnabled, distro);
    }

    /// <summary>Coloca o usuário no grupo docker, para usar o Docker sem sudo.</summary>
    public async Task AddUserToDockerGroupAsync(string distro, string user)
    {
        await RunRoot(distro, """
            getent group docker >/dev/null 2>&1 || groupadd docker
            usermod -aG docker "$A1"
            """, "adicionar o usuário ao grupo docker", [user]);
        settings.RecordChange(ConfigChangeKind.UserAddedToDockerGroup, distro, user);
    }

    // Ligado se o serviço OU o socket estiver ativo: com o docker.socket ativo, a primeira conexão à API já liga o Docker.
    private const string AutostartQuery = """
        if [ "$(ps -p 1 -o comm= 2>/dev/null)" != systemd ]; then echo none; exit 0; fi
        if systemctl is-enabled --quiet docker.service 2>/dev/null || systemctl is-enabled --quiet docker.socket 2>/dev/null; then echo on; else echo off; fi
        """;

    /// <summary>O Docker liga sozinho quando a distro liga? null = a distro não usa systemd (só liga quando alguém pede).</summary>
    public async Task<bool?> GetDockerAutostartAsync(string distro)
    {
        var r = await wsl.RunScriptAsync(distro, AutostartQuery, timeout: TimeSpan.FromSeconds(30));
        return r.StdOut.Trim() switch
        {
            "on" => true,
            "off" => false,
            _ => null,
        };
    }

    /// <summary>
    /// Liga ou desliga o início automático do Docker (serviço e socket) no systemd. Não liga nem para o Docker agora:
    /// vale a partir da próxima vez que a distro ligar. Fica registrado para o "Desfazer tudo".
    /// </summary>
    public async Task SetDockerAutostartAsync(string distro, bool enabled)
    {
        if (enabled)
        {
            await RunRoot(distro, "systemctl enable docker.service docker.socket", "ativar o início automático do Docker");
            // Se foi o app que tinha desativado, isso só desfaz aquela alteração.
            if (!ForgetChange(ConfigChangeKind.DockerAutostartDisabled, distro))
                settings.RecordChange(ConfigChangeKind.DockerServiceEnabled, distro);
        }
        else
        {
            await RunRoot(distro, "systemctl disable docker.service docker.socket", "desativar o início automático do Docker");
            if (!ForgetChange(ConfigChangeKind.DockerServiceEnabled, distro))
                settings.RecordChange(ConfigChangeKind.DockerAutostartDisabled, distro);
        }
    }

    private bool ForgetChange(ConfigChangeKind kind, string distro)
    {
        if (!settings.Current.Changes.Any(c => c.Kind == kind && c.Distro == distro))
            return false;
        settings.Update(s => s.Changes.RemoveAll(c => c.Kind == kind && c.Distro == distro));
        return true;
    }

    /// <summary>
    /// Modo TCP: faz o dockerd escutar também em tcp://127.0.0.1:2375 por um override do systemd.
    /// Colocar "hosts" no daemon.json conflita com o "-H fd://" do docker.service, por isso o override.
    /// </summary>
    public async Task SetTcpListenerAsync(string distro, bool enabled)
    {
        if (enabled)
        {
            await RunRoot(distro, """
                mkdir -p "$(dirname "$A1")"
                dockerd_bin="$(command -v dockerd || echo /usr/bin/dockerd)"
                cat > "$A1" <<EOF
                [Service]
                ExecStart=
                ExecStart=$dockerd_bin -H fd:// -H tcp://127.0.0.1:2375 --containerd=/run/containerd/containerd.sock
                EOF
                systemctl daemon-reload
                systemctl restart docker.service
                """, "configurar a API TCP", [TcpOverridePath]);
            settings.RecordChange(ConfigChangeKind.DockerTcpOverride, distro);
        }
        else
        {
            await RunRoot(distro, """
                rm -f -- "$A1"
                systemctl daemon-reload
                systemctl restart docker.service
                """, "remover a API TCP", [TcpOverridePath]);
            settings.Update(s => s.Changes.RemoveAll(c => c.Kind == ConfigChangeKind.DockerTcpOverride && c.Distro == distro));
        }
    }

    /// <summary>Instalação guiada: o script oficial roda num terminal visível, e o usuário acompanha e digita a senha do sudo.</summary>
    public Task OpenDockerInstallerAsync(string distro, string home) =>
        launcher.OpenTerminalAsync(distro, home,
            command: "curl -fsSL https://get.docker.com -o /tmp/get-docker.sh && sudo sh /tmp/get-docker.sh && rm -f /tmp/get-docker.sh && echo && echo 'Docker instalado. Volte ao WSL Vision e clique em Verificar de novo.'",
            title: "Instalar Docker Engine");

    public static async Task<(bool Ok, string Message, int Containers)> TestConnectionAsync(WslService wsl, string distro, DockerConnectionMode mode)
    {
        using var client = DockerManager.Create(wsl, distro, mode);
        try
        {
            if (!await client.PingAsync())
                return (false, mode == DockerConnectionMode.Tcp
                    ? "A API não respondeu em 127.0.0.1:2375."
                    : "O Docker não respondeu pelo socket. Verifique se o serviço está ligado e se o usuário está no grupo docker.", 0);
            var v = await client.GetVersionAsync();
            var list = await client.ListContainersAsync();
            return (true, $"Docker {v.Version} respondendo", list.Count);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, 0);
        }
    }

    /// <summary>Desfaz, na ordem inversa, tudo o que o app alterou na distro e no Windows.</summary>
    public async Task<List<string>> UndoAllAsync(WslConfigService wslConfig)
    {
        var done = new List<string>();
        foreach (var c in settings.Current.Changes.AsEnumerable().Reverse().ToList())
        {
            try
            {
                switch (c.Kind)
                {
                    case ConfigChangeKind.DockerTcpOverride when c.Distro is not null:
                        await SetTcpListenerAsync(c.Distro, false);
                        done.Add("API TCP removida");
                        break;
                    case ConfigChangeKind.UserAddedToDockerGroup when c.Distro is not null && c.Detail is not null:
                        await RunRoot(c.Distro, "gpasswd -d \"$A1\" docker || true", "remover do grupo docker", [c.Detail]);
                        done.Add($"{c.Detail} removido do grupo docker");
                        break;
                    case ConfigChangeKind.DockerServiceEnabled when c.Distro is not null:
                        await RunRoot(c.Distro, "systemctl disable docker.service docker.socket || true", "desativar o início automático do Docker");
                        done.Add("Docker não inicia mais sozinho");
                        break;
                    case ConfigChangeKind.DockerAutostartDisabled when c.Distro is not null:
                        await RunRoot(c.Distro, "systemctl enable docker.service docker.socket || true", "reativar o início automático do Docker");
                        done.Add("Docker volta a iniciar junto com a distro");
                        break;
                    case ConfigChangeKind.SystemdEnabled when c.Distro is not null:
                        await RunRoot(c.Distro, """
                            f=/etc/wsl.conf
                            if [ -f "$f.wslvision.bak" ]; then mv -f "$f.wslvision.bak" "$f";
                            elif [ -f "$f.wslvision.created" ]; then rm -f "$f" "$f.wslvision.created"; fi
                            """, "restaurar o /etc/wsl.conf");
                        done.Add("/etc/wsl.conf restaurado");
                        break;
                    case ConfigChangeKind.WslConfigEdited:
                        if (wslConfig.RestoreBackup())
                            done.Add(".wslconfig restaurado");
                        break;
                }
                settings.Update(s => s.Changes.RemoveAll(x => x.Kind == c.Kind && x.Distro == c.Distro));
            }
            catch (Exception ex)
            {
                done.Add($"Falhou: {ex.Message}");
            }
        }
        return done;
    }

    private async Task RunRoot(string distro, string script, string verb, IReadOnlyList<string>? args = null)
    {
        var r = await wsl.RunScriptAsync(distro, script, args, asRoot: true, timeout: TimeSpan.FromMinutes(3));
        if (!r.Success)
            throw new InvalidOperationException(WslService.ErrorText(r, $"Não foi possível {verb}."));
    }
}
