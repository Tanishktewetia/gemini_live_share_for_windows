using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using GeminiLiveShare.Core.BrowserAgent.Models;

namespace GeminiLiveShare.Core.BrowserAgent;

public sealed class BrowserAgentBridge : IAsyncDisposable
{
    public const string PipeName = "GeminiLiveShare.BrowserAgent";
    private const uint MaximumMessageBytes = 16 * 1024 * 1024;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolCallResult>> _pendingResults = new();
    private readonly BrowserAgentToolRegistry _toolRegistry;
    private readonly object _pipeLock = new();
    private Task? _listenerTask;
    private Stream? _connectedPipe;

    public BrowserAgentBridge(BrowserAgentToolRegistry? toolRegistry = null)
    {
        _toolRegistry = toolRegistry ?? new BrowserAgentToolRegistry();
    }

    public event EventHandler<string>? StatusChanged;

    public void Start()
    {
        if (_listenerTask is not null)
        {
            return;
        }

        _listenerTask = ListenAsync(_shutdown.Token);
    }

    public async Task<ToolCallResult> SendToolCallAsync(
        string toolName,
        JsonElement args,
        CancellationToken cancellationToken = default)
    {
        if (!_toolRegistry.Contains(toolName))
        {
            throw new InvalidOperationException($"Browser agent tool is not registered: {toolName}");
        }

        Stream pipe;
        lock (_pipeLock)
        {
            pipe = _connectedPipe ?? throw new InvalidOperationException("No browser extension is connected.");
        }

        string requestId = Guid.NewGuid().ToString("N");
        TaskCompletionSource<ToolCallResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResults[requestId] = completion;
        try
        {
            ToolCallRequest request = new()
            {
                RequestId = requestId,
                Payload = new ToolCallPayload { Tool = toolName, Args = args }
            };
            await WriteMessageAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request), cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingResults.TryRemove(requestId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        Stream? pipe;
        lock (_pipeLock)
        {
            pipe = _connectedPipe;
            _connectedPipe = null;
        }

        pipe?.Dispose();
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

        foreach (TaskCompletionSource<ToolCallResult> completion in _pendingResults.Values)
        {
            completion.TrySetCanceled(_shutdown.Token);
        }

        _writeLock.Dispose();
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
                lock (_pipeLock)
                {
                    _connectedPipe = pipe;
                }

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
            finally
            {
                lock (_pipeLock)
                {
                    if (ReferenceEquals(_connectedPipe, pipe))
                    {
                        _connectedPipe = null;
                    }
                }
            }
        }
    }

    private async Task RelayConnectionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? request = await ReadMessageAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            if (TryCompleteToolResult(request))
            {
                continue;
            }

            await WriteMessageAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryCompleteToolResult(byte[] message)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement type) ||
                type.GetString() != "tool_result" ||
                !root.TryGetProperty("requestId", out JsonElement requestId))
            {
                return false;
            }

            string? id = requestId.GetString();
            if (string.IsNullOrWhiteSpace(id) || !_pendingResults.TryRemove(id, out TaskCompletionSource<ToolCallResult>? completion))
            {
                return false;
            }

            ToolCallResult? result = JsonSerializer.Deserialize<ToolCallResult>(message);
            completion.TrySetResult(result ?? throw new InvalidDataException("The tool result was empty."));
            return true;
        }
        catch (JsonException)
        {
            return false;
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

    private async Task WriteMessageAsync(Stream stream, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
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
            await stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
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
