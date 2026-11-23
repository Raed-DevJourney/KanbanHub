using System.Net;
using System.Text;
using System.Text.Json;
using KanbanHub.Contracts;
using KanbanHub.Web.Api;

namespace KanbanHub.Web.Tests.Infrastructure;

/// <summary>What the web app sent, kept after the request itself is gone.</summary>
internal sealed record SentRequest(HttpMethod Method, string Path, string Body, string? IfMatch, string? IdempotencyKey);

/// <summary>Stands in for the API: answers the routes a test sets up and remembers every request.</summary>
internal sealed class StubServer : HttpMessageHandler
{
    private static readonly Uri BaseAddress = new("https://kanbanhub.test/api/v1/");

    private readonly Dictionary<(HttpMethod, string), Func<HttpResponseMessage>> routes = [];

    public List<SentRequest> Requests { get; } = [];

    public KanbanApi CreateApi() => new(new ApiTransport(new HttpClient(this) { BaseAddress = BaseAddress }));

    public void OnGet<T>(string path, T body) => routes[(HttpMethod.Get, path)] = () => Json(HttpStatusCode.OK, Serialize(body));

    public void On(HttpMethod method, string path, HttpStatusCode status, string json = "") =>
        routes[(method, path)] = () => Json(status, json);

    public void OnProblem(HttpMethod method, string path, int status, string kind, string detail, string extensions = "") =>
        routes[(method, path)] = () => Json((HttpStatusCode)status, $$"""
            { "type": "https://kanbanhub.example/problems/{{kind}}", "title": "{{kind}}", "status": {{status}}, "detail": "{{detail}}"{{extensions}} }
            """);

    public SentRequest Last(HttpMethod method) => Requests.Last(request => request.Method == method);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = BaseAddress.MakeRelativeUri(request.RequestUri!).ToString();
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new SentRequest(
            request.Method, path, body, request.Headers.IfMatch.FirstOrDefault()?.Tag,
            request.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? keys) ? keys.First() : null));

        return routes.TryGetValue((request.Method, path), out Func<HttpResponseMessage>? respond)
            ? respond()
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string Serialize<T>(T body) => JsonSerializer.Serialize(body, KanbanHubJsonContext.Default.GetTypeInfo(typeof(T))!);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, status >= HttpStatusCode.BadRequest ? "application/problem+json" : "application/json") };
}
