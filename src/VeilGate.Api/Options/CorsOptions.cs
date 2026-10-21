using System.ComponentModel.DataAnnotations;

namespace VeilGate.Api.Options;

public sealed class AppCorsOptions
{
    public const string Section = "Cors";
    /// <summary>Exact origins only. A wildcard is rejected at startup.</summary>
    [Required, MinLength(1)] public string[] AllowedOrigins { get; set; } = [];
}
