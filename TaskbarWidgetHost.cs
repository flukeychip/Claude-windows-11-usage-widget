using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TaskbarWidget
{
    internal sealed class TaskbarWidgetHost : IDisposable
    {
        private WidgetWindow? _window;
        private WidgetConfig  _config = new WidgetConfig();
        private CancellationTokenSource _cts = new CancellationTokenSource();

        // Countdown timer
        private DispatcherTimer? _countdownTimer;
        private DateTime?        _resetTarget;

        // Auto-refresh every 5 minutes
        private DispatcherTimer? _autoRefreshTimer;

        // Startup retry — back off if network isn't ready at boot
        private bool _startupSucceeded  = false;
        private int  _startupRetryCount = 0;
        private static readonly int[] StartupRetryDelaysMs = { 5_000, 15_000, 45_000 };

        // ── Startup ───────────────────────────────────────────────────────────

        public void Initialize()
        {
            // Run below normal priority — widget should never compete with user apps
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                      System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }

            _config = ConfigStore.Load();
            AutoStartHelper.PromptIfNeeded(_config);

            ApiService.SetUsagePercentage(_config.UsagePercentage ?? 0.0);

            _window = new WidgetWindow(_config);
            _window.RefreshRequested       += OnRefreshRequested;
            _window.SilentRefreshRequested += OnSilentRefreshRequested;
            _window.Show();

            _window.SetValue(_config.UsagePercentage ?? 0.0);
            OnSilentRefreshRequested();
            _ = CheckForUpdateAsync();

            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _autoRefreshTimer.Tick += (_, __) =>
            {
                OnSilentRefreshRequested();
                // Trim working set — widget is mostly idle, no need to hold pages in RAM
                try { NativeMethods.EmptyWorkingSet(
                          System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
            };
            _autoRefreshTimer.Start();
        }

        // ── Update check ─────────────────────────────────────────────────────

        private async Task CheckForUpdateAsync()
        {
            var (available, tag, installerUrl) = await UpdateService.CheckAsync();
            if (!available || tag == null) return;

            Application.Current?.Dispatcher.Invoke(() =>
                _window?.ShowUpdateAvailable(tag, installerUrl));
        }

        // ── Refresh ───────────────────────────────────────────────────────────

        private async void OnRefreshRequested()
        {
            if (_window is null) return;

            if (ApiService.IsFetching) return;

            _window.SetLoading(true);

            await ApiService.RefreshAsync((value, resetTime, isWeekly, error) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (_window is null) return;
                    _window.SetLoading(false);

                    if (value.HasValue)
                    {
                        _window.SetValue(value.Value, isWeekly);
                        StartCountdown(resetTime);
                        _config.UsagePercentage = value.Value;
                        ConfigStore.Save(_config);
                    }
                    else
                    {
                        ApplyError(error);
                    }
                });
            }, _cts.Token, manual: true);
        }

        private async void OnSilentRefreshRequested()
        {
            if (_window is null) return;

            await ApiService.RefreshAsync((value, resetTime, isWeekly, error) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (_window is null) return;
                    if (value.HasValue)
                    {
                        _startupSucceeded = true;
                        _window.SetValue(value.Value, isWeekly);
                        StartCountdown(resetTime);
                        _config.UsagePercentage = value.Value;
                        ConfigStore.Save(_config);
                    }
                    else
                    {
                        ApplyError(error);

                        // Retry on startup if the network wasn't ready yet
                        if (!_startupSucceeded && _startupRetryCount < StartupRetryDelaysMs.Length)
                        {
                            int delay = StartupRetryDelaysMs[_startupRetryCount++];
                            BrowserService.Log($"Startup fetch failed ({error}), retrying in {delay / 1000}s");
                            Task.Delay(delay, _cts.Token)
                                .ContinueWith(_ => Application.Current?.Dispatcher.Invoke(OnSilentRefreshRequested),
                                              System.Threading.Tasks.TaskContinuationOptions.NotOnCanceled);
                        }
                    }
                });
            }, _cts.Token);
        }

        // ── Error display ────────────────────────────────────────────────────

        private bool _webView2PromptShown = false;

        private void ApplyError(FetchError error)
        {
            if (_window is null) return;
            StopCountdown();

            switch (error)
            {
                case FetchError.NotLoggedIn:
                    _window.SetError("Sign in required");
                    break;
                case FetchError.NetworkError:
                    _window.SetError("No connection");
                    break;
                case FetchError.ServiceDown:
                    _window.SetError("Claude unavailable");
                    break;
                case FetchError.ParseError:
                    _window.SetError("Couldn't read usage");
                    break;
                case FetchError.Blocked:
                    _window.SetError("Blocked by security software");
                    break;
                case FetchError.WebView2Missing:
                    _window.SetError("Setup required");
                    if (!_webView2PromptShown)
                    {
                        _webView2PromptShown = true;
                        ShowWebView2InstallPrompt();
                    }
                    break;
                default:
                    _window.SetError("Something went wrong");
                    break;
            }
        }

        private static void ShowWebView2InstallPrompt()
        {
            var result = System.Windows.MessageBox.Show(
                "This widget needs Microsoft WebView2 to load Claude.ai.\n\n" +
                "It's a free download from Microsoft — click OK to open the download page in your browser.\n\n" +
                "After installing, restart the widget.",
                "WebView2 Required",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Information);

            if (result == System.Windows.MessageBoxResult.OK)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    UseShellExecute = true
                });
            }
        }

        // ── Countdown ────────────────────────────────────────────────────────

        private void StartCountdown(string? resetTimeStr)
        {
            StopCountdown();

            _resetTarget = ParseResetTime(resetTimeStr);

            if (_resetTarget == null)
            {
                _window?.SetResetTime("--:--");
                return;
            }

            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += OnCountdownTick;
            _countdownTimer.Start();
            OnCountdownTick(null, null); // show immediately without waiting 1s
        }

        private void StopCountdown()
        {
            _countdownTimer?.Stop();
            _countdownTimer = null;
            _resetTarget    = null;
        }

        private void OnCountdownTick(object? sender, EventArgs? e)
        {
            if (_window is null || _resetTarget is null) return;

            var remaining = _resetTarget.Value - DateTime.Now;

            if (remaining <= TimeSpan.Zero)
            {
                StopCountdown();
                _window.SetResetTime("resetting...");
                OnSilentRefreshRequested();
                return;
            }

            string display;
            if (remaining.TotalHours >= 24)
                display = $"{(int)remaining.TotalDays}d {remaining.Hours}h";
            else if (remaining.TotalHours >= 1)
                display = $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
            else if (remaining.TotalMinutes >= 1)
                display = $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s";
            else
                display = $"{remaining.Seconds}s";

            _window.SetResetTime(display);
        }

        /// <summary>
        /// Parses reset time strings into a target DateTime.
        /// Handles: "Mon 2:59 PM" and relative "X hr Y min" formats.
        /// </summary>
        private static DateTime? ParseResetTime(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;

            // "May 1" — billing-cycle reset: month + day, no time (midnight)
            var monthDayMatch = Regex.Match(s.Trim(),
                @"^(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\w*\s+(\d{1,2})$",
                RegexOptions.IgnoreCase);
            if (monthDayMatch.Success)
            {
                var monthAbbr = monthDayMatch.Groups[1].Value.Substring(0, 3).ToLowerInvariant();
                var day       = int.Parse(monthDayMatch.Groups[2].Value);
                var monthNums = new System.Collections.Generic.Dictionary<string, int>
                {
                    ["jan"]=1,["feb"]=2,["mar"]=3,["apr"]=4,["may"]=5,["jun"]=6,
                    ["jul"]=7,["aug"]=8,["sep"]=9,["oct"]=10,["nov"]=11,["dec"]=12
                };
                if (monthNums.TryGetValue(monthAbbr, out var month))
                {
                    var now    = DateTime.Now;
                    var target = new DateTime(now.Year, month, day, 0, 0, 0);
                    if (target <= now) target = target.AddYears(1);
                    return target;
                }
            }

            // "Mon 2:59 PM" — absolute day + time
            var absMatch = Regex.Match(s,
                @"(Mon|Tue|Wed|Thu|Fri|Sat|Sun)\w*\s+(\d{1,2}:\d{2})\s*(AM|PM)?",
                RegexOptions.IgnoreCase);

            if (absMatch.Success)
            {
                var dayStr  = absMatch.Groups[1].Value;
                var timeStr = absMatch.Groups[2].Value;
                var ampm    = absMatch.Groups[3].Value;

                if (!DateTime.TryParse($"{timeStr} {ampm}".Trim(), out var parsedTime))
                    return null;

                var targetDow = ParseDayOfWeek(dayStr);
                if (targetDow == null) return null;

                var now = DateTime.Now;
                int daysUntil = ((int)targetDow.Value - (int)now.DayOfWeek + 7) % 7;

                // Same day but time already passed → next week
                if (daysUntil == 0 && parsedTime.TimeOfDay <= now.TimeOfDay)
                    daysUntil = 7;

                return now.Date.AddDays(daysUntil).Add(parsedTime.TimeOfDay);
            }

            // "X hr Y min" / "X days" — relative duration
            var dayMatch = Regex.Match(s, @"(\d+)\s*day",  RegexOptions.IgnoreCase);
            var hrMatch  = Regex.Match(s, @"(\d+)\s*hr",   RegexOptions.IgnoreCase);
            var minMatch = Regex.Match(s, @"(\d+)\s*min",  RegexOptions.IgnoreCase);

            if (dayMatch.Success || hrMatch.Success || minMatch.Success)
            {
                var span = TimeSpan.Zero;
                if (dayMatch.Success) span += TimeSpan.FromDays(int.Parse(dayMatch.Groups[1].Value));
                if (hrMatch.Success)  span += TimeSpan.FromHours(int.Parse(hrMatch.Groups[1].Value));
                if (minMatch.Success) span += TimeSpan.FromMinutes(int.Parse(minMatch.Groups[1].Value));

                // Snap to minute boundary — resets always occur on a whole minute.
                // This syncs the countdown to the real system clock rather than
                // counting from an arbitrary sub-second offset at click time.
                // Add 1 minute because Claude's duration is rounded down.
                var raw = DateTime.Now + span;
                return new DateTime(raw.Year, raw.Month, raw.Day, raw.Hour, raw.Minute, 0).AddMinutes(1);
            }

            return null;
        }

        private static DayOfWeek? ParseDayOfWeek(string s)
        {
            return s.Substring(0, 3).ToLowerInvariant() switch
            {
                "mon" => DayOfWeek.Monday,
                "tue" => DayOfWeek.Tuesday,
                "wed" => DayOfWeek.Wednesday,
                "thu" => DayOfWeek.Thursday,
                "fri" => DayOfWeek.Friday,
                "sat" => DayOfWeek.Saturday,
                "sun" => DayOfWeek.Sunday,
                _     => (DayOfWeek?)null
            };
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            _autoRefreshTimer?.Stop();
            StopCountdown();
            BrowserService.Dispose();
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
