namespace WslVision.Services;

/// <summary>
/// Resultado de uma consulta cara (ex.: um wsl.exe) reaproveitado por pouco tempo. Quem chama durante uma consulta em
/// andamento espera a mesma, em vez de abrir outra. Uma falha não fica guardada, e <see cref="Invalidate"/> descarta o
/// resultado depois de algo que muda o estado (ligar ou desligar a distro).
/// </summary>
public sealed class CachedQuery<T>(Func<Task<T>> query, TimeSpan ttl, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private Task<T>? _task;
    private DateTimeOffset _expires = DateTimeOffset.MinValue;
    private int _version;

    public Task<T> GetAsync()
    {
        lock (_gate)
        {
            if (_task is { } t && (!t.IsCompleted || (t.IsCompletedSuccessfully && _time.GetUtcNow() < _expires)))
                return t;
            _expires = DateTimeOffset.MaxValue; // vale até a consulta terminar; o prazo conta a partir daí
            _task = RunAsync(++_version);
            return _task;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _version++;
            _task = null;
        }
    }

    private async Task<T> RunAsync(int version)
    {
        var result = await query();
        lock (_gate)
        {
            if (version == _version)
                _expires = _time.GetUtcNow() + ttl;
        }
        return result;
    }
}
