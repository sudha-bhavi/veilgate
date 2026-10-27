namespace VeilGate.Domain.Common;

/// <summary>A failure with a stable machine code (see Backend Design, error catalog) and an HTTP-friendly kind.</summary>
public sealed record Error(string Code, string Message, ErrorKind Kind = ErrorKind.Failure, IReadOnlyDictionary<string, object?>? Extensions = null)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    /// <summary>Adds a machine-readable field to the error, e.g. the entity types that caused policy.blocked. The API copies these into the problem+json body.</summary>
    public Error With(string key, object? value)
    {
        var copy = Extensions is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(Extensions);
        copy[key] = value;
        return this with { Extensions = copy };
    }

    public static Error NotFound(string code = "resource.not_found", string message = "Not found") => new(code, message, ErrorKind.NotFound);
    public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);
    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);
    public static Error Forbidden(string code = "auth.forbidden", string message = "Forbidden") => new(code, message, ErrorKind.Forbidden);
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorKind.Unauthorized);
    public static Error Locked(string code, string message) => new(code, message, ErrorKind.Locked);
    /// <summary>The request is well-formed but policy refuses it (policy.blocked, leak.gate_tripped): HTTP 422.</summary>
    public static Error Unprocessable(string code, string message) => new(code, message, ErrorKind.Unprocessable);
    public static Error ProviderUnavailable() => new("provider.unavailable", "The language model provider is unavailable.", ErrorKind.BadGateway);
    public static Error ProviderTimeout() => new("provider.timeout", "The language model provider timed out.", ErrorKind.GatewayTimeout);
    public static Error PreconditionFailed() => new("precondition.failed", "This changed since you loaded it. Reload and try again.", ErrorKind.PreconditionFailed);
}

public enum ErrorKind { Failure, Validation, NotFound, Conflict, Forbidden, Unauthorized, Locked, Unprocessable, BadGateway, GatewayTimeout, PreconditionFailed }

public class Result
{
    protected Result(bool ok, Error error)
    {
        if (ok && error != Error.None) throw new InvalidOperationException("A successful result cannot carry an error.");
        if (!ok && error == Error.None) throw new InvalidOperationException("A failed result needs an error.");
        IsSuccess = ok;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);
    public static Result<T> Success<T>(T value) => new(value, true, Error.None);
    public static Result<T> Failure<T>(Error error) => new(default, false, error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T? value, bool ok, Error error) : base(ok, error) => _value = value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read the value of a failed result.");

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Error error) => Failure<T>(error);
}
