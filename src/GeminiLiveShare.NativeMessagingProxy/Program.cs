using System.Text.Json;

namespace GeminiLiveShare.NativeMessagingProxy;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            await using NamedPipeRelay relay = await NamedPipeRelay.ConnectAsync().ConfigureAwait(false);
            StdioFramer stdin = new(Console.OpenStandardInput());
            StdioFramer stdout = new(Console.OpenStandardOutput());
            using CancellationTokenSource shutdown = new();
            Task extensionToApp = ForwardExtensionToAppAsync(stdin, relay, shutdown.Token);
            Task appToExtension = ForwardAppToExtensionAsync(relay, stdout, shutdown.Token);
            Task completed = await Task.WhenAny(extensionToApp, appToExtension).ConfigureAwait(false);
            shutdown.Cancel();
            await completed.ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Native messaging proxy could not connect to GeminiLiveShare.App: {exception.Message}");
            await WriteAppNotRunningEventAsync().ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task ForwardExtensionToAppAsync(
        StdioFramer stdin,
        NamedPipeRelay relay,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? message = await stdin.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            await relay.WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ForwardAppToExtensionAsync(
        NamedPipeRelay relay,
        StdioFramer stdout,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? message = await relay.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            await stdout.WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteAppNotRunningEventAsync()
    {
        StdioFramer stdout = new(Console.OpenStandardOutput());
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "event",
            requestId = Guid.NewGuid().ToString("N"),
            payload = new
            {
                code = "app_not_running",
                message = "GeminiLiveShare app is not running; browser actions are unavailable."
            }
        });
        await stdout.WriteMessageAsync(payload).ConfigureAwait(false);
    }
}
