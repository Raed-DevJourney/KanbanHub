using System.Text.Json.Serialization;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Web.Api;

/// <summary>
/// An RFC 9457 problem as the API returns it, with the extension members KanbanHub adds
/// (specification section 5.5).
/// </summary>
public sealed record ApiProblem
{
    private const string TypeBase = "https://kanbanhub.example/problems/";

    public string? Type { get; init; }

    public string? Title { get; init; }

    public int Status { get; init; }

    public string? Detail { get; init; }

    public IReadOnlyList<WipViolation> Violations { get; init; } = [];

    /// <summary>Cards in the full step that most need help.</summary>
    public IReadOnlyList<Guid> SwarmCandidates { get; init; } = [];

    public IReadOnlyList<string> UnmetItems { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>The short problem type, for example <c>wip-limit-reached</c>.</summary>
    [JsonIgnore]
    public string Kind => Type?.Replace(TypeBase, "", StringComparison.Ordinal) ?? "";

    public static ApiProblem Unreachable { get; } = new()
    {
        Type = TypeBase + "unreachable",
        Title = "Can't reach KanbanHub",
        Detail = "The server didn't answer. Check your connection and try again."
    };

    public static ApiProblem FromStatus(int status) => new()
    {
        Type = TypeBase + "http-" + status,
        Title = "Request failed",
        Status = status,
        Detail = status == 401 ? "Your sign-in has expired. Sign in again." : $"The server answered {status}."
    };
}

/// <summary>What a call to the API produced: the value, or the problem that explains why not.</summary>
public union ApiResult<T>(T, ApiProblem)
    where T : notnull;

/// <summary>A call that succeeded and returned no body.</summary>
public sealed record Done
{
    public static Done Instance { get; } = new();
}

/// <summary>JSON metadata for the types only the web app knows.</summary>
[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApiProblem))]
internal sealed partial class WebJsonContext : JsonSerializerContext;
