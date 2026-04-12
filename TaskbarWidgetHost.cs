using System;
using System.Text.RegularExpressions;
using System.Threading;
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

        // ── Startup ───────────────────────────────────────────────────────────

        public void Initialize()
        {
            _config = ConfigStore.Load();
            AutoStartHelper.PromptIfNeeded(_config);

            ApiService.SetUsagePercentage(_config.UsagePercentage ?? 0.0);

            _window = new WidgetWindow(_config);
            _window.RefreshRequested       += OnRefreshRequested;
            _window.SilentRefreshRequested += OnSilentRefreshRequested;
            _window.Show();

            _window.SetValue(_config.UsagePercentage ?? 0.0);
            OnSilentRefreshRequested();
        }

        // ── Refresh ───────────────────────────────────────────────────────────

        private async void OnRefreshRequested()
        {
            if (_window is null) return;
            _window.SetLoading(true);

            await ApiService.RefreshAsync((value, resetTime, error) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (_window is null) return;
                    _window.SetLoading(false);

                    if (value.HasValue)
                    {
                        _window.SetValue(value.Value);
                        StartCountdown(resetTime);
                        _config.UsagePercentage = value.Value;
                        ConfigStore.Save(_config);
                    }
                    else
                    {
                        ApplyError(error);
                    }
                });
            }, _cts.Token);
        }

        private async void OnSilentRefreshRequested()
        {
            if (_window is null) return;

            await ApiService.RefreshAsync((value, resetTime, error) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (_window is null) return;
                    if (value.HasValue)
                    {
                        _window.SetValue(value.Value);
                        StartCountdown(resetTime);
                        _config.UsagePercentage = value.Value;
                        ConfigStore.Save(_config);
                    }
                    else
                    {
                        ApplyError(error);
                    }
                });
            }, _cts.Token);
        }

        // ── Error display ────────────────────────────────────────────────────

        private void ApplyError(FetchError error)
        {
            if (_window is null) return;
            StopCountdown();

            switch (error)
            {
                case FetchError.NotLoggedIn:
                    _window.SetError("sign in");
                    break;
                case FetchError.NetworkError:
                    _window.SetError("offline");
                    break;
                case FetchError.ServiceDown:
                    _window.SetError("unavailable");
                    break;
                default:
                    _window.SetError("--:--");
                    break;
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
                return DateTime.Now + span;
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
            StopCountdown();
            BrowserService.Dispose();
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
