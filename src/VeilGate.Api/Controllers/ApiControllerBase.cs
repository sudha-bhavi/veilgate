using Microsoft.AspNetCore.Mvc;
using VeilGate.Domain.Common;

namespace VeilGate.Api.Controllers;

public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>Turns a domain Error into an RFC 9457 problem+json response with a stable "code" (Backend doc error catalog).</summary>
    protected ObjectResult Fail(Error error)
    {
        var status = error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Locked => StatusCodes.Status423Locked,
            ErrorKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
            ErrorKind.BadGateway => StatusCodes.Status502BadGateway,
            ErrorKind.GatewayTimeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status500InternalServerError
        };
        var problem = new ProblemDetails { Status = status, Title = error.Message };
        problem.Extensions["code"] = error.Code;
        if (error.Extensions is not null)
            foreach (var (key, value) in error.Extensions) problem.Extensions[key] = value;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
