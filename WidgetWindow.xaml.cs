using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TaskbarWidget;

/// <summary>
/// Transparent HWND_TOPMOST window that sits over the taskbar.
/// Visually indistinguishable from being embedded — transparent background
/// lets the taskbar show through; only the progress bar content is visible.
/// </summary>
public partial class WidgetWindow : Window
{
    private static uint _taskbarCreatedMsg;

    // Current physical-pixel screen position and size
    private int _physX, _physY, _physW, _physH;

    // Drag state (all in physical screen pixels)
    private bool _pendingDrag;              // mouse down, not yet moved enough to be a drag
    private bool _dragging;                 // actively dragging
    private bool _dragFeedbackShown = false;// drag feedback (shadow/lift) shown once
    private int  _dragOriginMouseX, _dragOriginMouseY;
    private int  _dragOriginWinX,   _dragOriginWinY;

    private TaskbarState? _taskbarState;
    private WidgetConfig  _config;
    private System.Windows.Threading.DispatcherTimer? _topmostTimer;
    private bool _snapBackActive;

    /// <summary>
    /// Fired when the user clicks the widget without dragging (triggers a usage refresh).
    /// </summary>
    public event Action? RefreshRequested;

    /// <summary>
    /// Fired when a drag ends — triggers a silent background refresh (no loading indicator).
    /// </summary>
    public event Action? SilentRefreshRequested;

    public WidgetWindow(WidgetConfig config)
    {
        InitializeComponent();
        _config = config;
        Widget.DragRequested += BeginDrag;
        // Refresh is now fired immediately on click, not after animation
        Loaded            += OnLoaded;
        IsVisibleChanged  += OnVisibilityChanged;
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LogDebug("OnLoaded: starting");
        LogDebug($"OnLoaded: SystemParameters.PrimaryScreenWidth={SystemParameters.PrimaryScreenWidth}, Height={SystemParameters.PrimaryScreenHeight}");
        LogDebug($"OnLoaded: SystemParameters.WorkArea={SystemParameters.WorkArea}");

        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        LogDebug($"OnLoaded: taskbar message ID = {_taskbarCreatedMsg}");

        var hwnd = new WindowInteropHelper(this).Handle;
        LogDebug($"OnLoaded: hwnd = {hwnd}");

        // WS_EX_TOOLWINDOW  → no taskbar button, hidden from Alt+Tab
        // WS_EX_NOACTIVATE  → never steals keyboard focus
        // WS_EX_TOPMOST     → baked into the style so it survives resets
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= (int)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
        LogDebug("OnLoaded: set window styles");

        // Hook window messages
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        LogDebug("OnLoaded: hooked window messages");

        // Position over the taskbar
        LogDebug("OnLoaded: calling Reposition()");
        Reposition();
        LogDebug("OnLoaded: Reposition() done");

        // Reassert TOPMOST every 50ms — taskbar can put itself above us without triggering our hooks
        _topmostTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;

                if (_dragging || _snapBackActive)
                {
                    // Assert TOPMOST/visible without overriding animated position
                    NativeMethods.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                    return;
                }

                NativeMethods.SetWindowPos(h, new IntPtr(-1), _physX, _physY, _physW, _physH,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            },
            Dispatcher);

