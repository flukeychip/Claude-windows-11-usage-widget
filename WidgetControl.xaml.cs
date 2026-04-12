using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace TaskbarWidget
{
    public partial class WidgetControl : UserControl
    {
        public event Action? DragRequested;

        private MenuItem? _autoStartItem;
        private MenuItem? _exitItem;

        private double _targetValue = 0;
        private bool   _isError     = false;

        private System.Windows.Threading.DispatcherTimer? _dragAnimTimer;
        private static readonly Random _rng = new Random();

        private static readonly string[] PointFrames = new[] { "point_00.png", "point_01.png", "point_02.png", "point_03.png", "point_04.png", "point_05.png", "point_01.png", "point_06.png" };
        private static readonly int[]    PointDelays = new[] { 80, 90, 110, 160, 25, 40, 35, 55 };
        private static readonly string[] MoveFrames  = new[] { "move_00.png", "move_01.png", "move_02.png", "move_03.png", "move_04.png", "move_05.png" };

        private static readonly (string[] f, int[] d) BounceClip = (
            new[] { "idle_bounce_00.png", "idle_bounce_01.png" },
            new[] { 300, 300 });

        private static readonly (string[] f, int[] d) BlinkClip = (
            new[] { "idle_bounce_00.png", "idle2_02.png", "idle2_03.png", "idle_bounce_00.png" },
            new[] { 100, 80, 80, 100 });

        private static readonly (string[] f, int[] d) MarchClip = (
            new[] { "idle2_04.png", "idle2_05.png", "idle2_06.png", "idle2_07.png" },
            new[] { 130, 130, 130, 400 });

        private static readonly (string[] f, int[] d) WiggleClip = (
            new[] { "idle2_09.png", "idle2_10.png", "idle2_09.png", "idle2_11.png" },
            new[] { 120, 120, 120, 400 });

        private static readonly (string[] f, int[] d) TiltClip = (
            new[] { "idle_tilt_00.png", "idle_tilt_01.png", "idle_tilt_00.png", "idle_tilt_02.png", "idle_tilt_00.png" },
            new[] { 200, 180, 200, 180, 200 });

        private static readonly (string[] f, int[] d) SlowBlinkClip = (
            new[] { "idle_bounce_00.png", "idle2_02.png", "idle2_03.png", "idle2_03.png", "idle2_02.png", "idle_bounce_00.png" },
            new[] { 300, 150, 200, 200, 150, 400 });

        private static readonly Duration AnimDuration = new Duration(TimeSpan.FromMilliseconds(350));

        public WidgetControl()
        {
            InitializeComponent();

            if (RootGrid.ContextMenu is ContextMenu cm)
            {
                _autoStartItem = cm.Items[0] as MenuItem;
                _exitItem      = cm.Items[2] as MenuItem;

                if (_exitItem is not null)
                    _exitItem.Click += (_, _) => Application.Current.Shutdown();

                if (_autoStartItem is not null)
                    _autoStartItem.Click += OnAutoStartClicked;
            }

            PlayStartupAnimation();
        }

        private async void PlayStartupAnimation()
        {
            _idleStopTcs = new TaskCompletionSource<bool>();
            for (int i = 0; i < 3; i++)
                if (!await PlayClip(SlowBlinkClip)) return;
            StartIdleBounceAnimation();
        }

        private void StartIdleBounceAnimation()
        {
            _idleStopTcs = new TaskCompletionSource<bool>();
            RunIdleStateMachine();
        }

        private async void RunIdleStateMachine()
        {
            while (true)
            {
                int bounces = _rng.Next(2, 6);
                for (int i = 0; i < bounces; i++)
                {
                    if (!await PlayClipFrame(BounceClip, 0)) return;
                    if (!await PlayClipFrame(BounceClip, 1)) return;
                }

                int roll = _rng.Next(100);
                if (roll < 45) continue;
                else if (roll < 80)
                {
                    if (!await PlayClipFrame(BlinkClip, 1)) return;
                }
                else if (roll < 92)
                {
                    if (!await PlayClip(TiltClip)) return;
                }
                else if (roll < 97)
                {
                    int loops = _rng.Next(2, 4);
                    for (int j = 0; j < loops; j++)
                        if (!await PlayClip(MarchClip)) return;
                }
                else
                {
                    int loops = _rng.Next(2, 4);
                    for (int j = 0; j < loops; j++)
                        if (!await PlayClip(WiggleClip)) return;
                }
            }
        }

        private TaskCompletionSource<bool>? _idleStopTcs;

        private async Task<bool> PlayClipFrame((string[] f, int[] d) clip, int idx)
        {
            if (_idleStopTcs?.Task.IsCompleted == true) return false;
            SetSpriteFrame(clip.f[idx]);
            var delay = Task.Delay(clip.d[idx]);
            var stop  = _idleStopTcs?.Task ?? Task.CompletedTask;
            var won   = await Task.WhenAny(delay, stop);
            return won == delay;
        }

        private async Task<bool> PlayClip((string[] f, int[] d) clip)
        {
            for (int i = 0; i < clip.f.Length; i++)
                if (!await PlayClipFrame(clip, i)) return false;
            return true;
        }

        private void StopIdleBounceAnimation()
        {
            _idleStopTcs?.TrySetResult(false);
        }

        public void PlayDragAnimation()
        {
            StopIdleBounceAnimation();
            _dragAnimTimer?.Stop();

            int frame = 0;
            SetSpriteFrame(MoveFrames[0]);
            _dragAnimTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(100),
                System.Windows.Threading.DispatcherPriority.Normal,
                (_, _) =>
                {
                    frame = (frame + 1) % MoveFrames.Length;
                    SetSpriteFrame(MoveFrames[frame]);
                },
                Dispatcher);
        }

        public void StopDragAnimation()
        {
            _dragAnimTimer?.Stop();
            _dragAnimTimer = null;
            StartIdleBounceAnimation();
        }

        public async void PlayClickAnimation()
        {
            StopIdleBounceAnimation();

            for (int i = 0; i < PointFrames.Length; i++)
            {
                SetSpriteFrame(PointFrames[i]);
                if (i == 4) PlayPokeBarAnimation();
                await Task.Delay(PointDelays[i]);
            }

            StartIdleBounceAnimation();
        }

        private void PlayPokeBarAnimation()
        {
            double pokeX = Math.Min(TrackGrid.ActualWidth * 0.10, 16);

            BarGroupTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            BarGroupTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            BarGroupTranslate.X = 0;
            BarGroupTranslate.Y = 0;

            var anticipate = new DoubleAnimation(-3, new Duration(TimeSpan.FromMilliseconds(35)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            anticipate.Completed += (_, _) =>
            {
                var lurch = new DoubleAnimation(pokeX, new Duration(TimeSpan.FromMilliseconds(60)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                lurch.Completed += (_, _) =>
                {
                    var spring = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(520)))
                    {
                        EasingFunction = new ElasticEase
                        {
                            EasingMode   = EasingMode.EaseOut,
                            Oscillations = 3,
                            Springiness  = 3
                        }
                    };
                    BarGroupTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, spring);
                };

                BarGroupTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, lurch);
            };

            BarGroupTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anticipate);

            var yUp = new DoubleAnimation(-5, new Duration(TimeSpan.FromMilliseconds(95)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            yUp.Completed += (_, _) =>
            {
                var yBack = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(480)))
                {
                    EasingFunction = new ElasticEase
                    {
                        EasingMode   = EasingMode.EaseOut,
                        Oscillations = 2,
                        Springiness  = 4
                    }
                };
                BarGroupTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, yBack);
            };

            BarGroupTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, yUp);
        }

        private const double SpritePixelScale = 21.0 / 11.0;

        private void SetSpriteFrame(string frameName)
        {
            try
            {
                var uri = new Uri($"pack://application:,,,/Assets/Sprites/{frameName}");
                var bmp = new System.Windows.Media.Imaging.BitmapImage(uri);
                SpriteImage.Source = bmp;
                SpriteImage.Width  = bmp.PixelWidth * SpritePixelScale;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load sprite frame: {ex.Message}");
            }
        }

        public void SetValue(double value)
        {
            _targetValue = Math.Min(Math.Max(value, 0.0), 1.0);
            _isError     = false;

            int pct = (int)Math.Round(_targetValue * 100);
            PercentLabel.Text       = $"{pct}%";
            PercentLabel.Foreground = new SolidColorBrush(Color.FromRgb(42, 42, 42));

            AnimateFill();
        }

        public void SetLoading(bool loading)
        {
            if (loading)
            {
                PercentLabel.Foreground      = new SolidColorBrush(Color.FromArgb(140, 42, 42, 42));
                ResetCounterLabel.Foreground = new SolidColorBrush(Color.FromArgb(140, 42, 42, 42));
            }
            else
            {
                FillBorder.BeginAnimation(OpacityProperty, null);
                FillBorder.Opacity           = 1.0;
                PercentLabel.Foreground      = new SolidColorBrush(Color.FromRgb(42, 42, 42));
                ResetCounterLabel.Foreground = new SolidColorBrush(Color.FromRgb(42, 42, 42));
            }
        }

        public void SetResetTime(string? resetTime)
        {
            ResetCounterLabel.Text = string.IsNullOrWhiteSpace(resetTime) ? "--:--" : resetTime;
        }

        public void SetError(string statusMsg = "--:--")
        {
            _isError = true;
            PercentLabel.Text            = "—";
            PercentLabel.Foreground      = new SolidColorBrush(Color.FromArgb(100, 42, 42, 42));
            ResetCounterLabel.Text       = statusMsg;
            ResetCounterLabel.Foreground = new SolidColorBrush(Color.FromArgb(140, 42, 42, 42));
            AnimateFillTo(0);
        }

        public void ResetDragFeedback()
        {
            var shadowAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            DragShadow.BeginAnimation(DropShadowEffect.OpacityProperty, shadowAnim);

            var liftAnim = new ThicknessAnimation(new Thickness(0), TimeSpan.FromMilliseconds(150));
            RootGrid.BeginAnimation(MarginProperty, liftAnim);
        }

        private void AnimateFill()
        {
            if (!_isError)
                AnimateFillTo(_targetValue);
        }

        private void AnimateFillTo(double fraction)
        {
            double trackWidth = TrackGrid.ActualWidth;
            if (trackWidth <= 0) return;

            var anim = new DoubleAnimation(trackWidth * fraction, AnimDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            FillBorder.BeginAnimation(WidthProperty, anim);
        }

        private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e)
        {
            double trackWidth  = e.NewSize.Width;
            double targetWidth = _isError ? 0 : trackWidth * _targetValue;
            FillBorder.BeginAnimation(WidthProperty, null);
            FillBorder.Width = Math.Max(0, targetWidth);
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragRequested?.Invoke();
            e.Handled = true;
        }

        private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                if (_autoStartItem is not null)
                    _autoStartItem.IsChecked = AutoStartHelper.IsEnabled();
            }
            catch { }
        }

        private void OnAutoStartClicked(object sender, RoutedEventArgs e)
        {
            if (AutoStartHelper.IsEnabled())
                AutoStartHelper.Disable();
            else
                AutoStartHelper.Enable();
        }
    }
}
