using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain;

/// <summary>The thing the caller named doesn't exist (or isn't theirs to see).</summary>
public sealed record class NotFound(string What);

/// <summary>The request is well-formed but breaks one or more rules.</summary>
public sealed record class Invalid(IReadOnlyList<string> Problems)
{
    public Invalid(string problem) : this([problem]) { }
}

/// <summary>The caller lacks a tenant-wide Entra app role.</summary>
public sealed record class TenantRoleRequired(string Role);

/// <summary>A change was stored and there is nothing more to say about it.</summary>
public sealed record class Saved
{
    public static Saved Instance { get; } = new();
}

/// <summary>
/// How a use case ends: its result, or one of the reasons it didn't happen.
/// Being a union, the API's mapping to HTTP must handle every case.
/// </summary>
public union Outcome<T>(T, Forbidden, TenantRoleRequired, NotFound, Invalid, ConcurrencyConflict)
    where T : notnull;
