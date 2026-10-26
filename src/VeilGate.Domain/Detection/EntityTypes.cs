namespace VeilGate.Domain.Detection;

/// <summary>The entity types a policy can talk about (Backend doc 4.2). Custom tenant rules use "CUSTOM:&lt;ruleName&gt;".</summary>
public static class EntityTypes
{
    public const string Person = "PERSON";
    public const string Ssn = "SSN";
    public const string Mrn = "MRN";
    public const string HealthPlanId = "HEALTH_PLAN_ID";
    public const string Account = "ACCOUNT";
    public const string License = "LICENSE";
    public const string VehicleId = "VEHICLE_ID";
    public const string DeviceId = "DEVICE_ID";
    public const string Phone = "PHONE";
    public const string Fax = "FAX";
    public const string Email = "EMAIL";
    public const string Url = "URL";
    public const string IpAddress = "IP_ADDRESS";
    public const string Address = "ADDRESS";
    public const string Zip = "ZIP";
    public const string Date = "DATE";
    public const string AgeOver89 = "AGE_OVER_89";
    // Extra types from the Database doc seed list (7.1). They are not HIPAA categories of their own but are still sensitive.
    public const string Dob = "DOB";
    public const string CreditCard = "CREDIT_CARD";
    public const string Npi = "NPI";
    public const string Dea = "DEA";

    public const string CustomPrefix = "CUSTOM:";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        Person, Ssn, Mrn, HealthPlanId, Account, License, VehicleId, DeviceId, Phone, Fax, Email, Url,
        IpAddress, Address, Zip, Date, AgeOver89, Dob, CreditCard, Npi, Dea,
    };

    public static bool IsValid(string type) =>
        Known.Contains(type) || (type.StartsWith(CustomPrefix, StringComparison.Ordinal) && type.Length > CustomPrefix.Length);

    /// <summary>The label used inside a token or a mask, e.g. PERSON or CUSTOM_BADGE (a colon would confuse the token format).</summary>
    public static string Label(string type) => type.Replace(':', '_');
}
