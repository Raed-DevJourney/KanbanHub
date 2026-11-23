using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KanbanHub.Contracts;

namespace KanbanHub.Web.Api;

/// <summary>The headers a write to a card carries.</summary>
public readonly record struct CardPreconditions(string? ETag = null, Guid? IdempotencyKey = null);

/// <summary>
/// Sends requests and turns every answer into an <see cref="ApiResult{T}"/>: nothing above this class
/// sees an HTTP status code or an exception.
/// </summary>
public sealed class ApiTransport(HttpClient http)
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    public Task<ApiResult<T>> GetAsync<T>(string url, CancellationToken cancellationToken)
        where T : notnull =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ReadBodyAsync<T>, cancellationToken);

    /// <summary>Posts without a body and reads the response body.</summary>
    public Task<ApiResult<T>> PostAsync<T>(string url, CancellationToken cancellationToken)
        where T : notnull =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, url), ReadBodyAsync<T>, cancellationToken);

    /// <summary>Sends a body and reads the response body.</summary>
    public Task<ApiResult<T>> SendAsync<TBody, T>(
        HttpMethod method, string url, TBody body, CardPreconditions preconditions, CancellationToken cancellationToken)
        where T : notnull =>
        SendAsync(CreateRequest(method, url, body, preconditions), ReadBodyAsync<T>, cancellationToken);

    /// <summary>Sends a body and expects no response body.</summary>
    public Task<ApiResult<Done>> SendAsync<TBody>(
        HttpMethod method, string url, TBody body, CardPreconditions preconditions, CancellationToken cancellationToken) =>
        SendAsync(CreateRequest(method, url, body, preconditions), NoBodyAsync, cancellationToken);

    /// <summary>Sends a request without a body and expects none back.</summary>
    public Task<ApiResult<Done>> SendAsync(HttpMethod method, string url, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(method, url), NoBodyAsync, cancellationToken);

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpRequestMessage request, Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
        where T : notnull
    {
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? await read(response, cancellationToken)
                : await ReadProblemAsync(response, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiProblem.Unreachable;
        }
        finally
        {
            request.Dispose();
        }
    }

    private static HttpRequestMessage CreateRequest<TBody>(HttpMethod method, string url, TBody body, CardPreconditions preconditions)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, TypeInfo<TBody>()) };
        if (preconditions.ETag is { } etag)
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{etag}\""));
        if (preconditions.IdempotencyKey is { } key)
            request.Headers.Add(IdempotencyKeyHeader, key.ToString());
        return request;
    }

    private static async Task<T> ReadBodyAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync(TypeInfo<T>(), cancellationToken)
        ?? throw new JsonException($"The server returned an empty body where a {typeof(T).Name} was expected.");

    private static Task<Done> NoBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        Task.FromResult(Done.Instance);

    private static async Task<ApiProblem> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.Ordinal) != true)
            return ApiProblem.FromStatus((int)response.StatusCode);

        ApiProblem? problem = await response.Content.ReadFromJsonAsync(WebJsonContext.Default.ApiProblem, cancellationToken);
        return problem ?? ApiProblem.FromStatus((int)response.StatusCode);
    }

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        (JsonTypeInfo<T>?)KanbanHubJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new InvalidOperationException($"{typeof(T).Name} is missing from {nameof(KanbanHubJsonContext)}.");
}
