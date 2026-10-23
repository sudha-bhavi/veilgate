using VeilGate.Domain.Common;

namespace VeilGate.Domain;

public sealed class Tenant : Entity
{
    private Tenant() { }

    public string Name { get; private set; } = string.Empty;
    public string Status { get; private set; } = "Active";

    public static Result<Tenant> Create(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is < 3 or > 120)
            return Error.Validation("validation.failed", "Tenant name must be 3 to 120 characters.");
        return new Tenant { Name = name };
    }

    public Result Suspend()
    {
        if (Status == "Suspended") return Result.Failure(Error.Conflict("resource.conflict", "Tenant is already suspended."));
        Status = "Suspended";
        return Result.Success();
    }
}
