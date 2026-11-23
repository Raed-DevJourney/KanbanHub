using System.Security.Claims;
using System.Text;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Tests.Agents;

/// <summary>"Connect GitHub" against doubles for GitHub, the token store and the clock.</summary>
public class GitHubConnectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private readonly Clock clock = new(Start);
    private readonly Accounts accounts = new();
    private readonly GitHub github = new();
    private readonly GitHubConnection connection;
    private readonly ClaimsPrincipal user = Person();

    public GitHubConnectionTests() => connection = new GitHubConnection(accounts, github, new Protector(), clock);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid UserId => user.GetObjectId();

    [Fact]
    public async Task Starting_remembers_a_random_state_for_ten_minutes_and_sends_the_person_to_github()
    {
        Uri first = await connection.StartAsync(user, Ct);
        Uri second = await connection.StartAsync(user, Ct);

        string state = StateOf(first);
        Assert.Equal(64, state.Length);
        Assert.NotEqual(state, StateOf(second));
        Assert.Equal((UserId, Start.AddMinutes(10)), accounts.States[state]);
    }

    [Fact]
    public async Task Completing_with_the_state_that_was_handed_out_connects_the_account_and_keeps_only_a_protected_token()
    {
        string state = StateOf(await connection.StartAsync(user, Ct));

        Outcome<GitHubAccount> outcome = await connection.CompleteAsync("good-code", state, user, Ct);

        Assert.Equal(new GitHubAccount(UserId, 583231, "octocat"), outcome.Value);
        Assert.Equal(new GitHubAccount(UserId, 583231, "octocat"), await connection.FindAsync(user, Ct));
        Assert.Equal("protected:refresh-1", Encoding.UTF8.GetString(accounts.Tokens[UserId]));
    }

    [Theory]
    [InlineData(null, "state")]
    [InlineData("good-code", null)]
    [InlineData("good-code", " ")]
    public async Task An_incomplete_answer_from_github_connects_nothing(string? code, string? state)
    {
        Outcome<GitHubAccount> outcome = await connection.CompleteAsync(code, state, user, Ct);

        Assert.IsType<Invalid>(outcome.Value);
        Assert.Empty(accounts.Tokens);
    }

    [Fact]
    public async Task A_state_counts_once_and_only_for_the_person_who_started()
    {
        string state = StateOf(await connection.StartAsync(user, Ct));

        Outcome<GitHubAccount> someoneElse = await connection.CompleteAsync("good-code", state, Person(), Ct);
        Outcome<GitHubAccount> first = await connection.CompleteAsync("good-code", state, user, Ct);
        Outcome<GitHubAccount> replay = await connection.CompleteAsync("good-code", state, user, Ct);

        Assert.IsType<Invalid>(someoneElse.Value);
        Assert.IsType<GitHubAccount>(first.Value);
        Assert.IsType<Invalid>(replay.Value);
    }

    [Fact]
    public async Task A_state_that_ran_out_of_time_connects_nothing()
    {
        string state = StateOf(await connection.StartAsync(user, Ct));
        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.IsType<Invalid>((await connection.CompleteAsync("good-code", state, user, Ct)).Value);
    }

    [Fact]
    public async Task A_code_github_rejects_connects_nothing()
    {
        string state = StateOf(await connection.StartAsync(user, Ct));

        Assert.IsType<Invalid>((await connection.CompleteAsync("bad-code", state, user, Ct)).Value);
        Assert.Null(await connection.FindAsync(user, Ct));
    }

    [Fact]
    public async Task A_token_that_is_still_good_is_reused_without_asking_github_again()
    {
        await ConnectAsync();

        string? first = await connection.GetFreshTokenAsync(UserId, Ct);
        clock.Advance(TimeSpan.FromHours(7));
        string? later = await connection.GetFreshTokenAsync(UserId, Ct);

        Assert.Equal(("access-1", "access-1"), (first, later));
        Assert.Equal(0, github.Refreshes);
    }

    [Fact]
    public async Task Shortly_before_it_expires_the_token_is_renewed_and_the_rotated_refresh_token_replaces_the_stored_one()
    {
        await ConnectAsync();
        clock.Advance(TimeSpan.FromHours(8) - TimeSpan.FromMinutes(4));

        string? renewed = await connection.GetFreshTokenAsync(UserId, Ct);

        Assert.Equal("access-2", renewed);
        Assert.Equal(["refresh-1"], github.RefreshedWith);
        Assert.Equal("protected:refresh-2", Encoding.UTF8.GetString(accounts.Tokens[UserId]));
    }

    [Fact]
    public async Task When_github_no_longer_accepts_the_refresh_token_the_person_must_connect_again()
    {
        await ConnectAsync();
        clock.Advance(TimeSpan.FromHours(9));
        github.IsRevoked = true;

        Assert.Null(await connection.GetFreshTokenAsync(UserId, Ct));
    }

    [Fact]
    public async Task Someone_who_never_connected_has_no_token()
    {
        Assert.Null(await connection.GetFreshTokenAsync(UserId, Ct));
    }

    [Fact]
    public async Task Disconnecting_forgets_the_account_and_the_token_at_once()
    {
        await ConnectAsync();
        await connection.GetFreshTokenAsync(UserId, Ct);

        Outcome<Saved> disconnected = await connection.DisconnectAsync(user, Ct);
        Outcome<Saved> again = await connection.DisconnectAsync(user, Ct);

        Assert.IsType<Saved>(disconnected.Value);
        Assert.IsType<NotFound>(again.Value);
        Assert.Null(await connection.GetFreshTokenAsync(UserId, Ct));
        Assert.Empty(accounts.Tokens);
    }

    [Fact]
    public async Task Connecting_a_different_github_account_stops_the_first_ones_token_from_being_used()
    {
        await ConnectAsync();
        github.UserId = 999;
        github.Login = "hubot";

        await ConnectAsync();
        string? token = await connection.GetFreshTokenAsync(UserId, Ct);

        Assert.Equal("access-2", token);
        Assert.Equal("hubot", (await connection.FindAsync(user, Ct))?.Login);
    }

    private async Task ConnectAsync()
    {
        string state = StateOf(await connection.StartAsync(user, Ct));
        Assert.IsType<GitHubAccount>((await connection.CompleteAsync("good-code", state, user, Ct)).Value);
    }

    private static string StateOf(Uri authorizeUrl) => authorizeUrl.Query.Split("state=")[1];

    private static ClaimsPrincipal Person() =>
        new(new ClaimsIdentity([new Claim("oid", Guid.NewGuid().ToString())], authenticationType: "test"));

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    /// <summary>Marks what it protected, so a test can see that the store never holds the token itself.</summary>
    private sealed class Protector : ITokenProtector
    {
        private const string Mark = "protected:";

        public Task<byte[]> ProtectAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(Encoding.UTF8.GetBytes(Mark + token));

        public Task<string> UnprotectAsync(byte[] protectedToken, CancellationToken cancellationToken) =>
            Task.FromResult(Encoding.UTF8.GetString(protectedToken)[Mark.Length..]);
    }

    /// <summary>Hands out numbered token pairs that live eight hours, as GitHub does.</summary>
    private sealed class GitHub : IGitHubAuthorization
    {
        private readonly Clock clock = new(Start);
        private int issued;

        public long UserId { get; set; } = 583231;

        public string Login { get; set; } = "octocat";

        public bool IsRevoked { get; set; }

        public List<string> RefreshedWith { get; } = [];

        public int Refreshes => RefreshedWith.Count;

        public Uri AuthorizeUrl(string state) => new($"https://github.test/authorize?state={state}");

        public Task<AuthorizedUser?> ExchangeAsync(string code, CancellationToken cancellationToken) =>
            Task.FromResult(code == "good-code" ? new AuthorizedUser(UserId, Login, Next()) : null);

        public Task<UserTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            RefreshedWith.Add(refreshToken);
            return Task.FromResult(IsRevoked ? null : Next());
        }

        private UserTokens Next()
        {
            issued++;
            return new UserTokens($"access-{issued}", $"refresh-{issued}", clock.GetUtcNow().AddHours(8 * issued));
        }
    }

    private sealed class Accounts : IGitHubAccountStore
    {
        private readonly Dictionary<Guid, GitHubAccount> accounts = [];

        public Dictionary<Guid, byte[]> Tokens { get; } = [];

        public Dictionary<string, (Guid UserId, DateTimeOffset ExpiresAt)> States { get; } = [];

        public Task<GitHubAccount?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(accounts.GetValueOrDefault(userId));

        public Task<byte[]?> FindRefreshTokenAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(Tokens.GetValueOrDefault(userId));

        public Task SaveAsync(GitHubAccount account, byte[] protectedRefreshToken, DateTimeOffset now, CancellationToken cancellationToken)
        {
            accounts[account.UserId] = account;
            Tokens[account.UserId] = protectedRefreshToken;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(accounts.Remove(userId) & Tokens.Remove(userId));

        public Task RememberStateAsync(string state, Guid userId, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken)
        {
            States[state] = (userId, expiresAt);
            return Task.CompletedTask;
        }

        public Task<bool> ConsumeStateAsync(string state, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            bool isGood = States.TryGetValue(state, out var known) && known.UserId == userId && known.ExpiresAt >= now;
            if (isGood) States.Remove(state);
            return Task.FromResult(isGood);
        }
    }
}
