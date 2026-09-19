using System.Drawing;

namespace GeminiLiveShare.Core.Desktop;

public interface IDesktopAutomationService
{
    DesktopElementSnapshot? GetElementUnderCursor();

    IReadOnlyList<DesktopItemSnapshot> ListTaskbarItems();

    IReadOnlyList<DesktopItemSnapshot> ListDesktopIcons();

    FocusedWindowSnapshot? GetFocusedWindow();
}

public sealed record DesktopElementSnapshot(
    string Name,
    string ControlType,
    IReadOnlyList<string> ParentPath,
    Rectangle Bounds);

public sealed record DesktopItemSnapshot(
    string Name,
    string ControlType,
    Rectangle Bounds);

public sealed record FocusedWindowSnapshot(
    string AppName,
    string Title,
    DesktopElementSnapshot? FocusedElement);
