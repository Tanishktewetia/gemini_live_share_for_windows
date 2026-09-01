using System.Runtime.InteropServices;
using System.Diagnostics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace GeminiLiveShare.Core.Vision;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1);
    private bool _disposed;

    public async Task RunAsync(
        Func<SoftwareBitmap, CancellationToken, Task> frameHandler,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frameHandler);
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows Graphics Capture is not supported on this computer.");
        }

        GraphicsCaptureItem item = CreatePrimaryMonitorItem();
        using IDirect3DDevice device = Direct3DDeviceFactory.Create();
        using Direct3D11CaptureFramePool framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);
        using GraphicsCaptureSession session = framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = true;
        session.StartCapture();
        long intervalTicks = (long)(FrameInterval.TotalSeconds * Stopwatch.Frequency);
        long nextFrameAt = Stopwatch.GetTimestamp() + intervalTicks;

        while (true)
        {
            TimeSpan delay = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextFrameAt);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            using Direct3D11CaptureFrame? frame = GetNewestFrame(framePool);
            if (frame is null)
            {
                nextFrameAt += intervalTicks;
                continue;
            }

            using SoftwareBitmap bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            await frameHandler(bitmap, cancellationToken).ConfigureAwait(false);

            nextFrameAt += intervalTicks;
            long now = Stopwatch.GetTimestamp();
            if (nextFrameAt <= now)
            {
                nextFrameAt = now + intervalTicks;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static Direct3D11CaptureFrame? GetNewestFrame(Direct3D11CaptureFramePool framePool)
    {
        Direct3D11CaptureFrame? newest = null;
        Direct3D11CaptureFrame? next;
        while ((next = framePool.TryGetNextFrame()) is not null)
        {
            newest?.Dispose();
            newest = next;
        }

        return newest;
    }

    private static GraphicsCaptureItem CreatePrimaryMonitorItem()
    {
        HMONITOR monitor = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO monitorInfo = new()
        {
            cbSize = (uint)Marshal.SizeOf<MONITORINFO>()
        };
        if (!PInvoke.GetMonitorInfo(monitor, ref monitorInfo) || ((uint)monitorInfo.dwFlags & 1U) == 0)
        {
            throw new InvalidOperationException("The primary monitor could not be resolved for screen capture.");
        }

        Guid itemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        GraphicsCaptureItemInterop interop = GraphicsCaptureItem.As<GraphicsCaptureItemInterop>();
        Marshal.ThrowExceptionForHR(interop.CreateForMonitor(monitor, in itemGuid, out nint itemPointer));
        try
        {
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface GraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, in Guid interfaceId, out nint result);

        [PreserveSig]
        int CreateForMonitor(HMONITOR monitor, in Guid interfaceId, out nint result);
    }
}
