using System.Buffers.Binary;
using System.IO.Pipes;

namespace GeminiLiveShare.Core.BrowserAgent;

public sealed class BrowserAgentBridge : IAsyncDisposable
{
    public const string PipeName = "GeminiLiveShare.BrowserAgent";
    private const uint MaximumMessageBytes = 16 * 1024 * 1024;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenerTask;

    public event EventHandler<string>? StatusChanged;

    public void Start()
    {
        if (_listenerTask is not null)
        {
            return;
        }

        _listenerTask = ListenAsync(_shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
        }

        _shutdown.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = new(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke(this, "Extension connected.");
                await RelayConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                StatusChanged?.Invoke(this, "Browser extension connection closed.");
            }
            catch (InvalidDataException exception)
            {
                StatusChanged?.Invoke(this, $"Browser extension message rejected: {exception.Message}");
            }
        }
    }

    private static async Task RelayConnectionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? request = await ReadMessageAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            await WriteMessageAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[sizeof(uint)];
        int firstByte = await stream.ReadAsync(lengthBytes.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (firstByte == 0)
        {
            return null;
        }

        await ReadExactlyAsync(stream, lengthBytes.AsMemory(1), cancellationToken).ConfigureAwait(false);
        uint messageLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (messageLength > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Native message exceeds the {MaximumMessageBytes} byte limit.");
        }

        byte[] message = new byte[messageLength];
        await ReadExactlyAsync(stream, message.AsMemory(), cancellationToken).ConfigureAwait(false);
        return message;
    }

    private static async Task WriteMessageAsync(
        Stream stream,
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken)
    {
        if ((ulong)message.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Native message exceeds the {MaximumMessageBytes} byte limit.");
        }

        byte[] lengthBytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)message.Length);
        await stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("The browser agent pipe ended mid-message.");
            }

            buffer = buffer[bytesRead..];
        }
    }
}