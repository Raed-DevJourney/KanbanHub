using KanbanHub.Web.Api;

namespace KanbanHub.Web.State;

public enum NoticeKind
{
    Info,
    Success,
    Problem
}

/// <summary>A message for the person at the screen; <paramref name="Details"/> list what they can do about it.</summary>
public sealed record Notice(NoticeKind Kind, string Message, IReadOnlyList<string> Details)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>
/// What the app tells people: toasts they see, and announcements a screen reader speaks
/// from the live region (specification section 16.3).
/// </summary>
public sealed class Notices
{
    private readonly List<Notice> toasts = [];

    public event Action? Changed;

    public IReadOnlyList<Notice> Toasts => toasts;

    /// <summary>The latest announcement for assistive technology.</summary>
    public string Announcement { get; private set; } = "";

    public void Announce(string message)
    {
        Announcement = message;
        Changed?.Invoke();
    }

    public void Show(NoticeKind kind, string message, params IReadOnlyList<string> details)
    {
        toasts.Add(new Notice(kind, message, details));
        Announce(message);
    }

    public void ShowProblem(ApiProblem problem) =>
        Show(NoticeKind.Problem, problem.Detail ?? problem.Title ?? "That didn't work.", [.. problem.Errors, .. problem.UnmetItems]);

    public void Dismiss(Guid noticeId)
    {
        toasts.RemoveAll(notice => notice.Id == noticeId);
        Changed?.Invoke();
    }
}
