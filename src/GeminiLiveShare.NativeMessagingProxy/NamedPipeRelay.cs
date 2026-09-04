using System.IO.Pipes;

namespace GeminiLiveShare.NativeMessagingProxy;

public sealed class NamedPipeRelay : IAsyncDisposable
{
    public const string PipeName = "GeminiLiveShare.BrowserAgent";
    private readonly StdioFramer _framer;
    private readonly NamedPipeClientStream _pipe;

    private NamedPipeRelay(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _framer = new StdioFramer(pipe);
    }

    public static async Task<NamedPipeRelay> ConnectAsync(CancellationToken cancellationToken = default)
    {
        NamedPipeClientStream pipe = new(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(750, cancellationToken).ConfigureAwait(false);
            return new NamedPipeRelay(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<byte[]?> ReadMessageAsync(CancellationToken cancellationToken = default) =>
        _framer.ReadMessageAsync(cancellationToken);

    public Task WriteMessageAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
        _framer.WriteMessageAsync(message, cancellationToken);

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}
