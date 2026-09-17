using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace GeminiLiveShare.Core.Audio;

/// <summary>
/// Microphone capture through the Windows Voice Capture DSP (CLSID_CWMAudioAEC) in source mode.
/// The DSP opens the default microphone and a loopback of the default speaker itself, then removes
/// the speaker signal (Gemini's own voice) from the microphone signal. Without this, speaker playback
/// reaches the microphone, Gemini's server-side voice activity detection hears it as user speech,
/// and every response interrupts itself.
/// </summary>
internal sealed class EchoCancellingMicrophone : IDisposable
{
    private const int SampleRate = 16_000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    // Match the 40 ms chunks the MME fallback produced so SessionOrchestrator's queue tuning is unchanged.
    private const int ChunkBytes = SampleRate * Channels * (BitsPerSample / 8) * 40 / 1000;
    private const int PollIntervalMilliseconds = 10;
    private const int MaximumConsecutiveFailures = 50;
    // The DSP only produces output while the speaker endpoint is rendering (loopback is its echo reference).
    // SessionOrchestrator keeps playback open for the whole session, so a long silence means a stall.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(3);

    private static readonly Guid VoiceCaptureDspClsid = new("745057C7-F353-4F2D-A7EE-58434477730E");
    private static readonly Guid AecPropertySet = new("6F52C567-0360-4BD2-9617-CCBF1421C939");
    private static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MediaSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid FormatWaveFormatEx = new("05589F81-C356-11CE-BF01-00AA0055595A");

    private readonly Action<byte[]> _audioCaptured;
    private readonly Action<Exception> _captureFailed;
    private readonly ManualResetEventSlim _stopRequested = new(false);
    private Thread? _thread;

    public EchoCancellingMicrophone(Action<byte[]> audioCaptured, Action<Exception> captureFailed)
    {
        _audioCaptured = audioCaptured;
        _captureFailed = captureFailed;
    }

    /// <summary>
    /// Starts the DSP on a dedicated MTA thread. Throws when the DSP cannot be initialized so the
    /// caller can fall back to plain capture.
    /// </summary>
    public void Start()
    {
        TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => Run(initialized))
        {
            IsBackground = true,
            Name = "GeminiLiveShare echo-cancelling microphone",
            Priority = ThreadPriority.AboveNormal
        };
        thread.SetApartmentState(ApartmentState.MTA);
        _thread = thread;
        thread.Start();

        if (!initialized.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            Stop();
            throw new TimeoutException("The Windows echo cancellation DSP did not start within 5 seconds.");
        }

