using System.ComponentModel.DataAnnotations;

namespace VeilGate.Api.Options;

/// <summary>
/// The "Try the demo" button. It is off unless a deployment switches it on, because it signs a visitor in without a password
/// or a second factor. Turn it on only for a sandbox organisation that holds synthetic data and nothing else.
/// </summary>
public sealed class DemoOptions
{
    public const string Section = "Demo";

    /// <summary>Environment variable form: Demo__Enabled=true</summary>
    public bool Enabled { get; set; }

    /// <summary>The one seeded user a visitor is signed in as. It must not be an administrator.</summary>
    [Required] public string UserEmail { get; set; } = "demo@acme.test";
}
