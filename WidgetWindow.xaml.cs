using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TaskbarWidget
{
    public partial class WidgetWindow : Window
    {
        private static uint _taskbarCreatedMsg;

        private int _physX, _physY, _physW, _physH;

        private bool     _pendingDrag;
        private bool     _dragging;
        private DateTime _lastClickTime = DateTime.MinValue;
        private bool _dragFeedbackShown = false;
        private int  _dragOriginMouseX, _dragOriginMouseY;
        private int  _dragOriginWinX,   _dragOriginWinY;

        private TaskbarState? _taskbarState;
        private WidgetConfig  _config;
        private System.Windows.Threading.DispatcherTimer? _topmostTimer;
        private System.Windows.Threading.DispatcherTimer? _behaviorTimer;
        private bool _snapBackActive;

        // True when the widget should be suppressed (fullscreen app / taskbar auto-hidden / screenshot)
        private bool _suppressed = false;

        public event Action? RefreshRequested;
        public event Action? SilentRefreshRequested;

        public WidgetWindow(WidgetConfig config)
        {
            InitializeComponent();
            _config = config;
            Widget.DragRequested += BeginDrag;
            Loaded           += OnLoaded;
            IsVisibleChanged += OnVisibilityChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");

            var hwnd = new WindowInteropHelper(this).Handle;

            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle |= (int)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            Reposition();

            _topmostTimer = new System.Windows.Threading.DispatcherTimer();
            _topmostTimer.Interval = TimeSpan.FromMilliseconds(50);
            _topmostTimer.Tick += (_, _) =>
            {
                if (_suppressed) return;  // fullscreen / auto-hide / screenshot active

                var h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;

                if (_dragging || _snapBackActive)
                {
                    NativeMethods.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                    return;
                }

                NativeMethods.SetWindowPos(h, new IntPtr(-1), _physX, _physY, _physW, _physH,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            };
            _topmostTimer.Start();

            // 1-second check: fullscreen, auto-hide taskbar, screenshot overlay
            _behaviorTimer = new System.Windows.Threading.DispatcherTimer();
            _behaviorTimer.Interval = TimeSpan.FromSeconds(1);
            _behaviorTimer.Tick += OnBehaviorTick;
            _behaviorTimer.Start();
        }

        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            bool visible = (bool)e.NewValue;
            if (!visible && !_suppressed)
            {
                Show();
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    NativeMethods.SetWindowPos(hwnd, new IntPtr(-1),
                        _physX, _physY, _physW, _physH,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
        }

        private void OnBehaviorTick(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // --- 1. Full-screen app detection ---
            NativeMethods.SHQueryUserNotificationState(out var quns);
            bool fullscreen = quns == NativeMethods.QUNS.QUNS_RUNNING_D3D_FULL_SCREEN
                           || quns == NativeMethods.QUNS.QUNS_PRESENTATION_MODE;

            // --- 2. Taskbar auto-hide: is taskbar mostly off-screen? ---
            bool taskbarHidden = false;
            var tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero && _taskbarState != null &&
                NativeMethods.GetWindowRect(tray, out var trayRect))
            {
                var sr = _taskbarState.ScreenRect;
                int ix = Math.Max(0, Math.Min(trayRect.Right,  sr.Right)  - Math.Max(trayRect.Left, sr.Left));
                int iy = Math.Max(0, Math.Min(trayRect.Bottom, sr.Bottom) - Math.Max(trayRect.Top,  sr.Top));
                int overlap = ix * iy;
                int area    = Math.Max(1, trayRect.Width * trayRect.Height);
                taskbarHidden = overlap < area / 2;  // less than 50% of taskbar visible
            }

            // --- 3. Screenshot overlay (Snipping Tool / Win+Shift+S) ---
            // ScreenClip is the Win11 Snipping Tool capture overlay
            bool screenshotActive = NativeMethods.FindWindow("ScreenClip",    null) != IntPtr.Zero
                                 || NativeMethods.FindWindow("SnipperWindow", null) != IntPtr.Zero;

            bool shouldSuppress = fullscreen || taskbarHidden || screenshotActive;

            if (shouldSuppress == _suppressed) return;
            _suppressed = shouldSuppress;

            if (_suppressed)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            }
            else
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
                NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), _physX, _physY, _physW, _physH,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
        }

        public void Reposition()
        {
            _taskbarState = TaskbarInfo.GetCurrentState();
            if (_taskbarState is null) return;

            (_physW, _physH) = TaskbarInfo.GetWidgetPhysicalSize(_taskbarState);

            int relX;
            if (_config.PositionFraction.HasValue)
            {
                int available = _taskbarState.IsHorizontal
                    ? _taskbarState.ScreenRect.Width  - _physW
                    : _taskbarState.ScreenRect.Height - _physH;
                relX = (int)Math.Round(Math.Min(Math.Max(_config.PositionFraction.Value, 0.0), 1.0) * Math.Max(0, available));
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

            if (_taskbarState.IsHorizontal)
                _physX = Math.Min(Math.Max(_physX, _taskbarState.ScreenRect.Left), _taskbarState.ScreenRect.Right - _physW);
            else
                _physY = Math.Min(Math.Max(_physY, _taskbarState.ScreenRect.Top), _taskbarState.ScreenRect.Bottom - _physH);

            ApplyPosition();
        }

        private void ApplyPosition()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            NativeMethods.SetWindowPos(hwnd, new IntPtr(-1),
                _physX, _physY, _physW, _physH,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        private void BeginDrag()
        {
            if (!NativeMethods.GetCursorPos(out var cursor)) return;

            if (cursor.X < _physX || cursor.X > _physX + _physW ||
                cursor.Y < _physY || cursor.Y > _physY + _physH)
                return;

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
                _physX = Math.Min(Math.Max(_dragOriginWinX + dx, _taskbarState.ScreenRect.Left), _taskbarState.ScreenRect.Right - _physW);
                _physY = _taskbarState.ScreenRect.Top;
            }
            else
            {
                _physX = _taskbarState.ScreenRect.Left;
                _physY = Math.Min(Math.Max(_dragOriginWinY + dy, _taskbarState.ScreenRect.Top), _taskbarState.ScreenRect.Bottom - _physH);
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), _physX, _physY, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        private void EndDrag()
        {
            _dragging          = false;
            _dragFeedbackShown = false;
            NativeMethods.ReleaseCapture();

            int dropX = _physX, dropY = _physY;
            bool obstructed = OverlapsTaskbarElement(dropX, dropY, _physW, _physH);

            if (obstructed)
            {
                _physX = _dragOriginWinX;
                _physY = _dragOriginWinY;
                AnimateWindowSnapBack(dropX, dropY, _dragOriginWinX, _dragOriginWinY);
            }
            else
            {
                var hwnd2 = new WindowInteropHelper(this).Handle;
                NativeMethods.SetWindowPos(hwnd2, new IntPtr(-1), _physX, _physY, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            Widget.ResetDragFeedback();
            Widget.StopDragAnimation();
            SilentRefreshRequested?.Invoke();

            if (_taskbarState is not null)
            {
                int rawOffset = _taskbarState.IsHorizontal
                    ? _physX - _taskbarState.ScreenRect.Left
                    : _physY - _taskbarState.ScreenRect.Top;
                int available = _taskbarState.IsHorizontal
                    ? _taskbarState.ScreenRect.Width  - _physW
                    : _taskbarState.ScreenRect.Height - _physH;

                _config.PositionX        = rawOffset;
                _config.PositionFraction = available > 0
                    ? Math.Min(Math.Max(rawOffset / (double)available, 0.0), 1.0)
                    : 0.0;
                ConfigStore.Save(_config);
            }
        }

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

                    case NativeMethods.WM_LBUTTONDOWN:
                    {
                        var now = DateTime.UtcNow;
                        var elapsed = (now - _lastClickTime).TotalMilliseconds;
                        _lastClickTime = now;
                        if (elapsed <= NativeMethods.GetDoubleClickTime())
                        {
                            // Second click of a double-click — cancel any pending drag,
                            // play talking animation then open menu
                            _pendingDrag = false;
                            NativeMethods.ReleaseCapture();
                            Widget.ResetDragFeedback();
                            Dispatcher.BeginInvoke(new Action(() =>
                                Widget.PlayTalkAnimation(() => Widget.ShowContextMenu())));
                            handled = true;
                        }
                        break;
                    }

                    case NativeMethods.WM_LBUTTONUP when _pendingDrag:
                        _pendingDrag = false;
                        NativeMethods.ReleaseCapture();
                        Widget.ResetDragFeedback();
                        RefreshRequested?.Invoke();
                        Widget.PlayClickAnimation();
                        handled = true;
                        break;

                    case NativeMethods.WM_RBUTTONUP:
                        // Context menu is on double-click only — suppress right-click
                        handled = true;
                        break;

                    case NativeMethods.WM_LBUTTONUP when _dragging:
                        EndDrag();
                        handled = true;
                        break;

                    case NativeMethods.WM_ACTIVATEAPP:
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            var h = new WindowInteropHelper(this).Handle;
                            if (h != IntPtr.Zero)
                                NativeMethods.SetWindowPos(h, new IntPtr(-1),
                                    _physX, _physY, _physW, _physH,
                                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                        }));
                        break;

                    case NativeMethods.WM_SHOWWINDOW:
                        if (wParam == IntPtr.Zero && !_suppressed)
                        {
                            // Prevent hide synchronously — no BeginInvoke gap
                            handled = true;
                        }
                        break;

                    case NativeMethods.WM_NCHITTEST:
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
                        if (!_suppressed)
                        {
                            var wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                            wp.hwndInsertAfter  = new IntPtr(-1);
                            wp.flags           &= ~NativeMethods.SWP_NOZORDER;
                            wp.flags           &= ~NativeMethods.SWP_HIDEWINDOW;
                            Marshal.StructureToPtr(wp, lParam, false);
                        }
                        break;

                    case NativeMethods.WM_DPICHANGED:
                    case NativeMethods.WM_SETTINGCHANGE:
                    case NativeMethods.WM_DISPLAYCHANGE:
                        Dispatcher.BeginInvoke(new Action(Reposition));
                        break;
                }

                if (msg == _taskbarCreatedMsg)
                {
                    Dispatcher.BeginInvoke(new Action(Reposition));
                    handled = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WndProc error: {ex.Message}");
            }

            return IntPtr.Zero;
        }

        private bool OverlapsTaskbarElement(int x, int y, int w, int h)
        {
            IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;
            if (!NativeMethods.GetWindowRect(tray, out var trayBounds)) return false;

            var our = new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            bool hit = false;

            NativeMethods.EnumChildWindows(tray, (hwnd2, lp) =>
            {
                if (!NativeMethods.GetWindowRect(hwnd2, out var r)) return true;
                if (r.Width <= 0 || r.Height <= 0 || r.Width >= trayBounds.Width) return true;
                if (Overlaps(our, r)) { hit = true; return false; }
                return true;
            }, IntPtr.Zero);

            return hit;
        }

        private static bool Overlaps(NativeMethods.RECT a, NativeMethods.RECT b)
            => !(a.Right <= b.Left || a.Left >= b.Right || a.Bottom <= b.Top || a.Top >= b.Bottom);

        private void AnimateWindowSnapBack(int fromX, int fromY, int toX, int toY)
        {
            _snapBackActive = true;
            var startTime = DateTime.UtcNow;
            const double durationMs = 450;
            var hwnd = new WindowInteropHelper(this).Handle;

            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(12);
            timer.Tick += (_, _) =>
            {
                double t = Math.Min((DateTime.UtcNow - startTime).TotalMilliseconds / durationMs, 1.0);
                double e = ElasticEaseOut(t);
                int x = (int)Math.Round(fromX + (toX - fromX) * e);
                int y = (int)Math.Round(fromY + (toY - fromY) * e);
                NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), x, y, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                if (t >= 1.0)
                {
                    timer.Stop();
                    _snapBackActive = false;
                }
            };
            timer.Start();
        }

        private static double ElasticEaseOut(double t)
        {
            if (t == 0 || t == 1) return t;
            const double p = 0.6;
            const double s = p / 4.0;
            return Math.Pow(2, -12 * t) * Math.Sin((t - s) * (2 * Math.PI) / p) + 1;
        }

        public void SetValue(double value, bool isWeekly = false)   => Widget.SetValue(value, isWeekly);
        public void SetError(string msg = "--:--")                 => Widget.SetError(msg);
        public void SetLoading(bool loading)                       => Widget.SetLoading(loading);
        public void SetResetTime(string? rt)                       => Widget.SetResetTime(rt);
        public void ShowUpdateAvailable(string tag, string? installerUrl = null) => Widget.ShowUpdateAvailable(tag, installerUrl);
        public void ShowContextMenu()                               => Widget.ShowContextMenu();

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
}
