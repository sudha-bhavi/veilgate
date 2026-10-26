using VeilGate.Domain.Detection;

namespace VeilGate.Application.Detection;

public interface IDetector
{
    string Name { get; }
    IEnumerable<DetectedEntity> Detect(string text, DetectionContext context);
}

public sealed record DetectionDictionary(string Name, string EntityType, AhoCorasick Matcher);

public sealed record DetectionContext
{
    public double MinConfidence { get; init; } = 0;
    public IReadOnlyList<DetectionDictionary> Dictionaries { get; init; } = [];
    public static readonly DetectionContext Default = new();
}