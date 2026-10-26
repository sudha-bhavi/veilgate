namespace VeilGate.Domain.Detection;

public static class SpanMerger
{
    /// <summary>
    /// Several detectors often find the same value (a phone number is also "digits"). Keep one detection per area of text:
    /// the higher priority wins, then the longer span, then the higher confidence, then the earlier start.
    /// The result is sorted by position.
    /// </summary>
    public static IReadOnlyList<DetectedEntity> Merge(IEnumerable<DetectedEntity> found)
    {
        var ranked = found
            .Where(e => e.Length > 0)
            .OrderByDescending(e => e.Priority)
            .ThenByDescending(e => e.Length)
            .ThenByDescending(e => e.Confidence)
            .ThenBy(e => e.Start);

        var kept = new List<DetectedEntity>();
        foreach (var candidate in ranked)
            if (!kept.Any(k => k.Overlaps(candidate)))
                kept.Add(candidate);

        return kept.OrderBy(e => e.Start).ThenBy(e => e.End).ToList();
    }
}
