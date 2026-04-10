using System;
using System.Runtime.InteropServices;

namespace TaskbarWidget
{
    public enum TaskbarEdge { Left, Top, Right, Bottom }

    internal sealed class TaskbarState
    {
        public IntPtr      Hwnd        { get; init; }
        public NativeMethods.RECT ScreenRect  { get; init; }
        public NativeMethods.RECT ClientRect  { get; init; }
        public TaskbarEdge Edge        { get; init; }
        public uint        Dpi         { get; init; }
        public double      Scale       { get; init; }

        public bool IsHorizontal => Edge is TaskbarEdge.Bottom or TaskbarEdge.Top;
    }

    internal static class TaskbarInfo
    {
        public const int LogicalWidgetWidth = 180;

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

        public static (int w, int h) GetWidgetPhysicalSize(TaskbarState state)
        {
            int physW = (int)Math.Round(LogicalWidgetWidth * state.Scale);
            int physH = state.IsHorizontal
                ? state.ClientRect.Height
                : state.ClientRect.Width;
            return (physW, physH);
        }

        public static (int x, int y) GetDefaultPosition(TaskbarState state, int widgetW, int widgetH)
        {
            int offset = (int)Math.Round(400 * state.Scale);
            return state.IsHorizontal ? (offset, 0) : (0, offset);
        }

        public static (int x, int y) ClampPosition(
            TaskbarState state, int x, int y, int widgetW, int widgetH)
        {
            int maxX = Math.Max(0, state.ClientRect.Width  - widgetW);
            int maxY = Math.Max(0, state.ClientRect.Height - widgetH);
            return state.IsHorizontal
                ? (Math.Min(Math.Max(x, 0), maxX), 0)
                : (0, Math.Min(Math.Max(y, 0), maxY));
        }

        private static TaskbarEdge DetectEdge()
        {
            var abd = new NativeMethods.APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.APPBARDATA))
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
}
