using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using WslVision.Models;

namespace WslVision.Services.Docker;

public sealed class DockerApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Cliente mínimo da Docker Engine API. Fala HTTP/1.1 ou por um túnel "docker system dial-stdio"
/// aberto via wsl.exe (nenhuma porta de rede), ou por TCP em 127.0.0.1:2375.
/// </summary>
public sealed class DockerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;

    private DockerClient(HttpMessageHandler handler, Uri baseAddress)
    {
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan, // cada chamada define o próprio prazo
        };
    }

    /// <summary>Cada conexão HTTP é um wsl.exe rodando "docker system dial-stdio" (usa /var/run/docker.sock).</summary>
    /// <remarks>
    /// Antes de abrir cada conexão nova, confere se a distro está ligada: um "wsl.exe -d" com a distro
    /// desligada a ligaria, e o app nunca deve acordar o WSL sem o usuário pedir.
    /// </remarks>
    public static DockerClient ViaWslSocket(WslService wsl, string distro) =>
        ViaProcessStdio(
            () => wsl.StartStreaming(distro, ["docker", "system", "dial-stdio"]),
            async () => await wsl.IsRunningAsync(distro));

    /// <summary>HTTP sobre o stdin/stdout de um processo; cada conexão do pool abre um processo novo.</summary>
    public static DockerClient ViaProcessStdio(Func<System.Diagnostics.Process> start, Func<Task<bool>>? canConnect = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, _) =>
            {
                if (canConnect is not null && !await canConnect())
                    throw new HttpRequestException("A distro está desligada.");
                return new ProcessDuplexStream(start());
            },
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8,
            UseProxy = false,
        };
        return new DockerClient(handler, new Uri("http://docker/"));
    }

    public static DockerClient ViaTcp(int port = 2375)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 8,
            UseProxy = false,
        };
        return new DockerClient(handler, new Uri($"http://127.0.0.1:{port}/"));
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = Linked(ct, TimeSpan.FromSeconds(8));
            using var r = await _http.GetAsync("_ping", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return false;
        }
    }

    public async Task<DockerVersion> GetVersionAsync(CancellationToken ct = default) =>
        await GetJsonAsync<DockerVersion>("version", ct, TimeSpan.FromSeconds(10));

    public async Task<List<ContainerSummary>> ListContainersAsync(CancellationToken ct = default) =>
        await GetJsonAsync<List<ContainerSummary>>("containers/json?all=true", ct, TimeSpan.FromSeconds(15));

    public Task StartAsync(string id, CancellationToken ct = default) => PostAsync($"containers/{Esc(id)}/start", ct);

    public Task StopAsync(string id, CancellationToken ct = default) => PostAsync($"containers/{Esc(id)}/stop?t=10", ct, TimeSpan.FromSeconds(40));

    public Task RestartAsync(string id, CancellationToken ct = default) => PostAsync($"containers/{Esc(id)}/restart?t=10", ct, TimeSpan.FromSeconds(60));

    public async Task RemoveAsync(string id, bool force, CancellationToken ct = default)
    {
        using var cts = Linked(ct, TimeSpan.FromSeconds(60));
        using var r = await _http.DeleteAsync($"containers/{Esc(id)}?force={(force ? "true" : "false")}", cts.Token);
        await EnsureOk(r, cts.Token);
    }

    /// <summary>Espaço usado por imagens, containers, volumes e cache de build (pode levar alguns segundos).</summary>
    public Task<DockerDiskUsage> GetDiskUsageAsync(CancellationToken ct = default) =>
        GetJsonAsync<DockerDiskUsage>("system/df", ct, TimeSpan.FromMinutes(2));

    /// <summary>POST /{what}/prune (images, containers, volumes, build). Devolve o espaço liberado, em bytes.</summary>
    public async Task<long> PruneAsync(string what, string? query = null, CancellationToken ct = default)
    {
        using var cts = Linked(ct, TimeSpan.FromMinutes(10));
        using var r = await _http.PostAsync($"{what}/prune{(query is null ? "" : "?" + query)}", null, cts.Token);
        await EnsureOk(r, cts.Token);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
        return doc.RootElement.TryGetProperty("SpaceReclaimed", out var s) && s.TryGetInt64(out var n) ? n : 0;
    }

    public static string Filters(string json) => "filters=" + Uri.EscapeDataString(json);

    public async Task<bool> IsTtyAsync(string id, CancellationToken ct = default)
    {
        var info = await GetJsonAsync<ContainerInspect>($"containers/{Esc(id)}/json", ct, TimeSpan.FromSeconds(10));
        return info.Config?.Tty ?? false;
    }

    /// <summary>
    /// Eventos de containers (start, die, destroy…) em tempo real, numa única conexão aberta.
    /// Substitui consultar a lista a cada poucos segundos.
    /// </summary>
    public async IAsyncEnumerable<string> StreamContainerEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // Só eventos que mudam o estado. Healthchecks geram "exec_*" a cada poucos segundos e ficam de fora.
        var filters = Uri.EscapeDataString(
            """{"type":["container"],"event":["create","destroy","die","kill","oom","pause","unpause","rename","restart","start","stop","update","health_status"]}""");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"events?filters={filters}");
        using var r = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOk(r, ct);
        await using var stream = await r.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
                continue;
            string? action = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                action = doc.RootElement.TryGetProperty("Action", out var a) ? a.GetString() : null;
            }
            catch (JsonException)
            {
                // Linha incompleta: ignora.
            }
            if (action is not null)
                yield return action;
        }
    }

    /// <summary>Logs em tempo real (follow), já separados em linhas.</summary>
    public async IAsyncEnumerable<DockerLogLine> StreamLogsAsync(
        string id, int tail, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var tty = await IsTtyAsync(id, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"containers/{Esc(id)}/logs?follow=true&stdout=true&stderr=true&timestamps=true&tail={tail}");
        using var r = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOk(r, ct);
        await using var stream = await r.Content.ReadAsStreamAsync(ct);

        var outBuf = new StringBuilder();
        var errBuf = new StringBuilder();
        var outDec = Encoding.UTF8.GetDecoder();
        var errDec = Encoding.UTF8.GetDecoder();

        if (tty)
        {
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                foreach (var line in Split(outBuf, Decode(outDec, buffer, n)))
                    yield return Parse(line, false);
            yield break;
        }

        // Formato multiplexado: cabeçalho de 8 bytes [stream, 0, 0, 0, tamanho (big-endian)] + dados.
        var header = new byte[8];
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stream, header, ct))
                yield break;
            var isErr = header[0] == 2;
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            var payload = new byte[size];
            if (!await ReadExactAsync(stream, payload, ct))
                yield break;
            foreach (var line in Split(isErr ? errBuf : outBuf, Decode(isErr ? errDec : outDec, payload, payload.Length)))
                yield return Parse(line, isErr);
        }
    }

    private static string Decode(Decoder d, byte[] bytes, int count)
    {
        // O Decoder guarda bytes de caracteres UTF-8 quebrados entre dois pedaços.
        var chars = new char[d.GetCharCount(bytes, 0, count)];
        d.GetChars(bytes, 0, count, chars, 0);
        return new string(chars);
    }

    private static IEnumerable<string> Split(StringBuilder pending, string chunk)
    {
        pending.Append(chunk);
        var text = pending.ToString();
        var last = text.LastIndexOf('\n');
        if (last < 0)
            yield break;
        pending.Clear().Append(text, last + 1, text.Length - last - 1);
        foreach (var l in text[..last].Split('\n'))
            yield return l.TrimEnd('\r');
    }

    private static DockerLogLine Parse(string line, bool isErr)
    {
        var sp = line.IndexOf(' ');
        if (sp > 0 && DateTimeOffset.TryParse(line[..sp], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            return new DockerLogLine(t.ToLocalTime(), line[(sp + 1)..], isErr);
        return new DockerLogLine(null, line, isErr);
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct, TimeSpan timeout)
    {
        using var cts = Linked(ct, timeout);
        using var r = await _http.GetAsync(path, cts.Token);
        await EnsureOk(r, cts.Token);
        return await r.Content.ReadFromJsonAsync<T>(Json, cts.Token)
            ?? throw new DockerApiException("Resposta vazia do Docker.");
    }

    private async Task PostAsync(string path, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = Linked(ct, timeout ?? TimeSpan.FromSeconds(30));
        using var r = await _http.PostAsync(path, null, cts.Token);
        await EnsureOk(r, cts.Token);
    }

    private static async Task EnsureOk(HttpResponseMessage r, CancellationToken ct)
    {
        // 304 = já estava no estado pedido (ex.: iniciar um container que já roda).
        if (r.IsSuccessStatusCode || r.StatusCode == HttpStatusCode.NotModified)
            return;
        string message;
        try
        {
            var body = await r.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? body : body;
        }
        catch
        {
            message = r.ReasonPhrase ?? "erro desconhecido";
        }
        throw new DockerApiException($"O Docker recusou a operação: {message}", r.StatusCode);
    }

    private static CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static string Esc(string id) => Uri.EscapeDataString(id);

    public void Dispose() => _http.Dispose();
}
