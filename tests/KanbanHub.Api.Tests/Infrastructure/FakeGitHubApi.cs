using System.Net;
using System.Text;
using KanbanHub.GitHub;

namespace KanbanHub.Api.Tests.Infrastructure;

/// <summary>A request the adapters sent: where to, as whom, and with what.</summary>
internal sealed record SentRequest(HttpMethod Method, string Path, string Query, string BearerToken, string Body)
{
    public string Host { get; init; } = "";

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Stands in for GitHub over HTTP: answers the routes a test sets up, 404 for anything else, and remembers every
/// request. It is the HTTP client factory too, so the adapters get it under whichever client name they ask for.
/// </summary>
internal sealed class FakeGitHubApi : HttpMessageHandler, IHttpClientFactory
{
    private static readonly Uri ApiAddress = new("https://api.github.com/");
    private static readonly Uri LoginAddress = new("https://github.com/");

    private readonly Dictionary<(HttpMethod, string), HttpResponseMessage> routes = [];

    public List<SentRequest> Requests { get; } = [];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false)
    {
        BaseAddress = name == GitHubUserAuthorization.LoginClientName ? LoginAddress : ApiAddress
    };

    /// <summary>Sets up an answer and returns it, so a test can add headers to it.</summary>
    public HttpResponseMessage On(HttpMethod method, string path, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        routes[(method, path)] = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath.TrimStart('/');
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new SentRequest(request.Method, path, request.RequestUri.Query, request.Headers.Authorization?.Parameter ?? "", body)
        {
            Host = request.RequestUri.Host,
            Headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase)
        });

        return routes.TryGetValue((request.Method, path), out HttpResponseMessage? prepared)
            ? await CopyAsync(prepared, cancellationToken)
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
    }

    /// <summary>The client disposes each response, so every request gets its own copy of the prepared one.</summary>
    private static async Task<HttpResponseMessage> CopyAsync(HttpResponseMessage prepared, CancellationToken cancellationToken)
    {
        var copy = new HttpResponseMessage(prepared.StatusCode)
        {
            Content = new StringContent(await prepared.Content.ReadAsStringAsync(cancellationToken), Encoding.UTF8, "application/json")
        };
        foreach (KeyValuePair<string, IEnumerable<string>> header in prepared.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return copy;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (HttpResponseMessage response in routes.Values) response.Dispose();
        }

        base.Dispose(disposing);
    }
}
