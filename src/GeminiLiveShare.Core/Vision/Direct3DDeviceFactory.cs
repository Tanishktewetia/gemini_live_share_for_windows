using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;

namespace GeminiLiveShare.Core.Vision;

internal static class Direct3DDeviceFactory
{
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;

    public static IDirect3DDevice Create()
    {
        int result = D3D11CreateDevice(
            0,
            1,
            0,
            D3D11CreateDeviceBgraSupport,
            0,
            0,
            D3D11SdkVersion,
            out nint d3dDevice,
            out _,
            out nint deviceContext);
        Marshal.ThrowExceptionForHR(result);

        try
        {
            Guid dxgiDeviceGuid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in dxgiDeviceGuid, out nint dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint inspectable));
                try
                {
                    return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(d3dDevice);
            Marshal.Release(deviceContext);
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out int featureLevel,
        out nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}