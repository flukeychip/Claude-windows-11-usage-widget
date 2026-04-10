using System;
using System.Runtime.InteropServices;

namespace TaskbarWidget;

/// <summary>
/// All Win32 / Shell P/Invoke declarations. Nothing else lives here.
/// </summary>
internal static class NativeMethods
{
    // ── Window style indices ───────────────────────────────────────────────────
    public const int GWL_STYLE   = -16;
    public const int GWL_EXSTYLE = -20;

    // ── Window styles ──────────────────────────────────────────────────────────
    public const uint WS_CHILD        = 0x40000000;
    public const uint WS_VISIBLE      = 0x10000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;

    // ── Extended window styles ─────────────────────────────────────────────────
    public const uint WS_EX_TOOLWINDOW = 0x00000080;   // hides from taskbar & Alt+Tab
    public const uint WS_EX_NOACTIVATE = 0x08000000;   // never steals focus
    public const uint WS_EX_LAYERED    = 0x00080000;   // required for per-pixel opacity
    public const uint WS_EX_TOPMOST    = 0x00000008;   // always above non-topmost windows

    // ── SetWindowPos flags ─────────────────────────────────────────────────────
    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOMOVE     = 0x0002;
    public const uint SWP_NOZORDER   = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;

    // ── Window messages ────────────────────────────────────────────────────────
    public const int WM_SETTINGCHANGE     = 0x001A;
    public const int WM_DISPLAYCHANGE     = 0x007E;
    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_NCHITTEST        = 0x0084;
    public const int WM_SHOWWINDOW       = 0x0018;
    public const int WM_ACTIVATEAPP      = 0x001C;

    // ── WM_NCHITTEST return values ─────────────────────────────────────────────
    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT      =  1;
    public const int WM_DPICHANGED     = 0x02E0;
    public const int WM_LBUTTONDOWN    = 0x0201;
    public const int WM_MOUSEMOVE      = 0x0200;
    public const int WM_LBUTTONUP      = 0x0202;
    public const int WM_RBUTTONUP      = 0x0205;
    public const int WM_DESTROY        = 0x0002;

    // ── AppBar API ─────────────────────────────────────────────────────────────
    public const uint ABM_GETTASKBARPOS = 0x00000005;
    public const uint ABE_LEFT          = 0;
    public const uint ABE_TOP           = 1;
    public const uint ABE_RIGHT         = 2;
    public const uint ABE_BOTTOM        = 3;

    // ── Monitor ───────────────────────────────────────────────────────────────
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int  MDT_EFFECTIVE_DPI        = 0;

    // ── HWND_MESSAGE: message-only window parent ───────────────────────────────
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    // ── WINDOWPOS (used by WM_WINDOWPOSCHANGING) ───────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int    x, y, cx, cy;
        public uint   flags;
    }

    // ── Structs ────────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width  => Right  - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint   cbSize;
        public IntPtr hWnd;
        public uint   uCallbackMessage;
        public uint   uEdge;
        public RECT   rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // ── Functions ──────────────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("shell32.dll")]
    public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(
        IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

}
