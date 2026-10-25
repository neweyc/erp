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
    /// <summary>The employee's email changed after the caller read it, so an invitation would go elsewhere.</summary>
    public const string EmployeeChanged = "employee_changed";
    public const string NotFound = "not_found";
    /// <summary>The change would leave no active user who can manage users.</summary>
    public const string LastAdmin = "last_admin";
    /// <summary>Another of the tenant's roles already has that name (ignoring case).</summary>
    public const string RoleNameTaken = "role_name_taken";
    /// <summary>A department (among its siblings), job title or location already has that name, ignoring case.</summary>
    public const string CatalogNameTaken = "catalog_name_taken";
    /// <summary>A role cannot be deleted while anyone holds it.</summary>
    public const string RoleInUse = "role_in_use";
    /// <summary>An administrator cannot deactivate their own account; another one must.</summary>
    public const string CannotDeactivateSelf = "cannot_deactivate_self";
    /// <summary>Only an account that has not yet accepted its invitation can be sent one again.</summary>
    public const string NotInvited = "not_invited";
    /// <summary>Terminating your own employee record would end your own access; another administrator must.</summary>
    public const string CannotTerminateSelf = "cannot_terminate_self";
    /// <summary>The account is already linked to an employee record.</summary>
    public const string UserHasEmployee = "user_has_employee";
    /// <summary>Another tenant has, or once had, that address. An address is never given to a second tenant.</summary>
    public const string SubdomainTaken = "subdomain_taken";
    /// <summary>Re-exported from the permission check, so a handler refusing something itself uses the same code.</summary>
    public const string NotPermitted = AuthProblem.NotPermitted;

    /// <summary>Re-exported so a feature never hand-writes an auth code and drifts from it.</summary>
    public const string AppNotLicensed = AuthProblem.AppNotLicensed;
}
