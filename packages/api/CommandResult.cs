using AppPlatform.Auth;
using Microsoft.AspNetCore.Http;

namespace AppPlatform.Api;

public enum CommandOutcome
{
    Succeeded,
    /// <summary>Input the caller can correct. 400.</summary>
    Invalid,
    /// <summary>Exists but this caller may not have it. 403 — never 404.</summary>
    Forbidden,
    NotFound,
    /// <summary>Lost a race, or violates a uniqueness rule. 409.</summary>
    Conflict,
}

/// <summary>
/// What a handler returns. Deliberately not an <c>IResult</c>: a handler that builds HTTP
/// responses cannot be unit-tested without a host, and the mapping below is the same in every
/// feature, so writing it per endpoint only creates opportunities to write it differently.
/// </summary>
public sealed record CommandResult
{
    private CommandResult() { }

    public CommandOutcome Outcome { get; private init; }
    public object? Value { get; private init; }
    public string? ProblemCode { get; private init; }
    public string? Message { get; private init; }

    public bool Succeeded => Outcome == CommandOutcome.Succeeded;

    public static CommandResult Ok(object? value = null)
        => new() { Outcome = CommandOutcome.Succeeded, Value = value };

    /// <param name="problemCode">
    /// Stable and machine-readable. The UI branches on this; a UI matching on
    /// <paramref name="message"/> breaks the first time someone improves the wording.
    /// </param>
    public static CommandResult Invalid(string problemCode, string message)
        => new() { Outcome = CommandOutcome.Invalid, ProblemCode = problemCode, Message = message };

    public static CommandResult NotFound(string problemCode = "not_found", string? message = null)
        => new() { Outcome = CommandOutcome.NotFound, ProblemCode = problemCode, Message = message };

    public static CommandResult Forbidden(string problemCode = "forbidden", string? message = null)
        => new() { Outcome = CommandOutcome.Forbidden, ProblemCode = problemCode, Message = message };

    public static CommandResult Conflict(string problemCode, string message)
        => new() { Outcome = CommandOutcome.Conflict, ProblemCode = problemCode, Message = message };

    public IResult CreateIResult() => Outcome switch
    {
        CommandOutcome.Succeeded when Value is null => Results.NoContent(),
        CommandOutcome.Succeeded => Results.Ok(Value),
        CommandOutcome.Invalid => Problem(StatusCodes.Status400BadRequest),
        CommandOutcome.Forbidden => Problem(StatusCodes.Status403Forbidden),
        CommandOutcome.NotFound => Problem(StatusCodes.Status404NotFound),
        CommandOutcome.Conflict => Problem(StatusCodes.Status409Conflict),
        _ => throw new InvalidOperationException($"Unmapped outcome {Outcome}."),
    };

    private IResult Problem(int status)
        => Results.Json(new { problemCode = ProblemCode, message = Message }, statusCode: status);
}

public static class CoreProblems
{
    public const string ValidationFailed = "validation_failed";
    public const string EmailInUse = "email_in_use";
    public const string EmployeeHasAccount = "employee_has_account";
    public const string EmployeeTerminated = "employee_terminated";
    public const string NotFound = "not_found";

    /// <summary>Re-exported so a feature never hand-writes an auth code and drifts from it.</summary>
    public const string AppNotLicensed = AuthProblem.AppNotLicensed;
}
