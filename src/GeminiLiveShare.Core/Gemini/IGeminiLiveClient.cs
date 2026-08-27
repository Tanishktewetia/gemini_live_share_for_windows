namespace GeminiLiveShare.Core.Gemini;

public interface IGeminiLiveClient : IAsyncDisposable
{
    event EventHandler<byte[]>? AudioReceived;

    event EventHandler? Interrupted;

    event EventHandler<string>? StatusChanged;

    event EventHandler<TranscriptionEventArgs>? TranscriptionReceived;

    event EventHandler<ConnectionAvailabilityChangedEventArgs>? ConnectionAvailabilityChanged;

    bool IsConnected { get; }

    Task ConnectAsync(string apiKey, CancellationToken cancellationToken = default);

    Task SendAudioAsync(byte[] pcmAudio, CancellationToken cancellationToken = default);

    Task SendVideoFrameAsync(string base64Jpeg, CancellationToken cancellationToken = default);

    Task SendAudioStreamEndAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}