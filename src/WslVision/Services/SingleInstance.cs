namespace WslVision.Services;

/// <summary>Garante uma única instância: abrir o app de novo só traz a janela existente para a frente.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\WslVision.SingleInstance";
    private const string EventName = @"Local\WslVision.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle? _wait;

    public bool IsFirst { get; }

    public SingleInstance(Action onActivateRequested)
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        IsFirst = createdNew;
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (IsFirst)
            _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => onActivateRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void SignalFirstInstance() => _event.Set();

    public void Dispose()
    {
        _wait?.Unregister(null);
        _event.Dispose();
        if (IsFirst)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
