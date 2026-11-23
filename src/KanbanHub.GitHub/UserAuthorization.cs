using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Security.KeyVault.Keys.Cryptography;
using KanbanHub.Domain.Agents;

namespace KanbanHub.GitHub;

/// <summary>
/// The GitHub App's user authorization flow (section 9.1): sends a person to GitHub to approve the app, trades
/// the code they come back with for user-to-server tokens, and renews those. The app must keep
/// "Expire user authorization tokens" switched on: only then does GitHub hand out the refresh token that is stored.
/// </summary>
public sealed class GitHubUserAuthorization(IHttpClientFactory http, GitHubAppOptions options, TimeProvider clock) : IGitHubAuthorization
{
    /// <summary>The HTTP client for <c>github.com/login</c>, which is not part of the REST API.</summary>
    public const string LoginClientName = "github-login";

    public Uri AuthorizeUrl(string state)
    {
        var url = new StringBuilder("https://github.com/login/oauth/authorize?client_id=").Append(Uri.EscapeDataString(options.ClientId));
        if (options.CallbackUrl is { } callback) url.Append("&redirect_uri=").Append(Uri.EscapeDataString(callback));

        return new Uri(url.Append("&state=").Append(Uri.EscapeDataString(state)).ToString());
    }

    public async Task<AuthorizedUser?> ExchangeAsync(string code, CancellationToken cancellationToken)
    {
        Dictionary<string, string> grant = new() { ["code"] = code };
        if (options.CallbackUrl is { } callback) grant["redirect_uri"] = callback;
        if (await RequestTokensAsync(grant, cancellationToken) is not { } tokens) return null;

        using HttpClient client = http.CreateClient(GitHubRestClient.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument user = await ReadAsync(response, cancellationToken);
        return new AuthorizedUser(user.RootElement.GetProperty("id").GetInt64(), user.RootElement.GetProperty("login").GetString()!, tokens);
    }

    public Task<UserTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        RequestTokensAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, cancellationToken);

    /// <summary>GitHub answers 200 either way: with tokens, or with an <c>error</c> when it rejects the grant.</summary>
    private async Task<UserTokens?> RequestTokensAsync(Dictionary<string, string> grant, CancellationToken cancellationToken)
    {
        grant["client_id"] = options.ClientId;
        grant["client_secret"] = options.ClientSecret;

        using HttpClient client = http.CreateClient(LoginClientName);
        using var form = new FormUrlEncodedContent(grant);
        using HttpResponseMessage response = await client.PostAsync("login/oauth/access_token", form, cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument answer = await ReadAsync(response, cancellationToken);
        return GitHubJson.Text(answer.RootElement, "access_token") is { } accessToken ? TokensOf(answer.RootElement, accessToken) : null;
    }

    private UserTokens TokensOf(JsonElement answer, string accessToken)
    {
        string refreshToken = GitHubJson.Text(answer, "refresh_token") ?? throw new InvalidOperationException(
            "GitHub sent no refresh token. Switch on 'Expire user authorization tokens' for the GitHub App.");
        long lifetime = GitHubJson.Number(answer, "expires_in") ?? 0;

        return new UserTokens(accessToken, refreshToken, clock.GetUtcNow().AddSeconds(lifetime));
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Encrypts refresh tokens with a Key Vault key before they are stored (section 17). The key never leaves the
/// vault, so a copy of the database alone gives nobody a way to act as a user on GitHub.
/// </summary>
public sealed class KeyVaultTokenProtector(CryptographyClient key) : ITokenProtector
{
    public async Task<byte[]> ProtectAsync(string token, CancellationToken cancellationToken) =>
        (await key.EncryptAsync(EncryptionAlgorithm.RsaOaep256, Encoding.UTF8.GetBytes(token), cancellationToken)).Ciphertext;

    public async Task<string> UnprotectAsync(byte[] protectedToken, CancellationToken cancellationToken) =>
        Encoding.UTF8.GetString((await key.DecryptAsync(EncryptionAlgorithm.RsaOaep256, protectedToken, cancellationToken)).Plaintext);
}

/// <summary>
/// Stands in for GitHub's user authorization on a development machine and in tests: it approves at once, sending
/// the browser straight back to the callback page, and hands out tokens that only the other stand-ins accept.
/// </summary>
public sealed class InMemoryGitHubAuthorization(Uri callback, TimeProvider clock) : IGitHubAuthorization
{
    public const string Code = "local-code";

    private const string RefreshPrefix = "local-refresh-";

    private int _issued;

    public long UserId { get; set; } = 583231;

    public string Login { get; set; } = "octocat";

    /// <summary>The person withdrew the authorization on GitHub: refresh tokens stop working.</summary>
    public bool IsRevoked { get; set; }

    public Uri AuthorizeUrl(string state) => new($"{callback}?code={Code}&state={Uri.EscapeDataString(state)}");

    public Task<AuthorizedUser?> ExchangeAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(code == Code ? new AuthorizedUser(UserId, Login, NextTokens()) : null);

    public Task<UserTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        Task.FromResult(!IsRevoked && refreshToken.StartsWith(RefreshPrefix, StringComparison.Ordinal) ? NextTokens() : null);

    private UserTokens NextTokens()
    {
        int number = Interlocked.Increment(ref _issued);
        return new UserTokens($"local-access-{number}", $"{RefreshPrefix}{number}", clock.GetUtcNow().AddHours(8));
    }
}

/// <summary>
/// Stores tokens as they are. Only for the stand-in above, whose tokens open nothing: in Azure,
/// <see cref="KeyVaultTokenProtector"/> is the one registered.
/// </summary>
public sealed class LocalTokenProtector : ITokenProtector
{
    public Task<byte[]> ProtectAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult(Encoding.UTF8.GetBytes(token));

    public Task<string> UnprotectAsync(byte[] protectedToken, CancellationToken cancellationToken) =>
        Task.FromResult(Encoding.UTF8.GetString(protectedToken));
}
