using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace GeminiLiveShare.Core.Desktop;

public sealed class DesktopAutomationService : IDesktopAutomationService
{
    private static readonly TimeSpan AutomationTimeout = TimeSpan.FromMilliseconds(500);

    public DesktopElementSnapshot? GetElementUnderCursor()
    {
        if (!TryGetCursorPosition(out Point cursor))
        {
            return null;
        }

        try
        {
            AutomationElement element = RunWithTimeout(() => AutomationElement.FromPoint(new WpfPoint(cursor.X, cursor.Y)));
            return ToElementSnapshot(element);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or TimeoutException)
        {
            Trace.WriteLine($"get_element_under_cursor failed: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<DesktopItemSnapshot> ListTaskbarItems()
    {
        try
        {
            AutomationElement? taskbar = FindByClassName("Shell_TrayWnd");
            if (taskbar is null)
            {
                return Array.Empty<DesktopItemSnapshot>();
            }

            Condition taskbarButtonCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
            AutomationElementCollection buttons = taskbar.FindAll(TreeScope.Descendants, taskbarButtonCondition);
            return ToUniqueItems(buttons);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or TimeoutException)
        {
            Trace.WriteLine($"list_taskbar_items failed: {ex.Message}");
            return Array.Empty<DesktopItemSnapshot>();
        }
    }

    public IReadOnlyList<DesktopItemSnapshot> ListDesktopIcons()
    {
        try
        {
            AutomationElement? folderView = FindDesktopFolderView();
            if (folderView is null)
            {
                return Array.Empty<DesktopItemSnapshot>();
            }

            Condition itemCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
            AutomationElementCollection icons = folderView.FindAll(TreeScope.Descendants, itemCondition);
            return ToUniqueItems(icons);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or TimeoutException)
        {
            Trace.WriteLine($"list_desktop_icons failed: {ex.Message}");
            return Array.Empty<DesktopItemSnapshot>();
        }
    }

    public FocusedWindowSnapshot? GetFocusedWindow()
    {
        nint foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0 || GetWindowThreadProcessId(foregroundWindow, out uint processId) == 0 || processId == 0)
        {
            return null;
        }

        string appName = ResolveProcessName(processId);
        string title = GetWindowTitle(foregroundWindow);
        DesktopElementSnapshot? focused = null;

        try
        {
            AutomationElement? focusedElement = RunWithTimeout(() => AutomationElement.FocusedElement);
            if (focusedElement is not null)
            {
                focused = ToElementSnapshot(focusedElement);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or TimeoutException)
        {
            Trace.WriteLine($"get_focused_window focused-element lookup failed: {ex.Message}");
        }

        return new FocusedWindowSnapshot(appName, title, focused);
    }

    private static IReadOnlyList<DesktopItemSnapshot> ToUniqueItems(AutomationElementCollection elements)
    {
        Dictionary<string, DesktopItemSnapshot> unique = new(StringComparer.Ordinal);
        foreach (AutomationElement element in elements)
        {
            Rectangle bounds = ToRectangle(element.Current.BoundingRectangle);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            string name = NormalizeName(element.Current.Name);
            string controlType = element.Current.ControlType?.ProgrammaticName ?? "Unknown";
            string key = $"{name}|{bounds.X}|{bounds.Y}|{bounds.Width}|{bounds.Height}";
            if (!unique.ContainsKey(key))
            {
                unique[key] = new DesktopItemSnapshot(name, controlType, bounds);
            }
        }

        return unique.Values
            .OrderBy(item => item.Bounds.Y)
            .ThenBy(item => item.Bounds.X)
            .ToArray();
    }

    private static DesktopElementSnapshot ToElementSnapshot(AutomationElement element)
    {
        List<string> path = new();
        AutomationElement? current = element;
        while (current is not null)
        {
            string segmentName = NormalizeName(current.Current.Name);
            string segmentType = current.Current.ControlType?.ProgrammaticName ?? "Unknown";
            path.Add($"{segmentType}:{segmentName}");
            current = TreeWalker.ControlViewWalker.GetParent(current);
        }

        path.Reverse();
        return new DesktopElementSnapshot(
            NormalizeName(element.Current.Name),
            element.Current.ControlType?.ProgrammaticName ?? "Unknown",
            path,
            ToRectangle(element.Current.BoundingRectangle));
    }

    private static Rectangle ToRectangle(WpfRect rect) =>
        rect.IsEmpty
            ? Rectangle.Empty
            : Rectangle.FromLTRB(
                (int)Math.Floor(rect.Left),
                (int)Math.Floor(rect.Top),
                (int)Math.Ceiling(rect.Right),
                (int)Math.Ceiling(rect.Bottom));

    private static string NormalizeName(string? name) => string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name.Trim();

    private static T RunWithTimeout<T>(Func<T> action)
    {
        Task<T> work = Task.Run(action);
        if (!work.Wait(AutomationTimeout))
        {
            throw new TimeoutException($"UI Automation lookup exceeded {AutomationTimeout.TotalMilliseconds:0} ms.");
        }

        return work.GetAwaiter().GetResult();
    }

    private static AutomationElement? FindByClassName(string className)
    {
        Condition condition = new PropertyCondition(AutomationElement.ClassNameProperty, className);
        return RunWithTimeout(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, condition));
    }

    private static AutomationElement? FindDesktopFolderView()
    {
        Condition listViewCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty, "SysListView32"),
            new PropertyCondition(AutomationElement.NameProperty, "FolderView"));

        return RunWithTimeout(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, listViewCondition));
    }

    private static string ResolveProcessName(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return string.IsNullOrWhiteSpace(process.ProcessName) ? "Unknown" : process.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string GetWindowTitle(nint window)
    {
        StringBuilder title = new(512);
        _ = GetWindowTextW(window, title, title.Capacity);
        return NormalizeName(title.ToString());
    }

    private static bool TryGetCursorPosition(out Point point)
    {
        if (!GetCursorPos(out POINT nativePoint))
        {
            point = default;
            return false;
        }

        point = new Point(nativePoint.X, nativePoint.Y);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
