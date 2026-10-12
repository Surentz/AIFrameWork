namespace AiFramework.Application.Assistant.Quick;

/// <summary>What an intent must resolve from the question before it can answer.</summary>
public enum QuickParameter
{
    None,
    Order,
    Product,
    Area,
    Window,
}

/// <summary>
/// One question the router answers without a model: who may ask it, what it must resolve, and the
/// question a suggestion chip sends for it. The canonical question is one of the intent's own
/// example phrases, so a chip is always answered instantly. ADR 0036.
/// </summary>
public sealed record QuickIntent(string Id, ToolScope Scope, QuickParameter Parameter, string CanonicalQuestion);

public static class QuickIntents
{
    public const string OrderStatus = "order_status";
    public const string RecentOrders = "recent_orders";
    public const string ExportStatus = "export_status";
    public const string ProductInfo = "product_info";
    public const string Population = "population";
    public const string JobHealth = "job_health";
    public const string FailedJobs = "failed_jobs";
    public const string DeadLetters = "dead_letters";
    public const string Traffic = "traffic";
    public const string Integrations = "integrations";
    public const string SignIns = "sign_ins";

    public static IReadOnlyList<QuickIntent> All { get; } =
    [
        new(OrderStatus, ToolScope.Member, QuickParameter.Order, "Has my last order shipped?"),
        new(RecentOrders, ToolScope.Member, QuickParameter.None, "Show my recent orders"),
        new(ExportStatus, ToolScope.Member, QuickParameter.None, "Is my export ready?"),
        new(ProductInfo, ToolScope.Member, QuickParameter.Product, "What does a product cost?"),
        new(Population, ToolScope.Member, QuickParameter.Area, "What is the population of Denmark?"),
        new(JobHealth, ToolScope.Operator, QuickParameter.Window, "Did any background job fail today?"),
        new(FailedJobs, ToolScope.Operator, QuickParameter.None, "Which jobs failed?"),
        new(DeadLetters, ToolScope.Operator, QuickParameter.None, "Is anything dead-lettered?"),
        new(Traffic, ToolScope.Operator, QuickParameter.Window, "How is the API doing?"),
        new(Integrations, ToolScope.Operator, QuickParameter.None, "Are the partner systems up?"),
        new(SignIns, ToolScope.Operator, QuickParameter.Window, "Are any accounts locked out?"),
    ];

    public static QuickIntent? Find(string id) =>
        All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    /// <summary>A member gets member intents; an operator gets every intent.</summary>
    public static bool Allows(ToolScope scope, QuickIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return scope == ToolScope.Operator || intent.Scope == ToolScope.Member;
    }
}
