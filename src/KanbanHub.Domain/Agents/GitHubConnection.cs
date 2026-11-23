using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Agents;

/// <summary>A person's connected GitHub account.</summary>
public sealed record class GitHubAccount(Guid UserId, long GitHubUserId, string Login);

/// <summary>A user-to-server token pair: the short-lived token to call GitHub with, and the one that renews it.</summary>
public sealed record class UserTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>What GitHub says after a person approved the app: who they are there, and their tokens.</summary>
public sealed record class AuthorizedUser(long GitHubUserId, string Login, UserTokens Tokens);

/// <summary>GitHub's side of "Connect GitHub": the app's user authorization flow (section 9.1).</summary>
public interface IGitHubAuthorization
{
    /// <summary>Where to send the browser so the person can approve the app.</summary>
    Uri AuthorizeUrl(string state);

    /// <summary>Trades the code GitHub handed back for the person's tokens, or null when GitHub rejects the code.</summary>
    Task<AuthorizedUser?> ExchangeAsync(string code, CancellationToken cancellationToken);

    /// <summary>A new token pair, or null when the refresh token is no longer good (the person must connect again).</summary>
    Task<UserTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}

/// <summary>Encrypts refresh tokens before they are stored: with a Key Vault key in Azure (section 17).</summary>
public interface ITokenProtector
{
    Task<byte[]> ProtectAsync(string token, CancellationToken cancellationToken);

    Task<string> UnprotectAsync(byte[] protectedToken, CancellationToken cancellationToken);
}

/// <summary>Storage port for connected accounts and for authorizations that are under way.</summary>
public interface IGitHubAccountStore
{
    Task<GitHubAccount?> FindAsync(Guid userId, CancellationToken cancellationToken);

    Task<byte[]?> FindRefreshTokenAsync(Guid userId, CancellationToken cancellationToken);

    Task SaveAsync(GitHubAccount account, byte[] protectedRefreshToken, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(Guid userId, CancellationToken cancellationToken);

    Task RememberStateAsync(string state, Guid userId, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>True once, for the person who started the authorization, while the state is fresh.</summary>
    Task<bool> ConsumeStateAsync(string state, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// "Connect GitHub": links the signed-in person to their GitHub account, so agents can be dispatched in their
/// name and on their Copilot plan. Only the encrypted refresh token is kept; access tokens are asked for when needed.
/// </summary>
public sealed class GitHubConnection(
    IGitHubAccountStore accounts, IGitHubAuthorization github, ITokenProtector protector, TimeProvider clock)
{
    /// <summary>How long a person has to approve the app on GitHub before they must start again.</summary>
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromMinutes(5);

    private const int StateBytes = 32;

    /// <summary>
    /// Access tokens still good, per person, in memory only. GitHub replaces the refresh token on every renewal,
    /// so renewing on each call would be slow and would let two requests at once invalidate each other.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Fresh> _fresh = new();

    private sealed record Fresh(long GitHubUserId, UserTokens Tokens);

    public Task<GitHubAccount?> FindAsync(ClaimsPrincipal user, CancellationToken cancellationToken) =>
        accounts.FindAsync(user.GetObjectId(), cancellationToken);

    /// <summary>
    /// Starts the authorization. The random state ties GitHub's answer to this person, so nobody can get
    /// someone else's session connected to a GitHub account of their choosing.
    /// </summary>
    public async Task<Uri> StartAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        string state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(StateBytes));
        DateTimeOffset now = clock.GetUtcNow();
        await accounts.RememberStateAsync(state, user.GetObjectId(), now + StateLifetime, now, cancellationToken);

        return github.AuthorizeUrl(state);
    }

    public async Task<Outcome<GitHubAccount>> CompleteAsync(string? code, string? state, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return new Invalid("GitHub's answer is incomplete. Start connecting again.");
        if (!await accounts.ConsumeStateAsync(state, user.GetObjectId(), clock.GetUtcNow(), cancellationToken))
            return new Invalid("This connection attempt is not yours or has expired. Start connecting again.");
        if (await github.ExchangeAsync(code, cancellationToken) is not { } authorized)
            return new Invalid("GitHub didn't accept the authorization. Start connecting again.");

        var account = new GitHubAccount(user.GetObjectId(), authorized.GitHubUserId, authorized.Login);
        await StoreAsync(account, authorized.Tokens, cancellationToken);
        return account;
    }

    public async Task<Outcome<Saved>> DisconnectAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        _fresh.TryRemove(user.GetObjectId(), out _);
        return await accounts.RemoveAsync(user.GetObjectId(), cancellationToken) ? Saved.Instance : new NotFound("GitHub connection");
    }

    /// <summary>
    /// A token to call GitHub with as this person, or null when they aren't connected or must connect again.
    /// The stored connection is checked every time, so disconnecting takes effect at once.
    /// </summary>
    public async Task<string?> GetFreshTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await accounts.FindAsync(userId, cancellationToken) is not { } account) return null;
        if (FindFresh(account) is { } known) return known;
        if (await accounts.FindRefreshTokenAsync(userId, cancellationToken) is not { } protectedToken) return null;

        string refreshToken = await protector.UnprotectAsync(protectedToken, cancellationToken);
        if (await github.RefreshAsync(refreshToken, cancellationToken) is not { } tokens) return null;

        await StoreAsync(account, tokens, cancellationToken);
        return tokens.AccessToken;
    }

    private string? FindFresh(GitHubAccount account) =>
        _fresh.TryGetValue(account.UserId, out Fresh? fresh)
        && fresh.GitHubUserId == account.GitHubUserId
        && fresh.Tokens.ExpiresAt - RenewBeforeExpiry > clock.GetUtcNow()
            ? fresh.Tokens.AccessToken
            : null;

    /// <summary>GitHub replaces the refresh token on every renewal, so the new one replaces the stored one.</summary>
    private async Task StoreAsync(GitHubAccount account, UserTokens tokens, CancellationToken cancellationToken)
    {
        byte[] protectedToken = await protector.ProtectAsync(tokens.RefreshToken, cancellationToken);
        await accounts.SaveAsync(account, protectedToken, clock.GetUtcNow(), cancellationToken);
        _fresh[account.UserId] = new Fresh(account.GitHubUserId, tokens);
    }
}
