using System.Buffers.Binary;

namespace GeminiLiveShare.NativeMessagingProxy;

internal static class Program
{
    private const uint MaximumMessageBytes = 16 * 1024 * 1024;

    private static async Task<int> Main()
    {
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        byte[] lengthBytes = new byte[sizeof(uint)];

        try
        {
            await input.ReadExactlyAsync(lengthBytes).ConfigureAwait(false);
            uint messageLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
            if (messageLength > MaximumMessageBytes)
            {
                return 1;
            }

            byte[] message = new byte[messageLength];
            await input.ReadExactlyAsync(message).ConfigureAwait(false);
            await output.WriteAsync(lengthBytes).ConfigureAwait(false);
            await output.WriteAsync(message).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            return 0;
        }
        catch (EndOfStreamException)
        {
            return 0;
        }
    }
}
