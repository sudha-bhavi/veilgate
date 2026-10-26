using VeilGate.Application.Detection;
using VeilGate.Domain.Detection;

namespace VeilGate.Infrastructure.Detection;

public sealed class DictionaryDetector : IDetector
{
    public string Name => "dict";

    public IEnumerable<DetectedEntity> Detect(string text, DetectionContext context)
    {
        foreach (var dictionary in context.Dictionaries)
            foreach (var (start, end) in dictionary.Matcher.Find(text))
                yield return new DetectedEntity(dictionary.EntityType, start, end, 0.9, Name);
    }
}