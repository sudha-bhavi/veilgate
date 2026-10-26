using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace VeilGate.Infrastructure.Persistence;

/// <summary>Translations between the plain types our code uses and the PostgreSQL column types inet and jsonb, shared by every table that has them.</summary>
internal static class PostgresConverters
{
    /// <summary>An IP address is text in our code and inet in the database.</summary>
    public static readonly ValueConverter<string, IPAddress> Ip = new(v => IPAddress.Parse(v), v => v.ToString());

    /// <summary>A small string-to-string dictionary is a dictionary in our code and jsonb in the database.</summary>
    public static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> Details = new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new Dictionary<string, string>());

    /// <summary>EF needs to know how to tell whether a dictionary changed, and how to copy it.</summary>
    public static readonly ValueComparer<IReadOnlyDictionary<string, string>> DetailsComparer = new(
        (x, y) => x!.Count == y!.Count && !x.Except(y).Any(),
        v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
        v => new Dictionary<string, string>(v));
}