        LogDebug("OnLoaded: complete");
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        bool visible = (bool)e.NewValue;
        LogDebug($"IsVisibleChanged: visible={visible} — reasserting");
        if (!visible)
        {
            // Immediately fight back — show and reposition
            Show();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                NativeMethods.SetWindowPos(hwnd, new IntPtr(-1),
                    _physX, _physY, _physW, _physH,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    // ── Positioning ───────────────────────────────────────────────────────────

    /// <summary>
    /// Re-reads the taskbar state and moves/resizes the window to match.
    /// Called on startup and whenever the taskbar changes.
    /// </summary>
    public void Reposition()
    {
        LogDebug("Reposition: starting");
        _taskbarState = TaskbarInfo.GetCurrentState();
        LogDebug($"Reposition: taskbarState = {(_taskbarState is not null ? "found" : "null")}");
        if (_taskbarState is null)
        {
            LogDebug("Reposition: taskbar not found, returning");
            return;
        }

        (_physW, _physH) = TaskbarInfo.GetWidgetPhysicalSize(_taskbarState);
        LogDebug($"Reposition: physical size = {_physW}x{_physH}");

        // Restore saved position — prefer DPI-independent fraction, fall back to legacy physical px
        int relX;
        if (_config.PositionFraction.HasValue)
        {
            int available = _taskbarState.IsHorizontal
                ? _taskbarState.ScreenRect.Width  - _physW
                : _taskbarState.ScreenRect.Height - _physH;
            relX = (int)Math.Round(Math.Clamp(_config.PositionFraction.Value, 0.0, 1.0) * Math.Max(0, available));
        }
        else
        {
            relX = _config.PositionX ?? (int)(400 * _taskbarState.Scale);
        }

        if (_taskbarState.IsHorizontal)
        {
            _physX = _taskbarState.ScreenRect.Left + relX;
            _physY = _taskbarState.ScreenRect.Top;
        }
        else
        {
            _physX = _taskbarState.ScreenRect.Left;
            _physY = _taskbarState.ScreenRect.Top + relX;
        }

        // Clamp so widget stays within taskbar span (both axes handled)
        if (_taskbarState.IsHorizontal)
        {
            _physX = Math.Clamp(_physX,
                _taskbarState.ScreenRect.Left,
                _taskbarState.ScreenRect.Right - _physW);
        }
        else
        {
            _physY = Math.Clamp(_physY,
                _taskbarState.ScreenRect.Top,
                _taskbarState.ScreenRect.Bottom - _physH);
        }

        LogDebug($"Reposition: before ApplyPosition, taskbar rect = {_taskbarState.ScreenRect.Left},{_taskbarState.ScreenRect.Top},{_taskbarState.ScreenRect.Right},{_taskbarState.ScreenRect.Bottom}");
        ApplyPosition();
    }

    private void ApplyPosition()
    {
        LogDebug($"ApplyPosition: position={_physX},{_physY} size={_physW}x{_physH}");
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            LogDebug("ApplyPosition: hwnd is zero, returning");
            return;
        }

        // HWND_TOPMOST = -1 ensures we are always above the taskbar in z-order
        NativeMethods.SetWindowPos(hwnd, new IntPtr(-1),
            _physX, _physY, _physW, _physH,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        LogDebug("ApplyPosition: SetWindowPos called");
    }

    // ── Drag ──────────────────────────────────────────────────────────────────

    private void BeginDrag()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return;

        LogDebug($"BeginDrag: cursor=({cursor.X},{cursor.Y}) bounds=({_physX},{_physY})-({_physX+_physW},{_physY+_physH})");

        // Hit-test: only start drag if click is within widget bounds (sprite + bar + labels)
        // Window spans _physX to _physX+_physW, _physY to _physY+_physH
        if (cursor.X < _physX || cursor.X > _physX + _physW ||
            cursor.Y < _physY || cursor.Y > _physY + _physH)
        {
            LogDebug($"BeginDrag: click outside bounds, ignoring");
            return;
        }

        // Don't commit to a drag immediately — wait to see if the mouse moves.
        // If mouse-up arrives before we cross the threshold, it is a click.
        _pendingDrag       = true;
        _dragging          = false;
        _dragFeedbackShown = false;
        _dragOriginMouseX  = cursor.X;
        _dragOriginMouseY  = cursor.Y;
        _dragOriginWinX    = _physX;
        _dragOriginWinY    = _physY;
        NativeMethods.SetCapture(new WindowInteropHelper(this).Handle);
    }

    private void UpdateDrag(int screenX, int screenY)
    {
        if (_taskbarState is null) return;

        int dx = screenX - _dragOriginMouseX;
        int dy = screenY - _dragOriginMouseY;

        // Show drag feedback (shadow, lift) if we've moved enough to commit to a drag
        if (_dragging && !_dragFeedbackShown)
        {
            _dragFeedbackShown = true;
            var shadowAnim = new System.Windows.Media.Animation.DoubleAnimation(0.4, TimeSpan.FromMilliseconds(200));
            Widget.DragShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, shadowAnim);

            var liftAnim = new System.Windows.Media.Animation.ThicknessAnimation(
                new Thickness(0, -6, 0, 6),
                new Thickness(0),
                TimeSpan.FromMilliseconds(200));
            Widget.RootGrid.BeginAnimation(System.Windows.Controls.Grid.MarginProperty, liftAnim);
        }

        if (_taskbarState.IsHorizontal)
        {
            _physX = Math.Clamp(_dragOriginWinX + dx,
                _taskbarState.ScreenRect.Left,
                _taskbarState.ScreenRect.Right - _physW);
            _physY = _taskbarState.ScreenRect.Top;
        }
        else
        {
            _physX = _taskbarState.ScreenRect.Left;
            _physY = Math.Clamp(_dragOriginWinY + dy,
                _taskbarState.ScreenRect.Top,
                _taskbarState.ScreenRect.Bottom - _physH);
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, new IntPtr(-1),
            _physX, _physY, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void EndDrag()
    {
        _dragging = false;
        _dragFeedbackShown = false;
        NativeMethods.ReleaseCapture();

        int dropX = _physX, dropY = _physY;
        bool obstructed = OverlapsTaskbarElement(dropX, dropY, _physW, _physH);
        LogDebug($"EndDrag: pos=({dropX},{dropY}) obstructed={obstructed} origin=({_dragOriginWinX},{_dragOriginWinY})");

        if (obstructed)
        {
            // Set _phys to destination FIRST so the 50ms timer won't fight the animation
            _physX = _dragOriginWinX;
            _physY = _dragOriginWinY;
            // _snapBackActive prevents the timer from overriding the animated position
            AnimateWindowSnapBack(dropX, dropY, _dragOriginWinX, _dragOriginWinY);
        }
        else
        {
            var hwnd2 = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(hwnd2, new IntPtr(-1),
                _physX, _physY, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // Reset drag feedback (shadow, lift) and animation
        Widget.ResetDragFeedback();
        Widget.StopDragAnimation();
        SilentRefreshRequested?.Invoke();

        // Save position as DPI-independent fraction of available drag space
        if (_taskbarState is not null)
        {
            int rawOffset = _taskbarState.IsHorizontal
                ? _physX - _taskbarState.ScreenRect.Left
                : _physY - _taskbarState.ScreenRect.Top;
            int available = _taskbarState.IsHorizontal
                ? _taskbarState.ScreenRect.Width  - _physW
                : _taskbarState.ScreenRect.Height - _physH;

            _config.PositionX        = rawOffset;  // legacy fallback
            _config.PositionFraction = available > 0
                ? Math.Clamp(rawOffset / (double)available, 0.0, 1.0)
                : 0.0;
            ConfigStore.Save(_config);
        }
    }

    // ── WndProc ───────────────────────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            switch (msg)
            {
                case NativeMethods.WM_MOUSEMOVE when _pendingDrag || _dragging:
                    if (NativeMethods.GetCursorPos(out var pt))
                    {
                        if (_pendingDrag)
                        {
                            // Promote to a real drag once the mouse has moved ≥5 physical pixels
                            int dx = Math.Abs(pt.X - _dragOriginMouseX);
                            int dy = Math.Abs(pt.Y - _dragOriginMouseY);
                            if (dx >= 5 || dy >= 5)
                            {
                                _pendingDrag = false;
                                _dragging    = true;
                                Widget.PlayDragAnimation();
                            }
                        }

                        if (_dragging)
                            UpdateDrag(pt.X, pt.Y);
                    }
                    handled = true;
                    break;

                case NativeMethods.WM_LBUTTONUP when _pendingDrag:
                    // Mouse up without enough movement — confirmed click
                    _pendingDrag = false;
                    NativeMethods.ReleaseCapture();
                    Widget.ResetDragFeedback();
                    RefreshRequested?.Invoke();    // fire refresh immediately
                    Widget.PlayClickAnimation();   // animation runs in parallel
                    handled = true;
                    break;

                case NativeMethods.WM_LBUTTONUP when _dragging:
                    EndDrag();
                    handled = true;
                    break;

                case NativeMethods.WM_ACTIVATEAPP:
                    // Any app activation/deactivation — reassert we're above the taskbar
                    Dispatcher.BeginInvoke(() =>
                    {
                        var h = new WindowInteropHelper(this).Handle;
                        if (h != IntPtr.Zero)
                            NativeMethods.SetWindowPos(h, new IntPtr(-1),
                                _physX, _physY, _physW, _physH,
                                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                    });
                    break;

                case NativeMethods.WM_SHOWWINDOW:
                    LogDebug($"WM_SHOWWINDOW: wParam={wParam} (0=hide, 1=show)");
                    if (wParam == IntPtr.Zero)
                    {
                        // Something is trying to hide us — force visible
                        LogDebug("WM_SHOWWINDOW: hide intercepted, reasserting visible");
                        Dispatcher.BeginInvoke(() =>
                        {
                            var h = new WindowInteropHelper(this).Handle;
                            NativeMethods.SetWindowPos(h, new IntPtr(-1),
                                _physX, _physY, _physW, _physH,
                                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                        });
                    }
                    break;

                case NativeMethods.WM_NCHITTEST:
                    // Return HTTRANSPARENT for empty/transparent areas so clicks pass to taskbar cleanly
                    if (NativeMethods.GetCursorPos(out var htPt))
                    {
                        bool inWidget = htPt.X >= _physX && htPt.X <= _physX + _physW &&
                                        htPt.Y >= _physY && htPt.Y <= _physY + _physH;
                        if (!inWidget)
                        {
                            handled = true;
                            return new IntPtr(NativeMethods.HTTRANSPARENT);
                        }
                    }
                    break;

                case NativeMethods.WM_WINDOWPOSCHANGING:
                    // Block any attempt to move us behind other windows or hide us
                    var wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                    LogDebug($"WM_WINDOWPOSCHANGING: flags={wp.flags:X8} hideWindow={(wp.flags & NativeMethods.SWP_HIDEWINDOW) != 0}");
                    wp.hwndInsertAfter  = new IntPtr(-1);   // HWND_TOPMOST
                    wp.flags           &= ~NativeMethods.SWP_NOZORDER;
                    wp.flags           &= ~NativeMethods.SWP_HIDEWINDOW;
                    Marshal.StructureToPtr(wp, lParam, false);
                    LogDebug($"WM_WINDOWPOSCHANGING: corrected flags={wp.flags:X8}");
                    break;

                case NativeMethods.WM_DPICHANGED:
                case NativeMethods.WM_SETTINGCHANGE:
                case NativeMethods.WM_DISPLAYCHANGE:
                    Dispatcher.BeginInvoke(Reposition);
                    break;
            }

            if (msg == _taskbarCreatedMsg)
            {
                // Explorer restarted — taskbar is a new HWND, reposition
                Dispatcher.BeginInvoke(Reposition);
                handled = true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WndProc error: {ex.Message}");
        }

        return IntPtr.Zero;
    }

    // ── Overlap detection ────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the given rect would overlap any real taskbar child window
    /// (Start, pinned apps, tray, search, etc.). Skips zero-size and full-width overlay windows.
    /// </summary>
    private bool OverlapsTaskbarElement(int x, int y, int w, int h)
    {
        IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;
        if (!NativeMethods.GetWindowRect(tray, out var trayBounds)) return false;

        var our = new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        bool hit = false;

        bool Callback(IntPtr hwnd, IntPtr lp)
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;
            // Skip zero-size and full-taskbar-width overlay windows (CoreWindow, DesktopWindowContentBridge)
            if (r.Width <= 0 || r.Height <= 0 || r.Width >= trayBounds.Width) return true;
            if (Overlaps(our, r)) { hit = true; return false; }
            return true;
        }

        NativeMethods.EnumChildWindows(tray, Callback, IntPtr.Zero);
        return hit;
    }

    private static bool Overlaps(NativeMethods.RECT a, NativeMethods.RECT b)
        => !(a.Right <= b.Left || a.Left >= b.Right || a.Bottom <= b.Top || a.Top >= b.Bottom);

    // ── Snap-back animation ───────────────────────────────────────────────────

    private void AnimateWindowSnapBack(int fromX, int fromY, int toX, int toY)
    {
        _snapBackActive = true;
        var startTime = DateTime.UtcNow;
        const double durationMs = 450;
        var hwnd = new WindowInteropHelper(this).Handle;
        int frameCount = 0;

        LogDebug($"SnapBack: START from ({fromX},{fromY}) to ({toX},{toY})");

        var timer = new System.Windows.Threading.DispatcherTimer();
        timer.Interval = TimeSpan.FromMilliseconds(12);
        timer.Tick += (_, _) =>
        {
            double t = Math.Min((DateTime.UtcNow - startTime).TotalMilliseconds / durationMs, 1.0);
            double e = ElasticEaseOut(t);
            int x = (int)Math.Round(fromX + (toX - fromX) * e);
            int y = (int)Math.Round(fromY + (toY - fromY) * e);
            frameCount++;
            NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            if (t >= 1.0)
            {
                timer.Stop();
                _snapBackActive = false;
                LogDebug($"SnapBack: DONE frames={frameCount} final=({x},{y})");
            }
        };
        timer.Start();
    }

    private static double ElasticEaseOut(double t)
    {
        if (t is 0 or 1) return t;
        const double p = 0.6;   // longer period = fewer oscillations
        const double s = p / 4.0;
        return Math.Pow(2, -12 * t) * Math.Sin((t - s) * (2 * Math.PI) / p) + 1;
    }

    // ── Control access ────────────────────────────────────────────────────────

    public void SetValue(double value)                        => Widget.SetValue(value);
    public void SetError()                                    => Widget.SetError();
    public void SetLoading(bool loading)                      => Widget.SetLoading(loading);
    public void SetResetTime(string? resetTime)               => Widget.SetResetTime(resetTime);

    // ── Debug logging ─────────────────────────────────────────────────────────

    private static void LogDebug(string msg)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TaskbarWidget", "debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch { }
    }
}
