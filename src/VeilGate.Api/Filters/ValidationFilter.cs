using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace VeilGate.Api.Filters;

/// <summary>
/// Runs the FluentValidation validator for every action argument that has one, before the action body runs.
/// A failure becomes 422 problem+json with code validation.failed and an "errors" map from field path to messages (Backend doc 3.1).
/// So controllers never validate by hand and cannot forget to.
/// </summary>
public sealed class ValidationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var errors = new Dictionary<string, List<string>>();

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null) continue;
            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator) continue;

            var result = await validator.ValidateAsync(new ValidationContext<object>(argument), context.HttpContext.RequestAborted);
            foreach (var failure in result.Errors)
            {
                var path = CamelCase(failure.PropertyName);
                if (!errors.TryGetValue(path, out var messages)) errors[path] = messages = [];
                messages.Add(failure.ErrorMessage);
            }
        }

        if (errors.Count == 0)
        {
            await next();
            return;
        }

        var problem = new ProblemDetails { Status = StatusCodes.Status422UnprocessableEntity, Title = "Validation failed" };
        problem.Extensions["code"] = "validation.failed";
        problem.Extensions["errors"] = errors;
        context.Result = new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>"Messages[1].Content" becomes "messages[1].content", the spelling the caller used in the JSON.</summary>
    internal static string CamelCase(string path) =>
        string.Join('.', path.Split('.').Select(s => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..]));
}
