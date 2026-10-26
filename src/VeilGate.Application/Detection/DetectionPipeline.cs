using VeilGate.Domain.Detection;

namespace VeilGate.Application.Detection;

public interface IDetectionPipeline
{
    Task<IReadOnlyList<DetectedEntity>> DetectAsync(string text, DetectionContext context, CancellationToken ct = default);
}

public sealed class DetectionPipeline(IEnumerable<IDetector> detectors) : IDetectionPipeline
{
    private readonly IDetector[] _detectors = detectors.ToArray();

    public Task<IReadOnlyList<DetectedEntity>> DetectAsync(string text, DetectionContext context, CancellationToken ct = default)
    {
        var all = new List<DetectedEntity>();
        foreach (var detector in _detectors)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var e in detector.Detect(text, context))
                if (e.Confidence >= context.MinConfidence && e.Start >= 0 && e.End <= text.Length)
                    all.Add(e);
        }
        return Task.FromResult(SpanMerger.Merge(all));
    }
}