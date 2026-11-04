namespace VeilGate.Infrastructure.Messaging;

public static class RetryDelay
{
    /// <summary>How long to wait after the given number of failures in a row: 1, 2, 4, 8 seconds and so on, never more than the cap.</summary>
    public static TimeSpan ForFailure(int consecutiveFailures, TimeSpan cap)
    {
        if (consecutiveFailures < 1) return TimeSpan.Zero;
        var seconds = Math.Pow(2, Math.Min(consecutiveFailures - 1, 30));   // the exponent is limited so it cannot overflow
        return TimeSpan.FromSeconds(Math.Min(seconds, cap.TotalSeconds));
    }
}
