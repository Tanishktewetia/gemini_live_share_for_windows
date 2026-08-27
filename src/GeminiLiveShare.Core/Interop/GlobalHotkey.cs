using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GeminiLiveShare.Core.Interop;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000
}

public sealed class GlobalHotkey : IDisposable
{
    public const int WindowMessage = 0x0312;

    private readonly nint _windowHandle;
    private readonly int _id;
    private bool _isRegistered;

    public GlobalHotkey(nint windowHandle, int id, HotkeyModifiers modifiers, uint virtualKey)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _id = id;
        if (!RegisterHotKey(_windowHandle, _id, (uint)modifiers, virtualKey))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to register the global hotkey.");
        }

        _isRegistered = true;
    }

    public int Id => _id;

    public void Dispose()
    {
        if (!_isRegistered)
        {
            return;
        }

        UnregisterHotKey(_windowHandle, _id);
        _isRegistered = false;
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);
}