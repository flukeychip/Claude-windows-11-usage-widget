using System;
using System.Threading;
using System.Windows;

namespace TaskbarWidget
{
    internal sealed class TaskbarWidgetHost : IDisposable
    {
        private WidgetWindow? _window;
        private WidgetConfig  _config = new WidgetConfig();
        private CancellationTokenSource _cts = new CancellationTokenSource();

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

        private async void OnRefreshRequested()
        {
            if (_window is null) return;

            _window.SetLoading(true);

            await ApiService.RefreshAsync((value, resetTime) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (_window is null) return;

                    _window.SetLoading(false);

                    if (value.HasValue)
                    {
                        _window.SetValue(value.Value);
                        _window.SetResetTime(resetTime);
                        _config.UsagePercentage = value.Value;
                        ConfigStore.Save(_config);
                    }
                    else
                    {
                        _window.SetError();
                    }
                });
            }, _cts.Token);
        }

        private async void OnSilentRefreshRequested()
        {
            if (_window is null) return;

            await ApiService.RefreshAsync((value, resetTime) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (_window is null) return;
                    if (value.HasValue)
                    {
                        _window.SetValue(value.Value);
                        _window.SetResetTime(resetTime);
                        _config.UsagePercentage = value.Value;
                        ConfigStore.Save(_config);
                    }
                });
            }, _cts.Token);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
