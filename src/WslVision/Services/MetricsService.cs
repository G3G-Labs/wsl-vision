using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WslVision.Models;

namespace WslVision.Services;

/// <summary>
/// Métricas ao vivo da distro e de cada container, com UM processo de longa duração na distro.
/// O loop usa só comandos internos do shell para ler /proc e os cgroups do Docker (sem docker CLI,
/// sem uma requisição por container) e só roda enquanto alguma aba visível pediu métricas.
/// </summary>
public sealed class MetricsService(WslService wsl)
{
    // Disco a cada 5 voltas (10 s): é a única leitura que precisa de processos (df, tail).
    private const string LoopScript = """
        i=0
        while :; do
          read up _ < /proc/uptime; echo "@@T $up"
          while read k v _; do
            case $k in MemTotal:|MemAvailable:) echo "@@M $k $v";; esac
          done < /proc/meminfo
          read l < /proc/stat; echo "@@CPU $l"
          if [ $((i % 5)) -eq 0 ]; then echo "@@DISK $(df -P -B1 / | tail -n 1)"; fi
          for d in /sys/fs/cgroup/system.slice/docker-*.scope /sys/fs/cgroup/docker/*/; do
            [ -r "$d/cpu.stat" ] || continue
            id=${d%/}; id=${id##*/}; id=${id#docker-}; id=${id%.scope}
            read _ u _ < "$d/cpu.stat"
            read m < "$d/memory.current"
            ia=0
            while read k v; do [ "$k" = inactive_file ] && { ia=$v; break; }; done < "$d/memory.stat"
            echo "@@C $id $u $m $ia"
          done
          echo "@@END"
          i=$((i + 1))
          sleep 2
        done
        """;

    private readonly HashSet<object> _subscribers = [];
    private Process? _process;
    private CancellationTokenSource? _cts;
    private string? _distro;

    /// <summary>Última amostra recebida (ou null se nada está rodando).</summary>
    public LiveSample? Last { get; private set; }

    /// <summary>Disparado no thread da interface a cada ~2 s.</summary>
    public event EventHandler<LiveSample>? Sampled;

    public bool IsRunning => _process is not null;

    /// <summary>Uma aba pede métricas. O processo sobe no primeiro pedido.</summary>
    public void Subscribe(object owner, string distro)
    {
        _subscribers.Add(owner);
        if (_process is null || _distro != distro)
            Start(distro);
    }

    /// <summary>A aba saiu de cena. O processo para quando ninguém mais precisa.</summary>
    public void Unsubscribe(object owner)
    {
        _subscribers.Remove(owner);
        if (_subscribers.Count == 0)
            Stop();
    }

