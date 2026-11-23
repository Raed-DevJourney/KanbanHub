using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace KanbanHub.GitHub;

/// <summary>Signs data with the GitHub App's private key (RS256).</summary>
public interface IAppJwtSigner
{
    Task<byte[]> SignAsync(byte[] data, CancellationToken cancellationToken);
}

/// <summary>Signs inside Key Vault with a non-exportable key, so the private key never reaches the app (section 9.1).</summary>
public sealed class KeyVaultJwtSigner(CryptographyClient key) : IAppJwtSigner
{
    public async Task<byte[]> SignAsync(byte[] data, CancellationToken cancellationToken) =>
        (await key.SignDataAsync(SignatureAlgorithm.RS256, data, cancellationToken)).Signature;
}

/// <summary>
/// Gets the tokens the app calls GitHub with: a short-lived JWT that proves it is the app, exchanged for an
/// installation token that is cached until five minutes before it expires (section 9.3).
/// </summary>
public sealed class GitHubAppTokenProvider(
    IAppJwtSigner signer, IHttpClientFactory http, IMemoryCache cache, GitHubAppOptions options, TimeProvider clock)
{
    private const string JwtHeader = """{"alg":"RS256","typ":"JWT"}""";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromMinutes(5);

    public async Task<string> GetInstallationTokenAsync(long installationId, CancellationToken cancellationToken) =>
        (await cache.GetOrCreateAsync($"gh-installation:{installationId}", entry => RequestTokenAsync(installationId, entry, cancellationToken)))!;

    /// <summary>The app's own JWT: issued a minute ago to allow for clock drift, valid for nine minutes (GitHub's limit is ten).</summary>
    public async Task<string> CreateAppJwtAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        string issuer = JavaScriptEncoder.Default.Encode(options.ClientId);
        string payload = $$"""{"iat":{{(now - ClockSkew).ToUnixTimeSeconds()}},"exp":{{(now + JwtLifetime).ToUnixTimeSeconds()}},"iss":"{{issuer}}"}""";

        string unsigned = $"{Encode(JwtHeader)}.{Encode(payload)}";
        byte[] signature = await signer.SignAsync(Encoding.ASCII.GetBytes(unsigned), cancellationToken);
        return $"{unsigned}.{Base64Url.EncodeToString(signature)}";
    }

    private async Task<string> RequestTokenAsync(long installationId, ICacheEntry entry, CancellationToken cancellationToken)
    {
        using HttpClient client = http.CreateClient(GitHubRestClient.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await CreateAppJwtAsync(cancellationToken));

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument token = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        entry.AbsoluteExpiration = token.RootElement.GetProperty("expires_at").GetDateTimeOffset() - RenewBeforeExpiry;
        return token.RootElement.GetProperty("token").GetString()!;
    }

    private static string Encode(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
