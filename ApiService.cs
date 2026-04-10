using System;
using System.Threading;
using System.Threading.Tasks;

namespace TaskbarWidget;

internal static class ApiService
{
    private static double _currentValue = 0.0;
    private static bool  _isFetching    = false;

    public static bool IsFetching => _isFetching;

    /// <summary>
    /// Returns the last fetched usage value (0.0–1.0).
    /// The bar displays this; it does not change until the user clicks to refresh.
    /// </summary>
    public static Task<double?> FetchMetricAsync(CancellationToken ct = default)
        => Task.FromResult<double?>(_currentValue);

    /// <summary>
    /// Called when the user clicks the widget.
    /// Opens Chrome invisibly, reads Claude.ai usage, updates the stored value.
    /// Calls onUpdate each time a new value arrives (or null on error).
    /// </summary>
    public static async Task RefreshAsync(Action<double?, string?> onUpdate, CancellationToken ct = default)
    {
        if (_isFetching) return;
        _isFetching = true;

        try
        {
            var (usage, resetTime) = await BrowserService.FetchUsageAsync(ct);
            if (usage.HasValue)
                _currentValue = usage.Value;

            onUpdate(usage, resetTime);
        }
        finally
        {
            _isFetching = false;
        }
    }

    public static void SetUsagePercentage(double percentage)
        => _currentValue = Math.Clamp(percentage, 0.0, 1.0);
}
