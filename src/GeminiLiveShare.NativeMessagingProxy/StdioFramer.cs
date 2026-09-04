using System.Buffers.Binary;

namespace GeminiLiveShare.NativeMessagingProxy;

public sealed class StdioFramer
{
    private const uint MaximumMessageBytes = 16 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public StdioFramer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public async Task<byte[]?> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        byte[] lengthBytes = new byte[sizeof(uint)];
        int firstByte = await _stream.ReadAsync(lengthBytes.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (firstByte == 0)
        {
            return null;
        }

        await ReadExactlyAsync(lengthBytes.AsMemory(1), cancellationToken).ConfigureAwait(false);
        uint messageLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (messageLength > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Native message exceeds the {MaximumMessageBytes} byte limit.");
        }

        byte[] message = new byte[messageLength];
        await ReadExactlyAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        return message;
    }

    public async Task WriteMessageAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if ((ulong)message.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Native message exceeds the {MaximumMessageBytes} byte limit.");
        }

        byte[] lengthBytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)message.Length);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            int bytesRead = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("The native messaging stream ended mid-message.");
            }

            buffer = buffer[bytesRead..];
        }
    }
}
