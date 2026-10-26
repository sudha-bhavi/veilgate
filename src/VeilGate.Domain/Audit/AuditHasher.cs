using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeilGate.Domain.Audit;

public static class AuditHasher
{
    /// <summary>hash = SHA-256(prevHash + canonicalJson(event)), as lowercase hex (Backend doc 6.4).</summary>
    public static string Compute(AuditEvent e)
    {
        var prev = Encoding.UTF8.GetBytes(e.PrevHash);
        var json = CanonicalJson(e);
        var data = new byte[prev.Length + json.Length];
        prev.CopyTo(data, 0);
        json.CopyTo(data, prev.Length);
        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    /// <summary>
    /// "Canonical" means the same event always produces exactly the same bytes: fixed property order, fixed time format,
    /// detail keys sorted. Without that, two correct computers could disagree about the hash.
    /// The hash fields themselves (PrevHash is mixed in separately, Hash is the output) are not part of the JSON.
    /// </summary>
    private static byte[] CanonicalJson(AuditEvent e)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("id", e.Id);
            w.WriteString("tenantId", e.TenantId);
            w.WriteNumber("seq", e.Seq);
            w.WriteString("occurredAt", e.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
            w.WriteString("actorType", e.ActorType);
            WriteOptional(w, "actorId", e.ActorId?.ToString());
            w.WriteString("action", e.Action);
            WriteOptional(w, "resourceType", e.ResourceType);
            WriteOptional(w, "resourceId", e.ResourceId?.ToString());
            w.WriteString("result", e.Result);
            WriteOptional(w, "ip", e.Ip);
            w.WriteStartObject("details");
            foreach (var (key, value) in e.Details.OrderBy(d => d.Key, StringComparer.Ordinal))
                w.WriteString(key, value);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteOptional(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }
}