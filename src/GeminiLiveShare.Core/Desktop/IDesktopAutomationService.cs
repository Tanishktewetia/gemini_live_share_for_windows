using System.Drawing;

namespace GeminiLiveShare.Core.Desktop;

public interface IDesktopAutomationService
{
    DesktopElementSnapshot? GetElementUnderCursor();

    IReadOnlyList<DesktopItemSnapshot> ListTaskbarItems();

    IReadOnlyList<DesktopItemSnapshot> ListDesktopIcons();

    DesktopIconCountSnapshot GetDesktopIconCount();

    TaskbarItemCountSnapshot GetTaskbarItemCount();

    IReadOnlyList<DesktopItemSnapshot> FindElementsByNameRole(string name, string? role);

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

public sealed record DesktopIconCountSnapshot(
    int Count,
    int SourceItemCount,
    int VisibleUiItemCount,
    int HiddenOrFilteredCount,
    bool IsReliable,
    string ReliabilityNote,
    string SourceStrategy,
    string SourcePolicy,
    IReadOnlyList<DesktopItemSnapshot> VisibleItems);

public sealed record TaskbarItemCountSnapshot(
    int Count,
    int AppButtons,
    int TrayButtons,
    int SystemButtons,
    bool IsReliable,
    string ReliabilityNote,
    string SourceStrategy,
    string SourcePolicy,
    IReadOnlyList<DesktopItemSnapshot> AppItems);

public sealed record FocusedWindowSnapshot(
    string AppName,
    string Title,
    DesktopElementSnapshot? FocusedElement);
