using VeilGate.Domain.Detection;

namespace VeilGate.Application.Detection;

public interface IDetector
{
    string Name { get; }
    IEnumerable<DetectedEntity> Detect(string text, DetectionContext context);
}

public sealed record DetectionContext
{
    public double MinConfidence { get; init; } = 0;
    public static readonly DetectionContext Default = new();
}