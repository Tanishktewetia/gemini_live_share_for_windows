using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using SkiaSharp;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using AutomationCondition = System.Windows.Automation.Condition;
using ScreenRect = System.Windows.Rect;

namespace GeminiLiveShare.Core.Vision;

public sealed partial class CredentialBlurService : ICredentialBlurService
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SlowLookupThreshold = TimeSpan.FromMilliseconds(50);
    private readonly object _lookupLock = new();
    private Task<PasswordLookupResult>? _activeLookup;
    private nint _cachedForegroundWindow;
    private IReadOnlyList<AutomationElement> _cachedPasswordElements = Array.Empty<AutomationElement>();

    public async Task<bool> BlurPasswordFieldsAsync(
        SKBitmap fullResolutionFrame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fullResolutionFrame);
        cancellationToken.ThrowIfCancellationRequested();

        Task<PasswordLookupResult> lookup;
        lock (_lookupLock)
        {
            nint currentForegroundWindow = GetForegroundWindow();
            if (_cachedForegroundWindow != currentForegroundWindow)
            {
                // Clear immediately on focus changes, including while an older lookup is still running.
                ClearCachedElements(currentForegroundWindow);
            }

            if (_activeLookup is { IsCompleted: false })
            {
                Trace.WriteLine("Skipping video frame because the previous UI Automation lookup is still running.");
                return false;
            }

            lookup = Task.Run(FindPasswordBounds);
            _ = lookup.ContinueWith(
                completed => Trace.WriteLine($"UI Automation password lookup failed after timeout: {completed.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _activeLookup = lookup;
        }

        PasswordLookupResult lookupResult;
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            lookupResult = await lookup.WaitAsync(LookupTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Trace.WriteLine($"UI Automation password lookup exceeded {LookupTimeout.TotalMilliseconds:0} ms; dropping video frame.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.WriteLine($"UI Automation password lookup failed; dropping video frame: {ex.Message}");
            return false;
        }
        finally
        {
            stopwatch.Stop();
        }

        if (stopwatch.Elapsed >= SlowLookupThreshold)
        {
            Trace.WriteLine($"UI Automation password lookup took {stopwatch.Elapsed.TotalMilliseconds:0} ms.");
        }

        if (!lookupResult.IsValid)
        {
            ClearCachedElementsIfWindowChanged(lookupResult.ForegroundWindow);
            Trace.WriteLine("Foreground window could not be safely inspected or is shell multitasking UI; dropping video frame.");
            return false;
        }

        if (!TryResolvePasswordBounds(lookupResult, out IReadOnlyList<ScreenRect> passwordBounds))
        {
            Trace.WriteLine("Known password elements could not be relocated; dropping video frame.");
            return false;
        }

        if (!TryGetPrimaryMonitorBounds(out Windows.Win32.Foundation.RECT monitorBounds))
        {
            Trace.WriteLine("Unable to determine the primary monitor bounds; dropping video frame.");
            return false;
        }

        ApplyBlackBoxes(fullResolutionFrame, passwordBounds, monitorBounds);
        return true;
    }

    private static PasswordLookupResult FindPasswordBounds()
    {
        nint foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0)
        {
            // Activation transitions can temporarily have no foreground HWND; fail closed.
            return PasswordLookupResult.Failure(0);
        }

        if (!TryIdentifyForegroundWindow(foregroundWindow, out string className, out string processName))
        {
            // An unidentifiable foreground surface cannot safely be associated with this frame.
            return PasswordLookupResult.Failure(foregroundWindow);
        }

        if (IsSwitcherWindow(foregroundWindow, className, processName))
        {
            // Shell switchers contain live thumbnails that cannot be protected with per-control UIA bounds.
            return PasswordLookupResult.Failure(foregroundWindow);
        }

        AutomationElement root = AutomationElement.FromHandle(foregroundWindow);
        AutomationCondition passwordCondition = new PropertyCondition(AutomationElement.IsPasswordProperty, true);
        AutomationElementCollection passwordElements = root.FindAll(TreeScope.Descendants, passwordCondition);
        List<AutomationElement> elements = new(passwordElements.Count);
        foreach (AutomationElement element in passwordElements)
        {
            elements.Add(element);
        }

        nint finalForegroundWindow = GetForegroundWindow();
        if (finalForegroundWindow == 0 || finalForegroundWindow != foregroundWindow)
        {
            // Pixels and UIA coordinates may refer to different windows if focus changed mid-traversal.
            return PasswordLookupResult.Failure(finalForegroundWindow);
        }

        return PasswordLookupResult.Success(foregroundWindow, elements);
    }

    private bool TryResolvePasswordBounds(
        PasswordLookupResult result,
        out IReadOnlyList<ScreenRect> passwordBounds)
    {
        lock (_lookupLock)
        {
            if (_cachedForegroundWindow != result.ForegroundWindow)
            {
                // Password elements must never follow focus to a different foreground window.
                ClearCachedElements(result.ForegroundWindow);
            }

            if (result.Elements.Count > 0)
            {
                _cachedPasswordElements = result.Elements.ToArray();
            }

            if (_cachedPasswordElements.Count == 0)
            {
                passwordBounds = Array.Empty<ScreenRect>();
                return true;
            }

            List<ScreenRect> bounds = new(_cachedPasswordElements.Count);
            List<AutomationElement> validElements = new(_cachedPasswordElements.Count);
            foreach (AutomationElement element in _cachedPasswordElements)
            {
                try
                {
                    ScreenRect rectangle = element.Current.BoundingRectangle;
                    validElements.Add(element);
                    if (!rectangle.IsEmpty && rectangle.Width > 0 && rectangle.Height > 0)
                    {
                        bounds.Add(rectangle);
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // A fresh empty FindAll plus no surviving cached element is uncertain; fail closed below.
                }
            }

            _cachedPasswordElements = validElements;
            passwordBounds = bounds;
            return validElements.Count > 0 && bounds.Count > 0;
        }
    }

    private void ClearCachedElementsIfWindowChanged(nint foregroundWindow)
    {
        lock (_lookupLock)
        {
            if (_cachedForegroundWindow != foregroundWindow)
            {
                ClearCachedElements(foregroundWindow);
            }
        }
    }

    private void ClearCachedElements(nint foregroundWindow)
    {
        _cachedForegroundWindow = foregroundWindow;
        _cachedPasswordElements = Array.Empty<AutomationElement>();
    }

    private static bool TryIdentifyForegroundWindow(nint window, out string className, out string processName)
    {
        className = GetWindowClassName(window);
        processName = string.Empty;
        if (string.IsNullOrWhiteSpace(className) || GetWindowThreadProcessId(window, out uint processId) == 0 || processId == 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            return !string.IsNullOrWhiteSpace(processName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool IsSwitcherWindow(nint window, string className, string processName)
    {
        if (IsKnownSwitcherClass(className))
        {
            return true;
        }

        if (!processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
            !processName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (CoversPrimaryMonitor(window))
        {
            // Win+Tab can use build-specific classes, but its Explorer overlay covers the monitor.
            return true;
        }

        if (!IsGenericShellClass(className))
        {
            return false;
        }

        // Generic XAML/CoreWindow classes are common, so require title or ancestor corroboration.
        for (nint current = window; current != 0; current = GetParent(current))
        {
            if (IsKnownSwitcherClass(GetWindowClassName(current)) || HasSwitcherTitle(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CoversPrimaryMonitor(nint window)
    {
        if (!GetWindowRect(window, out Windows.Win32.Foundation.RECT windowBounds) ||
            !TryGetPrimaryMonitorBounds(out Windows.Win32.Foundation.RECT monitorBounds))
        {
            return false;
        }

        long monitorWidth = monitorBounds.right - monitorBounds.left;
        long monitorHeight = monitorBounds.bottom - monitorBounds.top;
        long overlapWidth = Math.Max(0, Math.Min(windowBounds.right, monitorBounds.right) -
            Math.Max(windowBounds.left, monitorBounds.left));
        long overlapHeight = Math.Max(0, Math.Min(windowBounds.bottom, monitorBounds.bottom) -
            Math.Max(windowBounds.top, monitorBounds.top));
        return monitorWidth > 0 && monitorHeight > 0 &&
            overlapWidth * overlapHeight >= monitorWidth * monitorHeight * 0.9;
    }

    private static bool IsKnownSwitcherClass(string className) =>
        className.Equals("MultitaskingViewFrame", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("TaskSwitcherWnd", StringComparison.OrdinalIgnoreCase);

    private static bool IsGenericShellClass(string className) =>
        className.Equals("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("CoreWindow", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase);

    private static bool HasSwitcherTitle(nint window)
    {
        StringBuilder title = new(256);
        _ = GetWindowTextW(window, title, title.Capacity);
        string value = title.ToString();
        return value.Contains("Task View", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Task Switching", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Alt-Tab", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Multitasking", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowClassName(nint window)
    {
        StringBuilder className = new(256);
        return GetClassNameW(window, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }

    private static bool TryGetPrimaryMonitorBounds(out Windows.Win32.Foundation.RECT bounds)
    {
        HMONITOR monitor = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO monitorInfo = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>()
        };

        if (!PInvoke.GetMonitorInfo(monitor, ref monitorInfo))
        {
            bounds = default;
            return false;
        }

        bounds = monitorInfo.rcMonitor;
        return true;
    }

    private static void ApplyBlackBoxes(
        SKBitmap frame,
        IReadOnlyList<ScreenRect> passwordBounds,
        Windows.Win32.Foundation.RECT monitorBounds)
    {
        int monitorWidth = monitorBounds.right - monitorBounds.left;
        int monitorHeight = monitorBounds.bottom - monitorBounds.top;
        if (monitorWidth <= 0 || monitorHeight <= 0)
        {
            return;
        }

        double scaleX = frame.Width / (double)monitorWidth;
        double scaleY = frame.Height / (double)monitorHeight;
        using SKCanvas canvas = new(frame);
        using SKPaint blackPaint = new()
        {
            Color = SKColors.Black,
            BlendMode = SKBlendMode.Src,
            IsAntialias = false
        };

        foreach (ScreenRect screenBounds in passwordBounds)
        {
            int left = Math.Clamp(
                (int)Math.Floor((screenBounds.Left - monitorBounds.left) * scaleX),
                0,
                frame.Width);
            int top = Math.Clamp(
                (int)Math.Floor((screenBounds.Top - monitorBounds.top) * scaleY),
                0,
                frame.Height);
            int right = Math.Clamp(
                (int)Math.Ceiling((screenBounds.Right - monitorBounds.left) * scaleX),
                0,
                frame.Width);
            int bottom = Math.Clamp(
                (int)Math.Ceiling((screenBounds.Bottom - monitorBounds.top) * scaleY),
                0,
                frame.Height);

            if (right > left && bottom > top)
            {
                canvas.DrawRect(SKRect.Create(left, top, right - left, bottom - top), blackPaint);
            }
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(nint window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint window,
        out Windows.Win32.Foundation.RECT rectangle);

    private sealed record PasswordLookupResult(
        bool IsValid,
        nint ForegroundWindow,
        IReadOnlyList<AutomationElement> Elements)
    {
        public static PasswordLookupResult Failure(nint foregroundWindow) =>
            new(false, foregroundWindow, Array.Empty<AutomationElement>());

        public static PasswordLookupResult Success(
            nint foregroundWindow,
            IReadOnlyList<AutomationElement> elements) =>
            new(true, foregroundWindow, elements);
    }
}