using System.ComponentModel.DataAnnotations;

namespace VeilGate.Api.Options;

public sealed class GatewayOptions
{
    public const string Section = "Gateway";
    [Range(1024, 10_485_760)] public int MaxBodyBytes { get; set; } = 1_048_576;
    [Range(1024, 20_971_520)] public int MaxFhirBytes { get; set; } = 5_242_880;
    [Range(1, 1440)] public int VaultTtlMinutesDefault { get; set; } = 60;
    [Range(1, 1440)] public int VaultTtlMinutesMax { get; set; } = 1440;
}
