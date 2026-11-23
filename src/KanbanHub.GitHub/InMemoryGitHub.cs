using KanbanHub.Domain.GitHub;

namespace KanbanHub.GitHub;

/// <summary>An issue as <see cref="InMemoryGitHub"/> keeps it.</summary>
public sealed class StoredIssue(IssueRef reference)
{
    public IssueRef Reference { get; } = reference;

    public string Title { get; set; } = "";

    public string? Body { get; set; }

    public List<string> Labels { get; } = [];

    public IssueState State { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The comments on the issue, by id, in the order they were added.</summary>
    public Dictionary<long, string> Comments { get; } = [];

    public IssueSnapshot ToSnapshot() => new()
    {
        Issue = Reference,
        NodeId = $"I_{Reference.RepositoryId}_{Reference.Number}",
        Title = Title,
        Body = Body,
        Labels = [.. Labels],
        State = State,
        UpdatedAt = UpdatedAt
    };
}

/// <summary>
/// GitHub, in memory: for the local dev host and for tests, where there is no GitHub App to call.
/// It keeps issues, comments and check runs, and remembers every write so a test can see what was sent.
/// </summary>
public sealed class InMemoryGitHub(TimeProvider clock) : IGitHubClient
{
    private const long InstallationId = 1;

    private readonly Dictionary<string, long> repositories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(long RepositoryId, int Number), StoredIssue> issues = [];
    private long nextId = Random.Shared.NextInt64(1_000_000, 1_000_000_000_000);   // unlike another instance's, so their repositories never collide

    public IReadOnlyCollection<StoredIssue> Issues => issues.Values;

    /// <summary>Every issue update the sync sent, oldest first.</summary>
    public List<(IssueRef Issue, IssueChange Change)> Updates { get; } = [];

    public Dictionary<long, CheckRun> CheckRuns { get; } = [];

    /// <summary>Repositories the app is "not installed" on.</summary>
    public HashSet<string> NotInstalled { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>While set, every call is refused the way a rate limit refuses it.</summary>
    public DateTimeOffset? RateLimitedUntil { get; set; }

    public int PageSize { get; set; } = 100;

    public StoredIssue IssueOf(IssueRef issue) => issues[(issue.RepositoryId, issue.Number)];

    /// <summary>Adds an issue the way a person on GitHub would: without the sync knowing.</summary>
    public StoredIssue Open(RepoRef repository, string title, params string[] labels)
    {
        int number = issues.Keys.Where(key => key.RepositoryId == repository.RepositoryId).Select(key => key.Number).DefaultIfEmpty().Max() + 1;
        var issue = new StoredIssue(new IssueRef(repository.InstallationId, repository.RepositoryId, number)) { Title = title, UpdatedAt = clock.GetUtcNow() };
        issue.Labels.AddRange(labels);
        issues[(repository.RepositoryId, number)] = issue;
        return issue;
    }

    public Task<RepositoryInfo?> FindRepositoryAsync(string fullName, CancellationToken cancellationToken)
    {
        ThrowIfRateLimited();
        if (NotInstalled.Contains(fullName)) return Task.FromResult<RepositoryInfo?>(null);
        if (!repositories.TryGetValue(fullName, out long id)) repositories[fullName] = id = nextId++;

        return Task.FromResult<RepositoryInfo?>(new RepositoryInfo(new RepoRef(InstallationId, id), fullName));
    }

    public Task<IssueSnapshot?> FindIssueAsync(IssueRef issue, CancellationToken cancellationToken)
    {
        ThrowIfRateLimited();
        return Task.FromResult(issues.GetValueOrDefault((issue.RepositoryId, issue.Number))?.ToSnapshot());
    }

    public Task<IssueSnapshot> CreateIssueAsync(RepoRef repository, IssueChange content, CancellationToken cancellationToken)
    {
        ThrowIfRateLimited();
        StoredIssue issue = Open(repository, content.Title ?? "");
        issue.Body = content.Body;
        return Task.FromResult(issue.ToSnapshot());
    }

    public Task UpdateIssueAsync(IssueRef issue, IssueChange change, CancellationToken cancellationToken)
    {
        ThrowIfRateLimited();
        StoredIssue stored = Known(issue);
        stored.Title = change.Title ?? stored.Title;
        stored.Body = change.Body ?? stored.Body;
        stored.State = change.State ?? stored.State;
        if (change.Labels is { } labels)
        {
            stored.Labels.Clear();
            stored.Labels.AddRange(labels);
        }

        stored.UpdatedAt = clock.GetUtcNow();
        Updates.Add((issue, change));
        return Task.CompletedTask;
    }

    public Task<long> AddCommentAsync(IssueRef issue, string body, CancellationToken cancellationToken)
    {
        ThrowIfRateLimited();
        long id = nextId++;
        Known(issue).Comments[id] = body;
        return Task.FromResult(id);
    }

    public Task<bool> EditCommentAsync(RepoRef repository, long commentId, string body, CancellationToken cancellationToken)
    {
        ThrowIfRateLimited();
        if (issues.Values.FirstOrDefault(issue => issue.Comments.ContainsKey(commentId)) is not { } commented)
            return Task.FromResult(false);

        commented.Comments[commentId] = body;
        return Task.FromResult(true);
    }

    public Task<IssuePage> ListIssuesAsync(RepoRef repository, IssueQuery query, CancellationToken cancellationToken)
    {
        ThrowIfRateLimited();
        StoredIssue[] matching = [.. issues.Values.Where(issue => Matches(issue, repository, query)).OrderBy(issue => issue.Reference.Number)];
        IssueSnapshot[] page = [.. matching.Skip((query.Page - 1) * PageSize).Take(PageSize).Select(issue => issue.ToSnapshot())];

        return Task.FromResult(new IssuePage(page, LastPage: Math.Max(1, (matching.Length + PageSize - 1) / PageSize)));
    }

    public Task<long> SaveCheckRunAsync(RepoRef repository, long? checkRunId, CheckRun run, CancellationToken cancellationToken)
    {
        ThrowIfRateLimited();
        long id = checkRunId ?? nextId++;
        CheckRuns[id] = run;
        return Task.FromResult(id);
    }

    /// <summary>Tells the stand-in about a repository the database already links, so it keeps its id across a restart.</summary>
    public void Remember(RepositoryInfo repository) => repositories[repository.FullName] = repository.Repo.RepositoryId;

    /// <summary>
    /// The issue the sync writes to. Everything here is forgotten when the process stops, while the database
    /// keeps its links: an issue it still points to is taken on again, empty.
    /// </summary>
    public StoredIssue Known(IssueRef issue)
    {
        if (!issues.TryGetValue((issue.RepositoryId, issue.Number), out StoredIssue? stored))
            issues[(issue.RepositoryId, issue.Number)] = stored = new StoredIssue(issue) { Title = "(an issue from an earlier run)", UpdatedAt = clock.GetUtcNow() };

        return stored;
    }

    private static bool Matches(StoredIssue issue, RepoRef repository, IssueQuery query) =>
        issue.Reference.RepositoryId == repository.RepositoryId
        && (query.UpdatedSince is { } since ? issue.UpdatedAt >= since : issue.State is IssueState.Open)
        && (query.Label is null || issue.Labels.Contains(query.Label, StringComparer.OrdinalIgnoreCase));

    private void ThrowIfRateLimited()
    {
        if (RateLimitedUntil is { } until) throw new GitHubRateLimitedException(until);
    }
}
