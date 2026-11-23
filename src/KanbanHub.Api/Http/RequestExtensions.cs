using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using KanbanHub.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KanbanHub.Api.Http;

/// <summary>A card's row version as an HTTP entity tag: base64 in double quotes.</summary>
internal static class ETags
{
    private const int RowVersionLength = 8;

    public static string Format(byte[] rowVersion) => $"\"{Convert.ToBase64String(rowVersion)}\"";

    public static bool TryParse(string header, out byte[] rowVersion)
    {
        rowVersion = new byte[RowVersionLength];
        return Convert.TryFromBase64String(header.Trim('"'), rowVersion, out int written) && written == RowVersionLength;
    }
}

internal static class RequestExtensions
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Reads the JSON body, or returns null when it is missing or not of the expected shape.</summary>
    public static async Task<T?> ReadBodyAsync<T>(this HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(request.Body, TypeInfo<T>(), cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the body to <paramref name="current"/> as a JSON merge patch (RFC 7396):
    /// members present replace the current value, <c>null</c> clears it, members absent stay as they are.
    /// </summary>
    public static T? MergeInto<T>(this JsonObject patch, T current)
        where T : class
    {
        JsonObject merged = JsonSerializer.SerializeToNode(current, TypeInfo<T>())!.AsObject();
        foreach ((string name, JsonNode? value) in patch) merged[name] = value?.DeepClone();

        try
        {
            return merged.Deserialize(TypeInfo<T>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An integer query parameter, or <paramref name="fallback"/> when it is absent or not a number.</summary>
    public static int IntOr(this IQueryCollection query, string name, int fallback) =>
        int.TryParse(query[name], out int value) ? value : fallback;

    public static bool TryGetIdempotencyKey(this HttpRequest request, out Guid key) =>
        Guid.TryParse(request.Headers[IdempotencyKeyHeader].ToString(), out key);

    public static bool TryGetIfMatch(this HttpRequest request, out byte[] rowVersion) =>
        ETags.TryParse(request.Headers.IfMatch.ToString(), out rowVersion);

    public static IActionResult OkWithETag(this HttpResponse response, byte[] rowVersion, object? body = null)
    {
        response.Headers.ETag = ETags.Format(rowVersion);
        return body is null ? new NoContentResult() : new OkObjectResult(body);
    }

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        (JsonTypeInfo<T>?)KanbanHubJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new InvalidOperationException($"{typeof(T).Name} is missing from {nameof(KanbanHubJsonContext)}.");
}
