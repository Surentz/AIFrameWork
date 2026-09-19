namespace AiFramework.Domain.Notifications;

/// <summary>
/// What a notification is about. Persisted by name rather than by number (see the EF
/// configuration), so the numeric values here are free to be reordered — but the NAMES are a
/// stored contract: renaming one orphans every row already written under the old name.
/// </summary>
public enum NotificationKind
{
    /// <summary>An order the caller placed was accepted.</summary>
    OrderPlaced,

    /// <summary>An order the caller placed has shipped.</summary>
    OrderShipped,

    /// <summary>An order the caller placed was cancelled.</summary>
    OrderCancelled,

    /// <summary>
    /// The price changed on a product the caller has ordered before. The only kind not caused by
    /// something the caller themselves did, which is why it is the one that needs a rule for who
    /// receives it — see ProductPriceChangedNotifier in Application.
    /// </summary>
    ProductPriceChanged,
}