    /// <summary>Para tudo (ex.: o WSL vai ser desligado). Os inscritos voltam a pedir quando precisarem.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        if (_process is { } p)
        {
            ProcessRunner.TryKill(p);
            p.Dispose();
        }
        _process = null;
        Last = null;
    }

    private void Start(string distro)
    {
        Stop();
        _distro = distro;
        var p = wsl.StartStreaming(distro, ["sh", "-s"]);
        _process = p;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Run(() => ReadLoop(p, dispatcher, cts.Token), cts.Token);
    }

    private async Task ReadLoop(Process p, Dispatcher dispatcher, CancellationToken ct)
    {
        try
        {
            await p.StandardInput.WriteAsync(LoopScript.Replace("\r\n", "\n") + "\n");
            await p.StandardInput.FlushAsync(ct);
            p.StandardInput.Close();

            long memTotal = 0, memAvail = 0, diskUsed = 0, diskSize = 0;
            long[]? prevCpu = null;
            double cpu = 0, uptime = 0, prevUptime = 0;
            var prevContainer = new Dictionary<string, long>();
            var containers = new Dictionary<string, ContainerUsage>();

            while (!ct.IsCancellationRequested && await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;
                switch (parts[0])
                {
                    case "@@T" when parts.Length >= 2:
                        prevUptime = uptime;
                        uptime = ParseDouble(parts[1]);
                        containers = new Dictionary<string, ContainerUsage>();
                        break;
                    case "@@M" when parts.Length >= 3:
                        var kb = ParseLong(parts[2]) * 1024;
                        if (parts[1] == "MemTotal:") memTotal = kb;
                        else memAvail = kb;
                        break;
                    case "@@CPU" when parts.Length >= 6 && parts[1] == "cpu":
                        var cur = parts.Skip(2).Take(8).Select(ParseLong).ToArray();
                        if (prevCpu is { Length: > 4 } && cur.Length > 4)
                        {
                            var idle = cur[3] + cur[4] - (prevCpu[3] + prevCpu[4]);
                            var total = cur.Sum() - prevCpu.Sum();
                            cpu = total > 0 ? Math.Clamp(100.0 * (total - idle) / total, 0, 100) : 0;
                        }
                        prevCpu = cur;
                        break;
                    case "@@DISK" when parts.Length >= 5:
                        diskSize = ParseLong(parts[2]);
                        diskUsed = ParseLong(parts[3]);
                        break;
                    case "@@C" when parts.Length >= 5:
                        // Mesmo cálculo do "docker stats": CPU em % de um núcleo; memória sem o cache inativo.
                        var id = parts[1];
                        var usageUsec = ParseLong(parts[2]);
                        var mem = Math.Max(0, ParseLong(parts[3]) - ParseLong(parts[4]));
                        double cpuPct = 0;
                        if (prevContainer.TryGetValue(id, out var prevUsage) && uptime > prevUptime)
                            cpuPct = Math.Max(0, (usageUsec - prevUsage) / ((uptime - prevUptime) * 1_000_000) * 100);
                        prevContainer[id] = usageUsec;
                        containers[id] = new ContainerUsage(cpuPct, mem);
                        break;
                    case "@@END" when memTotal > 0:
                        foreach (var gone in prevContainer.Keys.Where(k => !containers.ContainsKey(k)).ToList())
                            prevContainer.Remove(gone);
                        var sample = new LiveSample(new WslMetricsSample(memTotal, memAvail, cpu, diskUsed, diskSize, uptime), containers);
                        await dispatcher.InvokeAsync(() =>
                        {
                            if (ReferenceEquals(_process, p))
                            {
                                Last = sample;
                                Sampled?.Invoke(this, sample);
                            }
                        });
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A aba fechou ou o WSL foi desligado.
        }
        finally
        {
            // Processo terminou sozinho (ex.: distro desligada): libera para um próximo Subscribe religar.
            await dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_process, p))
                {
                    p.Dispose();
                    _process = null;
                    Last = null;
                }
            });
        }
    }

    /// <summary>Religa o processo se ele morreu e ainda há quem precise (ex.: a distro voltou).</summary>
    public void EnsureRunning(string distro)
    {
        if (_subscribers.Count > 0 && (_process is null || _distro != distro))
            Start(distro);
    }

    /// <summary>Memória que o Windows entregou à VM do WSL (processo vmmemWSL, ou vmmem nas versões antigas).</summary>
    public static long? GetVmmemBytes()
    {
        foreach (var name in new[] { "vmmemWSL", "vmmem" })
        {
            var procs = Process.GetProcessesByName(name);
            try
            {
                if (procs.Length > 0)
                    return procs.Sum(p => p.WorkingSet64);
            }
            catch
            {
                return null;
            }
            finally
            {
                foreach (var p in procs)
                    p.Dispose();
            }
        }
        return null;
    }

    public static long GetTotalPhysicalMemory()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.ullTotalPhys : 0;
    }

    private static long ParseLong(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}

/// <summary>Uma leitura: distro inteira + cada container (chave = ID completo do container).</summary>
public sealed record LiveSample(WslMetricsSample Wsl, IReadOnlyDictionary<string, ContainerUsage> Containers);
