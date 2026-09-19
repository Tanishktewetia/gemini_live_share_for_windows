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
    private static readonly TimeSpan AutomationTimeout = TimeSpan.FromMilliseconds(750);
    private const int LvmGetItemCount = 0x1000 + 4;

    private static readonly HashSet<string> ReservedTaskbarKeywords =
    [
        "start",
        "search",
        "task view",
        "widgets",
        "copilot",
        "show hidden icons",
        "hidden icons",
        "notification",
        "system tray",
        "show desktop",
        "network",
        "volume",
        "battery",
        "clock",
        "date",
        "time",
        "input",
        "language"
    ];

    private static readonly HashSet<string> TrayClassNames =
    [
        "TrayNotifyWnd",
        "NotifyIconOverflowWindow",
        "SysPager"
    ];

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

    public IReadOnlyList<DesktopItemSnapshot> ListTaskbarItems() => GetTaskbarItemCount().AppItems;

    public IReadOnlyList<DesktopItemSnapshot> ListDesktopIcons() => GetDesktopIconCount().VisibleItems;

    public DesktopIconCountSnapshot GetDesktopIconCount()
    {
        try
        {
            nint listViewHandle = 0;
            string hostStrategy = "none";
            bool listViewLocated = TryFindDesktopListViewHandle(out listViewHandle, out hostStrategy);

            int shellCount = 0;
            bool shellCountAvailable = listViewLocated && TryGetDesktopListViewItemCount(listViewHandle, out shellCount);
            IReadOnlyList<DesktopItemSnapshot> visibleItems = listViewLocated
                ? ListVisibleDesktopItemsFromHandle(listViewHandle)
                : ListVisibleDesktopItemsFallback();

            int visibleUiCount = visibleItems.Count;
            int sourceItemCount = shellCountAvailable ? shellCount : visibleUiCount;
            int finalCount = shellCountAvailable ? Math.Max(shellCount, visibleUiCount) : visibleUiCount;
            int hiddenOrFiltered = Math.Max(0, finalCount - visibleUiCount);

            bool reliable = shellCountAvailable || visibleUiCount > 0;
            string reliabilityNote = reliable
                ? shellCountAvailable
                    ? "Desktop count is reliable from shell list view item count."
                    : "Desktop count is reliable from visible UIA list items (shell item count unavailable)."
                : "Desktop FolderView could not be resolved by shell or UIA; exact desktop icon count unavailable.";
            string strategy = shellCountAvailable
                ? $"{hostStrategy}+shell-count"
                : listViewLocated
                    ? $"{hostStrategy}+uia-visible-fallback"
                    : "uia-root-fallback";

            return new DesktopIconCountSnapshot(
                Count: finalCount,
                SourceItemCount: sourceItemCount,
                VisibleUiItemCount: visibleUiCount,
                HiddenOrFilteredCount: hiddenOrFiltered,
                IsReliable: reliable,
                ReliabilityNote: reliabilityNote,
                SourceStrategy: strategy,
                SourcePolicy: "Desktop policy: shell FolderView item count when available; otherwise visible UIA list items. UIA remains the source for labels/positions.",
                VisibleItems: visibleItems);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or TimeoutException)
        {
            Trace.WriteLine($"get_desktop_icon_count failed: {ex.Message}");
            return new DesktopIconCountSnapshot(
                Count: 0,
                SourceItemCount: 0,
                VisibleUiItemCount: 0,
                HiddenOrFilteredCount: 0,
                IsReliable: false,
                ReliabilityNote: "Desktop automation failed while reading FolderView.",
                SourceStrategy: "error",
                SourcePolicy: "Desktop policy: shell FolderView item count when available; otherwise visible UIA list items. UIA remains the source for labels/positions.",
                VisibleItems: Array.Empty<DesktopItemSnapshot>());
        }
    }

    public TaskbarItemCountSnapshot GetTaskbarItemCount()
    {
        try
        {
            AutomationElement? taskbar = FindByClassName("Shell_TrayWnd");
            if (taskbar is null)
            {
                return new TaskbarItemCountSnapshot(
                    Count: 0,
                    AppButtons: 0,
                    TrayButtons: 0,
                    SystemButtons: 0,
                    IsReliable: false,
                    ReliabilityNote: "Taskbar host was not found.",
                    SourceStrategy: "host-missing",
                    SourcePolicy: "Taskbar policy: app buttons only; Start/Search/Widgets/system tray/clock/overflow excluded.",
                    AppItems: Array.Empty<DesktopItemSnapshot>());
            }

            (IReadOnlyList<DesktopItemSnapshot> taskListItems, bool taskListFound) = ListVisibleButtonsInTaskList(taskbar);
            IReadOnlyList<AutomationElement> allButtons = FindVisibleButtons(taskbar);
            int totalVisibleButtons = ToUniqueItems(allButtons).Count;

            IReadOnlyList<AutomationElement> trayButtonElements = allButtons
                .Where(button => IsDescendantOfAnyClass(button, TrayClassNames))
                .ToArray();
            int trayButtons = ToUniqueItems(trayButtonElements).Count;

            IReadOnlyList<DesktopItemSnapshot> fallbackAppItems = ToUniqueItems(allButtons
                .Where(button => !IsDescendantOfAnyClass(button, TrayClassNames))
                .Where(button => !IsLikelySystemTaskbarButton(button))
                .ToArray());

            IReadOnlyList<DesktopItemSnapshot> appItems;
            string strategy;
            if (taskListItems.Count > 0)
            {
                appItems = taskListItems;
                strategy = "tasklist-class";
            }
            else if (fallbackAppItems.Count > 0)
            {
                appItems = fallbackAppItems;
                strategy = "filtered-visible-buttons";
            }
            else
            {
                appItems = Array.Empty<DesktopItemSnapshot>();
                strategy = taskListFound ? "tasklist-empty" : "no-app-button-strategy";
            }

            int systemButtons = Math.Max(0, totalVisibleButtons - appItems.Count - trayButtons);
            bool reliable = appItems.Count > 0 || (taskListFound && totalVisibleButtons > 0);
            string reliabilityNote = reliable
                ? appItems.Count > 0
                    ? "Taskbar app count is reliable from task list/fallback app-button detection."
                    : "Task list container was found with no visible app buttons."
                : "Taskbar app-button container was not resolved reliably; exact taskbar app count unavailable.";

            return new TaskbarItemCountSnapshot(
                Count: appItems.Count,
                AppButtons: appItems.Count,
                TrayButtons: trayButtons,
                SystemButtons: systemButtons,
                IsReliable: reliable,
                ReliabilityNote: reliabilityNote,
                SourceStrategy: strategy,
                SourcePolicy: "Taskbar policy: app buttons only; Start/Search/Widgets/system tray/clock/overflow excluded.",
                AppItems: appItems);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or TimeoutException)
        {
            Trace.WriteLine($"get_taskbar_item_count failed: {ex.Message}");
            return new TaskbarItemCountSnapshot(
                Count: 0,
                AppButtons: 0,
                TrayButtons: 0,
                SystemButtons: 0,
                IsReliable: false,
                ReliabilityNote: "Taskbar automation failed while collecting app buttons.",
                SourceStrategy: "error",
                SourcePolicy: "Taskbar policy: app buttons only; Start/Search/Widgets/system tray/clock/overflow excluded.",
                AppItems: Array.Empty<DesktopItemSnapshot>());
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

    private static (IReadOnlyList<DesktopItemSnapshot> Items, bool FoundTaskListHost) ListVisibleButtonsInTaskList(AutomationElement taskbar)
    {
        Condition taskListCondition = new PropertyCondition(AutomationElement.ClassNameProperty, "MSTaskListWClass");
        AutomationElement? taskList = taskbar.FindFirst(TreeScope.Descendants, taskListCondition);
        if (taskList is null)
        {
            return (Array.Empty<DesktopItemSnapshot>(), false);
        }

        return (ToUniqueItems(FindVisibleButtons(taskList)), true);
    }

    private static IReadOnlyList<DesktopItemSnapshot> ListVisibleDesktopItemsFromHandle(nint listViewHandle)
    {
        try
        {
            AutomationElement listView = RunWithTimeout(() => AutomationElement.FromHandle(listViewHandle));
            Condition itemCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
            return ToUniqueItems(listView.FindAll(TreeScope.Descendants, itemCondition));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or TimeoutException)
        {
            Trace.WriteLine($"desktop icon UIA from handle failed: {ex.Message}");
            return Array.Empty<DesktopItemSnapshot>();
        }
    }

    private static IReadOnlyList<DesktopItemSnapshot> ListVisibleDesktopItemsFallback()
    {
        try
        {
            AutomationElement? folderView = FindDesktopFolderViewByClassOnly();
            if (folderView is null)
            {
                return Array.Empty<DesktopItemSnapshot>();
            }

            Condition itemCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
            return ToUniqueItems(folderView.FindAll(TreeScope.Descendants, itemCondition));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or TimeoutException)
        {
            Trace.WriteLine($"desktop icon fallback UIA failed: {ex.Message}");
            return Array.Empty<DesktopItemSnapshot>();
        }
    }

    private static IReadOnlyList<AutomationElement> FindVisibleButtons(AutomationElement root)
    {
        Condition buttonCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
        AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants, buttonCondition);
        List<AutomationElement> visible = new(buttons.Count);
        foreach (AutomationElement button in buttons)
        {
            Rectangle bounds = ToRectangle(button.Current.BoundingRectangle);
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                visible.Add(button);
            }
        }

        return visible;
    }

    private static bool IsLikelySystemTaskbarButton(AutomationElement button)
    {
        string name = (button.Current.Name ?? string.Empty).Trim().ToLowerInvariant();
        string automationId = (button.Current.AutomationId ?? string.Empty).Trim().ToLowerInvariant();
        string className = (button.Current.ClassName ?? string.Empty).Trim().ToLowerInvariant();

        if (ReservedTaskbarKeywords.Any(keyword => name.Contains(keyword, StringComparison.Ordinal) ||
                                                   automationId.Contains(keyword, StringComparison.Ordinal) ||
                                                   className.Contains(keyword, StringComparison.Ordinal)))
        {
            return true;
        }

        return className.Contains("start", StringComparison.Ordinal) ||
            className.Contains("search", StringComparison.Ordinal) ||
            className.Contains("tray", StringComparison.Ordinal);
    }

    private static bool IsDescendantOfAnyClass(AutomationElement element, IReadOnlySet<string> classNames)
    {
        AutomationElement? current = element;
        while (current is not null)
        {
            string className = current.Current.ClassName ?? string.Empty;
            if (classNames.Contains(className))
            {
                return true;
            }

            current = TreeWalker.ControlViewWalker.GetParent(current);
        }

        return false;
    }

    private static bool TryFindDesktopListViewHandle(out nint listViewHandle, out string strategy)
    {
        listViewHandle = 0;
        strategy = "none";

        nint progman = FindWindowExW(0, 0, "Progman", null);
        if (TryFindDesktopListViewUnderHost(progman, out listViewHandle))
        {
            strategy = "win32-progman";
            return true;
        }

        nint worker = 0;
        while ((worker = FindWindowExW(0, worker, "WorkerW", null)) != 0)
        {
            if (TryFindDesktopListViewUnderHost(worker, out listViewHandle))
            {
                strategy = "win32-workerw";
                return true;
            }
        }

        return false;
    }

    private static bool TryFindDesktopListViewUnderHost(nint host, out nint listViewHandle)
    {
        listViewHandle = 0;
        if (host == 0)
        {
            return false;
        }

        nint defView = FindWindowExW(host, 0, "SHELLDLL_DefView", null);
        if (defView == 0)
        {
            return false;
        }

        listViewHandle = FindWindowExW(defView, 0, "SysListView32", null);
        if (listViewHandle == 0)
        {
            listViewHandle = FindWindowExW(defView, 0, "SysListView32", "FolderView");
        }

        return listViewHandle != 0;
    }

    private static bool TryGetDesktopListViewItemCount(nint listViewHandle, out int count)
    {
        count = 0;
        if (listViewHandle == 0)
        {
            return false;
        }

        nint result = SendMessageW(listViewHandle, LvmGetItemCount, 0, 0);
        if (result.ToInt64() < 0)
        {
            return false;
        }

        count = result.ToInt32();
        return true;
    }

    private static IReadOnlyList<DesktopItemSnapshot> ToUniqueItems(IEnumerable<AutomationElement> elements)
    {
        List<AutomationElement> list = elements.ToList();
        Dictionary<string, DesktopItemSnapshot> unique = new(StringComparer.Ordinal);
        foreach (AutomationElement element in list)
        {
            Rectangle bounds = ToRectangle(element.Current.BoundingRectangle);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            string name = NormalizeName(element.Current.Name);
            string controlType = element.Current.ControlType?.ProgrammaticName ?? "Unknown";
            string key = BuildSnapshotKey(name, bounds);
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

    private static IReadOnlyList<DesktopItemSnapshot> ToUniqueItems(AutomationElementCollection elements)
    {
        List<AutomationElement> list = new(elements.Count);
        foreach (AutomationElement element in elements)
        {
            list.Add(element);
        }

        return ToUniqueItems(list);
    }

    private static string BuildSnapshotKey(string name, Rectangle bounds) =>
        $"{name}|{bounds.X}|{bounds.Y}|{bounds.Width}|{bounds.Height}";

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

    private static AutomationElement? FindDesktopFolderViewByClassOnly()
    {
        Condition condition = new PropertyCondition(AutomationElement.ClassNameProperty, "SysListView32");
        return RunWithTimeout(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition));
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}


