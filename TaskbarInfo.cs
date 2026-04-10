using System;
using System.Runtime.InteropServices;

namespace TaskbarWidget;

public enum TaskbarEdge { Left, Top, Right, Bottom }

/// <summary>
/// A snapshot of the taskbar's position, size, and DPI at a point in time.
/// All pixel values are physical (Win32) pixels unless noted.
/// </summary>
internal sealed class TaskbarState
{
    public IntPtr      Hwnd        { get; init; }
    public NativeMethods.RECT ScreenRect  { get; init; }  // taskbar screen coords (physical px)
    public NativeMethods.RECT ClientRect  { get; init; }  // taskbar client area (child origin = 0,0)
    public TaskbarEdge Edge        { get; init; }
    public uint        Dpi         { get; init; }
    public double      Scale       { get; init; }          // Dpi / 96.0

    public bool IsHorizontal => Edge is TaskbarEdge.Bottom or TaskbarEdge.Top;
}

internal static class TaskbarInfo
{
    // Logical width of the widget in DIPs at 100% scale.
    // Physical width = LogicalWidgetWidth * state.Scale
    public const int LogicalWidgetWidth = 180;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current taskbar state. Returns null if Shell_TrayWnd is not found
    /// (e.g. during an explorer.exe restart — caller should retry).
    /// </summary>
    public static TaskbarState? GetCurrentState()
    {
        IntPtr hwnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (hwnd == IntPtr.Zero) return null;

        if (!NativeMethods.GetWindowRect(hwnd, out var screenRect)) return null;
        if (!NativeMethods.GetClientRect(hwnd, out var clientRect)) return null;

        var edge  = DetectEdge();
        uint dpi  = GetTaskbarDpi(screenRect);

        return new TaskbarState
        {
            Hwnd       = hwnd,
            ScreenRect = screenRect,
            ClientRect = clientRect,
            Edge       = edge,
            Dpi        = dpi,
            Scale      = dpi / 96.0
        };
    }

    /// <summary>
    /// Physical pixel size of the widget for the given taskbar state.
    /// Height always matches the full taskbar client height so the widget fills it cleanly.
    /// </summary>
    public static (int w, int h) GetWidgetPhysicalSize(TaskbarState state)
    {
        int physW = (int)Math.Round(LogicalWidgetWidth * state.Scale);

        // On horizontal taskbar: widget spans full height.
        // On vertical taskbar:   widget spans full width, height = scaled logical width.
        int physH = state.IsHorizontal
            ? state.ClientRect.Height
            : state.ClientRect.Width;

        return (physW, physH);
    }

    /// <summary>
    /// Default position (in taskbar client coordinates, physical px) for first run.
    /// Places the widget just after the approximate Widgets-button zone.
    /// </summary>
    public static (int x, int y) GetDefaultPosition(TaskbarState state, int widgetW, int widgetH)
    {
        // Offset past the approximate Widgets button region (scaled)
        int offset = (int)Math.Round(400 * state.Scale);

        return state.IsHorizontal
            ? (offset, 0)
            : (0, offset);
    }

    /// <summary>
    /// Clamps a proposed widget position so it stays fully within the taskbar client area.
    /// Drag axis is constrained to the taskbar's primary axis.
    /// </summary>
    public static (int x, int y) ClampPosition(
        TaskbarState state, int x, int y, int widgetW, int widgetH)
    {
        int maxX = Math.Max(0, state.ClientRect.Width  - widgetW);
        int maxY = Math.Max(0, state.ClientRect.Height - widgetH);

        // On horizontal taskbar only X moves; on vertical only Y moves.
        return state.IsHorizontal
            ? (Math.Clamp(x, 0, maxX), 0)
            : (0, Math.Clamp(y, 0, maxY));
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static TaskbarEdge DetectEdge()
    {
        var abd = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>()
        };

        NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref abd);

        return abd.uEdge switch
        {
            NativeMethods.ABE_LEFT  => TaskbarEdge.Left,
            NativeMethods.ABE_TOP   => TaskbarEdge.Top,
            NativeMethods.ABE_RIGHT => TaskbarEdge.Right,
            _                       => TaskbarEdge.Bottom
        };
    }

    private static uint GetTaskbarDpi(NativeMethods.RECT taskbarScreenRect)
    {
        var r = taskbarScreenRect;
        IntPtr monitor = NativeMethods.MonitorFromRect(ref r, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return 96;

        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        return dpiX > 0 ? dpiX : 96;
    }
}
