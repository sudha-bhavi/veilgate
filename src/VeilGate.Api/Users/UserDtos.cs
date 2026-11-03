using FluentValidation;
using VeilGate.Application.Users;

namespace VeilGate.Api.Users;

// The JSON bodies of the user endpoints (Backend doc 5.1). They hold only what the caller may choose: never a tenant id, a status
// or a token version. The tenant comes from the verified token, and an unknown property is rejected outright.

public sealed record InviteUserRequest(string Email, string Role);

public sealed record ChangeUserRoleRequest(string Role);

/// <summary>Sent from the accept-invitation page. The token is the secret from the emailed link.</summary>
public sealed record AcceptInvitationRequest(string Token, string FullName, string Password);

public sealed class InviteUserRequestValidator : AbstractValidator<InviteUserRequest>
{
    public InviteUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Email is required").MaximumLength(254).WithMessage("Maximum 254 characters");
        RuleFor(x => x.Role).Must(role => UserRoles.TryParseAssignable(role, out _)).WithMessage("Choose a role from the list");
    }
}

public sealed class ChangeUserRoleRequestValidator : AbstractValidator<ChangeUserRoleRequest>
{
    public ChangeUserRoleRequestValidator() =>
        RuleFor(x => x.Role).Must(role => UserRoles.TryParseAssignable(role, out _)).WithMessage("Choose a role from the list");
}

public sealed class AcceptInvitationRequestValidator : AbstractValidator<AcceptInvitationRequest>
{
    public AcceptInvitationRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().WithMessage("The link is incomplete").MaximumLength(128).WithMessage("The link is not valid");
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Name is required").MaximumLength(120).WithMessage("Maximum 120 characters");
        // The strength rules are in the handler. This upper bound only stops giant inputs from burning hashing time.
        RuleFor(x => x.Password).NotEmpty().WithMessage("Password is required").MaximumLength(256).WithMessage("Maximum 256 characters");
    }
}
