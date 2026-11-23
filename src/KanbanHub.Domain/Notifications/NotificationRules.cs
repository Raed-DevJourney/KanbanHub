using KanbanHub.Domain.Events;

namespace KanbanHub.Domain.Notifications;

/// <summary>The kinds of notification this release sends (a subset of specification section 11.3).</summary>
public enum NotificationKind
{
    CardBlocked,
    CardUnblocked,
    WipOverrideUsed,
    CommentAdded,

    /// <summary>A card has been in flow longer than 85% of its kind take to finish (section 11.1, feature 4).</summary>
    CardAging
}

/// <summary>Where a notification goes for one person.</summary>
public readonly record struct Channels(bool InApp, bool Email)
{
    public bool Any => InApp || Email;
}

public static class ChannelPolicy
{
    private static readonly Channels InAppOnly = new(InApp: true, Email: false);

    /// <summary>What a person gets when they haven't chosen: in-app for every kind this release sends.</summary>
    public static Channels DefaultFor(NotificationKind kind) => InAppOnly;

    /// <summary>A WIP override is always shown in-app: section 11.3 marks it as one people can't turn off.</summary>
    public static bool CanMute(NotificationKind kind) => kind is not NotificationKind.WipOverrideUsed;

    /// <summary>The person's choice where they made one, corrected for kinds that can't be switched off.</summary>
    public static Channels Resolve(NotificationKind kind, Channels? preference)
    {
        Channels chosen = preference ?? DefaultFor(kind);
        return CanMute(kind) ? chosen : chosen with { InApp = true };
    }
}

/// <summary>What the planner knows about the card an event is about, and the people around it.</summary>
public sealed record CardEventContext
{
    public required int CardNumber { get; init; }

    public required string CardTitle { get; init; }

    public required Guid CreatedBy { get; init; }

    /// <summary>Who caused the event; they are never notified about their own action.</summary>
    public required Guid ActorId { get; init; }

    /// <summary>The block reason, the override reason or the comment text.</summary>
    public string? Detail { get; init; }

    /// <summary>The move went past an "allow with reason" WIP limit.</summary>
    public bool IsWipOverride { get; init; }

    /// <summary>People who hold the Owner or Flow Manager role on the board.</summary>
    public IReadOnlyList<Guid> Managers { get; init; } = [];

    /// <summary>People who commented on the card.</summary>
    public IReadOnlyList<Guid> Participants { get; init; } = [];

    /// <summary>People who muted the card.</summary>
    public IReadOnlyList<Guid> Muted { get; init; } = [];
}

/// <summary>A notification the rules call for, before anyone's channel choices are applied.</summary>
public sealed record PlannedNotification(Guid UserId, NotificationKind Kind, string Title, string? Detail);

/// <summary>Decides who hears about an event. Pure rules: no storage, no clock.</summary>
public static class NotificationPlanner
{
    public const int MaxDetailLength = 200;

    public static IReadOnlyList<PlannedNotification> Plan(BoardEvent boardEvent, CardEventContext card)
    {
        string name = $"#{card.CardNumber} {card.CardTitle}";
        return boardEvent.Type switch
        {
            BoardEvent.CardBlocked => ForFollowers(card, NotificationKind.CardBlocked, $"{name} is blocked"),
            BoardEvent.CardUnblocked => ForFollowers(card, NotificationKind.CardUnblocked, $"{name} is unblocked"),
            BoardEvent.CardCommented => ForConversation(card, $"New comment on {name}"),
            BoardEvent.CardMoved when card.IsWipOverride => ForManagers(card, $"A WIP limit was overridden for {name}"),
            _ => []
        };
    }

    /// <summary>
    /// An aging card concerns its creator and the board's managers, unless they muted it. Nobody caused it, so
    /// nobody is left out as "the one who did it".
    /// </summary>
    public static IReadOnlyList<PlannedNotification> PlanAging(CardEventContext card, string detail) =>
    [
        .. new[] { card.CreatedBy }.Concat(card.Managers)
            .Distinct()
            .Where(user => !card.Muted.Contains(user))
            .Select(user => new PlannedNotification(user, NotificationKind.CardAging, $"#{card.CardNumber} {card.CardTitle} is aging", Shorten(detail)))
    ];

    /// <summary>The card's creator and the board's managers, unless they muted the card.</summary>
    private static IReadOnlyList<PlannedNotification> ForFollowers(CardEventContext card, NotificationKind kind, string title) =>
        Notify(card, kind, title, [card.CreatedBy, .. card.Managers], respectMute: true);

    /// <summary>The card's creator and everyone who commented before, unless they muted the card.</summary>
    private static IReadOnlyList<PlannedNotification> ForConversation(CardEventContext card, string title) =>
        Notify(card, NotificationKind.CommentAdded, title, [card.CreatedBy, .. card.Participants], respectMute: true);

    /// <summary>Overrides go to the board's managers whatever they muted: they are accountable for the limits.</summary>
    private static IReadOnlyList<PlannedNotification> ForManagers(CardEventContext card, string title) =>
        Notify(card, NotificationKind.WipOverrideUsed, title, card.Managers, respectMute: false);

    private static IReadOnlyList<PlannedNotification> Notify(
        CardEventContext card, NotificationKind kind, string title, IEnumerable<Guid> candidates, bool respectMute) =>
    [
        .. candidates
            .Distinct()
            .Where(user => user != card.ActorId && !(respectMute && card.Muted.Contains(user)))
            .Select(user => new PlannedNotification(user, kind, title, Shorten(card.Detail)))
    ];

    private static string? Shorten(string? detail) =>
        detail is { Length: > MaxDetailLength } ? string.Concat(detail.AsSpan(0, MaxDetailLength - 1), "…") : detail;
}
