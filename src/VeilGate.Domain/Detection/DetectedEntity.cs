namespace VeilGate.Domain.Detection;

/// <summary>
/// One sensitive value found in a text. Only positions are kept, never the value itself, so this is safe to log or store.
/// <paramref name="Priority"/>: bigger wins when two detections overlap.
/// </summary>
public sealed record DetectedEntity(string Type, int Start, int End, double Confidence, string Detector, int Priority = 100)
{
    public int Length => End - Start;
    public bool Overlaps(DetectedEntity other) => Start < other.End && other.Start < End;
}
