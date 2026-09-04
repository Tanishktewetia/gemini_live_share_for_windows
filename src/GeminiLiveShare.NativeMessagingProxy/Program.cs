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

            while (true)
            {
                byte[]? request = await stdin.ReadMessageAsync().ConfigureAwait(false);
                if (request is null)
                {
                    return 0;
                }

                await relay.WriteMessageAsync(request).ConfigureAwait(false);
                byte[]? response = await relay.ReadMessageAsync().ConfigureAwait(false);
                if (response is null)
                {
                    return 1;
                }

                await stdout.WriteMessageAsync(response).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Native messaging proxy could not connect to GeminiLiveShare.App: {exception.Message}");
            await WriteAppNotRunningEventAsync().ConfigureAwait(false);
            return 1;
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
