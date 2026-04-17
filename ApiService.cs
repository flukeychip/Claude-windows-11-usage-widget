using System;
using System.Threading;
using System.Threading.Tasks;

namespace TaskbarWidget
{
    internal static class ApiService
    {
        private static double _currentValue = 0.0;
        private static bool   _isFetching   = false;

        public static bool IsFetching => _isFetching;

        public static Task<double?> FetchMetricAsync(CancellationToken ct = default)
            => Task.FromResult<double?>(_currentValue);

        public static async Task RefreshAsync(Action<double?, string?, bool, FetchError> onUpdate,
                                              CancellationToken ct = default,
                                              bool manual = false)
        {
            if (_isFetching) return;

            _isFetching = true;
            try
            {
                var (usage, resetTime, isWeekly, error) = await BrowserService.FetchUsageAsync(ct);
                if (usage.HasValue)
                    _currentValue = usage.Value;

                onUpdate(usage, resetTime, isWeekly, error);
            }
            finally
            {
                _isFetching = false;
            }
        }

        public static void SetUsagePercentage(double percentage)
            => _currentValue = Math.Min(Math.Max(percentage, 0.0), 1.0);
    }
}
