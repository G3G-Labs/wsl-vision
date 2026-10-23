using System.Diagnostics;

namespace WslVision.Services.Docker;

/// <summary>
/// Junta o stdout (leitura) e o stdin (escrita) de um processo num único Stream.
/// Usado para falar HTTP com o Docker através de "docker system dial-stdio" dentro do WSL.
/// </summary>
internal sealed class ProcessDuplexStream(Process process) : Stream
{
    private readonly Stream _read = process.StandardOutput.BaseStream;
    private readonly Stream _write = process.StandardInput.BaseStream;
    private int _disposed;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _read.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _read.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _write.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _write.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Flush() => _write.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
        {
            try { _write.Dispose(); } catch { /* já fechado */ }
            ProcessRunner.TryKill(process);
            process.Dispose();
        }
        base.Dispose(disposing);
    }
}