        initialized.Task.GetAwaiter().GetResult();
    }

    public void Stop()
    {
        _stopRequested.Set();
        Thread? thread = _thread;
        _thread = null;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    // The stop event is deliberately not disposed: if a join times out, the DSP thread may still read it.
    public void Dispose() => Stop();

    private void Run(TaskCompletionSource initialized)
    {
        IMediaObject? mediaObject = null;
        MediaBuffer? outputBuffer = null;
        bool streaming = false;
        try
        {
            mediaObject = (IMediaObject)Activator.CreateInstance(
                Type.GetTypeFromCLSID(VoiceCaptureDspClsid, throwOnError: true)!)!;
            ConfigureDsp(mediaObject);
            SetOutputFormat(mediaObject);
            ThrowIfFailed(mediaObject.AllocateStreamingResources(), "AllocateStreamingResources");
            streaming = true;
            outputBuffer = new MediaBuffer(SampleRate * Channels * (BitsPerSample / 8));
            initialized.TrySetResult();
        }
        catch (Exception ex)
        {
            Release(mediaObject, outputBuffer, streaming);
            initialized.TrySetException(ex);
            return;
        }

        try
        {
            CaptureLoop(mediaObject, outputBuffer);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Echo-cancelling microphone stopped: {ex.Message}");
            _captureFailed(ex);
        }
        finally
        {
            Release(mediaObject, outputBuffer, streaming);
        }
    }

    private void CaptureLoop(IMediaObject mediaObject, MediaBuffer outputBuffer)
    {
        byte[] pending = new byte[ChunkBytes * 4];
        int pendingLength = 0;
        int consecutiveFailures = 0;
        DmoOutputDataBuffer[] buffers = new DmoOutputDataBuffer[1];
        Stopwatch sinceLastOutput = Stopwatch.StartNew();

        while (!_stopRequested.Wait(PollIntervalMilliseconds))
        {
            // Drain everything the DSP has buffered; INCOMPLETE means more data is waiting.
            bool moreAvailable;
            do
            {
                outputBuffer.Length = 0;
                buffers[0] = new DmoOutputDataBuffer { Buffer = outputBuffer };
                int result = mediaObject.ProcessOutput(0, 1, buffers, out _);
                buffers[0].Buffer = null;
                if (result < 0)
                {
                    if (++consecutiveFailures >= MaximumConsecutiveFailures)
                    {
                        // Typically the default microphone or speaker was unplugged/changed mid-session.
                        Marshal.ThrowExceptionForHR(result);
                    }

                    break;
                }

                consecutiveFailures = 0;
                moreAvailable = (buffers[0].Status & DmoOutputDataBufferIncomplete) != 0;
                int produced = outputBuffer.Length;
                if (produced > 0)
                {
                    sinceLastOutput.Restart();
                    if (pendingLength + produced > pending.Length)
                    {
                        Array.Resize(ref pending, pendingLength + produced);
                    }

                    Marshal.Copy(outputBuffer.Pointer, pending, pendingLength, produced);
                    pendingLength += produced;
                }
            }
            while (moreAvailable && !_stopRequested.IsSet);

            if (sinceLastOutput.Elapsed >= StallTimeout)
            {
                throw new EchoCancellationStalledException(
                    $"The echo cancellation DSP produced no audio for {StallTimeout.TotalSeconds:0} seconds.");
            }

            int offset = 0;
            while (pendingLength - offset >= ChunkBytes)
            {
                byte[] chunk = new byte[ChunkBytes];
                Buffer.BlockCopy(pending, offset, chunk, 0, ChunkBytes);
                offset += ChunkBytes;
                _audioCaptured(chunk);
            }

            if (offset > 0)
            {
                Buffer.BlockCopy(pending, offset, pending, 0, pendingLength - offset);
                pendingLength -= offset;
            }
        }
    }

    private const uint DmoOutputDataBufferIncomplete = 0x01000000;

    private static void ConfigureDsp(IMediaObject mediaObject)
    {
        IPropertyStore properties = (IPropertyStore)mediaObject;

        // Source mode: the DSP captures the microphone and speaker loopback itself.
        SetProperty(properties, 3, PropVariant.FromBool(true));
        // SINGLE_CHANNEL_AEC (0): echo cancellation for a single microphone.
        SetProperty(properties, 2, PropVariant.FromInt(0));
        // Use the same default endpoints as the rest of the app (WaveOutEvent plays on the default console device).
        (int render, int capture) = GetDefaultDeviceIndexes();
        SetProperty(properties, 4, PropVariant.FromInt((render << 16) | (capture & 0xFFFF)));
    }

    private static (int Render, int Capture) GetDefaultDeviceIndexes()
    {
        using MMDeviceEnumerator enumerator = new();
        return (
            FindDefaultIndex(enumerator, DataFlow.Render),
            FindDefaultIndex(enumerator, DataFlow.Capture));
    }

    private static int FindDefaultIndex(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Console))
        {
            throw new InvalidOperationException($"No default {flow.ToString().ToLowerInvariant()} audio device is available.");
        }

        using MMDevice defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
        MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        for (int index = 0; index < devices.Count; index++)
        {
            using MMDevice device = devices[index];
            if (device.ID == defaultDevice.ID)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"The default {flow.ToString().ToLowerInvariant()} audio device could not be indexed.");
    }

    private static void SetProperty(IPropertyStore properties, int propertyId, PropVariant value)
    {
        PropertyKey key = new() { FormatId = AecPropertySet, PropertyId = propertyId };
        ThrowIfFailed(properties.SetValue(ref key, ref value), $"SetValue({propertyId})");
    }

    private static void SetOutputFormat(IMediaObject mediaObject)
    {
        int blockAlign = Channels * (BitsPerSample / 8);
        WaveFormatEx format = new()
        {
            FormatTag = 1,
            Channels = Channels,
            SamplesPerSecond = SampleRate,
            AverageBytesPerSecond = SampleRate * blockAlign,
            BlockAlign = (short)blockAlign,
            BitsPerSample = BitsPerSample,
            ExtraSize = 0
        };

        int formatSize = Marshal.SizeOf<WaveFormatEx>();
        nint formatPointer = Marshal.AllocCoTaskMem(formatSize);
        try
        {
            Marshal.StructureToPtr(format, formatPointer, false);
            DmoMediaType mediaType = new()
            {
                MajorType = MediaTypeAudio,
                SubType = MediaSubtypePcm,
                FixedSizeSamples = 1,
                TemporalCompression = 0,
                SampleSize = blockAlign,
                FormatType = FormatWaveFormatEx,
                Unknown = 0,
                FormatSize = formatSize,
                Format = formatPointer
            };
            ThrowIfFailed(mediaObject.SetOutputType(0, ref mediaType, 0), "SetOutputType");
        }
        finally
        {
            Marshal.FreeCoTaskMem(formatPointer);
        }
    }

    private static void Release(IMediaObject? mediaObject, MediaBuffer? outputBuffer, bool streaming)
    {
        if (mediaObject is not null)
        {
            try
            {
                if (streaming)
                {
                    mediaObject.FreeStreamingResources();
                }
            }
            catch (COMException)
            {
            }

            Marshal.FinalReleaseComObject(mediaObject);
        }

        outputBuffer?.Dispose();
    }

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new COMException($"Voice Capture DSP {operation} failed (0x{hresult:X8}).", hresult);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        private const ushort VtI4 = 3;
        private const ushort VtBool = 11;

        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public int IntValue;
        [FieldOffset(8)] public short BoolValue;

        public static PropVariant FromInt(int value) => new() { VariantType = VtI4, IntValue = value };

        // VARIANT_TRUE is -1 and VARIANT_FALSE is 0.
        public static PropVariant FromBool(bool value) => new() { VariantType = VtBool, BoolValue = (short)(value ? -1 : 0) };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSecond;
        public int AverageBytesPerSecond;
        public short BlockAlign;
        public short BitsPerSample;
        public short ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DmoMediaType
    {
        public Guid MajorType;
        public Guid SubType;
        public int FixedSizeSamples;
        public int TemporalCompression;
        public int SampleSize;
        public Guid FormatType;
        public nint Unknown;
        public int FormatSize;
        public nint Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DmoOutputDataBuffer
    {
        [MarshalAs(UnmanagedType.Interface)]
        public IMediaBuffer? Buffer;
        public uint Status;
        public long Timestamp;
        public long TimeLength;
    }

    [ComImport]
    [Guid("59EFF8B9-938C-4A26-82F2-95CB84CDC837")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMediaBuffer
    {
        [PreserveSig] int SetLength(int length);
        [PreserveSig] int GetMaxLength(out int maxLength);
        [PreserveSig] int GetBufferAndLength(nint bufferPointerOut, nint lengthOut);
    }

    [ComVisible(true)]
    private sealed class MediaBuffer : IMediaBuffer, IDisposable
    {
        private readonly int _maxLength;

        public MediaBuffer(int maxLength)
        {
            _maxLength = maxLength;
            Pointer = Marshal.AllocHGlobal(maxLength);
        }

        public nint Pointer { get; private set; }

        public int Length { get; set; }

        public int SetLength(int length)
        {
            if (length < 0 || length > _maxLength)
            {
                return unchecked((int)0x80070057); // E_INVALIDARG
            }

            Length = length;
            return 0;
        }

        public int GetMaxLength(out int maxLength)
        {
            maxLength = _maxLength;
            return 0;
        }

        public int GetBufferAndLength(nint bufferPointerOut, nint lengthOut)
        {
            // Either out-pointer may legally be null.
            if (bufferPointerOut != 0)
            {
                Marshal.WriteIntPtr(bufferPointerOut, Pointer);
            }

            if (lengthOut != 0)
            {
                Marshal.WriteInt32(lengthOut, Length);
            }

            return 0;
        }

        public void Dispose()
        {
            if (Pointer != 0)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = 0;
            }
        }
    }

    [ComImport]
    [Guid("D8AD0F58-5494-4102-97C5-EC798E59BCF4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMediaObject
    {
        [PreserveSig] int GetStreamCount(out int inputStreams, out int outputStreams);
        [PreserveSig] int GetInputStreamInfo(int stream, out int flags);
        [PreserveSig] int GetOutputStreamInfo(int stream, out int flags);
        [PreserveSig] int GetInputType(int stream, int typeIndex, nint mediaType);
        [PreserveSig] int GetOutputType(int stream, int typeIndex, nint mediaType);
        [PreserveSig] int SetInputType(int stream, nint mediaType, int flags);
        [PreserveSig] int SetOutputType(int stream, ref DmoMediaType mediaType, int flags);
        [PreserveSig] int GetInputCurrentType(int stream, nint mediaType);
        [PreserveSig] int GetOutputCurrentType(int stream, nint mediaType);
        [PreserveSig] int GetInputSizeInfo(int stream, out int size, out int maxLookahead, out int alignment);
        [PreserveSig] int GetOutputSizeInfo(int stream, out int size, out int alignment);
        [PreserveSig] int GetInputMaxLatency(int stream, out long maxLatency);
        [PreserveSig] int SetInputMaxLatency(int stream, long maxLatency);
        [PreserveSig] int Flush();
        [PreserveSig] int Discontinuity(int stream);
        [PreserveSig] int AllocateStreamingResources();
        [PreserveSig] int FreeStreamingResources();
        [PreserveSig] int GetInputStatus(int stream, out int flags);
        [PreserveSig] int ProcessInput(int stream, nint buffer, int flags, long timestamp, long timeLength);
        [PreserveSig] int ProcessOutput(
            int flags,
            int outputBufferCount,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] DmoOutputDataBuffer[] outputBuffers,
            out int status);
        [PreserveSig] int Lock(int acquireLock);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}

internal sealed class EchoCancellationStalledException(string message) : Exception(message);
