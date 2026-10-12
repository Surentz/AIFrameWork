using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Application.Orders;
using AiFramework.Application.Products;
using AiFramework.Application.Statistics;
using AiFramework.Domain.Orders;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// One method per intent: dispatch the same caller-scoped queries the AI's tools use, then build
/// the answer from their values and nothing else. A failed read is said plainly, never filled in. A
/// name too close to call is asked about, never picked. Monitoring answers carry counts and names
/// only — no error text — so nothing free-form reaches a replay. ADR 0036.
/// </summary>
public sealed class QuickAnswerComposer(IQueryDispatcher queries, ParameterResolver parameters)
{
    public const int RecentOrderCount = 5;

    public const int TableRows = 5;

    public const int OrderPrefixScan = 25;

    public const int ProductCandidates = 5;

    /// <summary>How far the best product must lead the next to be named without asking.</summary>
    public const double ProductLead = 0.1;

    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(15);

    private static readonly QuickLink AllOrders = new("All orders", "/orders");
    private static readonly QuickLink PlaceOrder = new("Place an order", "/orders/new");
    private static readonly QuickLink Exports = new("Go to Exports", "/orders/exports");
    private static readonly QuickLink Catalogue = new("Open the catalogue", "/products");
    private static readonly QuickLink PopulationPage = new("Population figures", "/statistics/population");
    private static readonly QuickLink JobRuns = new("Job runs", "/monitoring/jobs");
    private static readonly QuickLink TrafficPage = new("Traffic", "/monitoring/traffic");
    private static readonly QuickLink IntegrationsPage = new("External systems", "/monitoring/integrations");
    private static readonly QuickLink SignInsPage = new("Sign-ins", "/monitoring/logins");

    public static QuickAnswer Help(HelpSection section, string? answeredAs)
    {
        ArgumentNullException.ThrowIfNull(section);
        return new QuickAnswer(QuickAnswerKind.Help, section.Title, section.Text, [], null, section.Link, answeredAs);
    }

    public async Task<QuickReply> ComposeAsync(QuickIntent intent, string question, string? answeredAs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(question);

        QuickReply reply = intent.Id switch
        {
            QuickIntents.OrderStatus => await OrderStatusAsync(question, cancellationToken).ConfigureAwait(false),
            QuickIntents.RecentOrders => await RecentOrdersAsync(cancellationToken).ConfigureAwait(false),
            QuickIntents.ExportStatus => await ExportStatusAsync(cancellationToken).ConfigureAwait(false),
            QuickIntents.ProductInfo => await ProductAsync(question, cancellationToken).ConfigureAwait(false),
            QuickIntents.Population => await PopulationAsync(question, cancellationToken).ConfigureAwait(false),
            QuickIntents.JobHealth => await JobHealthAsync(question, cancellationToken).ConfigureAwait(false),
            QuickIntents.FailedJobs => await FailedJobsAsync(cancellationToken).ConfigureAwait(false),
            QuickIntents.DeadLetters => await DeadLettersAsync(cancellationToken).ConfigureAwait(false),
            QuickIntents.Traffic => await TrafficAsync(question, cancellationToken).ConfigureAwait(false),
            QuickIntents.Integrations => await IntegrationsAsync(cancellationToken).ConfigureAwait(false),
            QuickIntents.SignIns => await SignInsAsync(question, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"No composer for intent '{intent.Id}'."),
        };

        return reply is QuickAnswer answer ? answer with { AnsweredAs = answeredAs } : reply;
    }

    private static QuickAnswer Answer(string text, IReadOnlyList<QuickFact> facts, QuickTable? table, QuickLink link) =>
        new(QuickAnswerKind.Instant, null, text, facts, table, link, null);

    private static QuickAnswer CouldNotLoad(string what, QuickLink link) =>
        Answer($"{what} couldn't be loaded right now. Try again in a moment.", [], null, link);

    private static bool Failed<T>(Result<T>? result) => result is null || !result.IsSuccess;

    private async Task<QuickReply> OrderStatusAsync(string question, CancellationToken ct)
    {
        var reference = ParameterResolver.OrderId(question);
        if (reference is null && ParameterResolver.OrderPrefix(question) is { } prefix)
        {
            var recent = await queries.SendAsync(new GetOrders(OrderPrefixScan, null), ct).ConfigureAwait(false);
            if (Failed(recent))
            {
                return CouldNotLoad("Your orders", AllOrders);
            }

            var hit = recent.Value.Items.FirstOrDefault(o => string.Equals(QuickText.ShortId(o.Id), prefix, StringComparison.Ordinal));
            if (hit is null)
            {
                return Answer($"I couldn't find order {prefix} among your orders.", [], null, AllOrders);
            }

            reference = hit.Id;
        }

        string subject;
        if (reference is { } explicitId)
        {
            subject = $"Order {QuickText.ShortId(explicitId)}";
        }
        else
        {
            var newest = await queries.SendAsync(new GetOrders(1, null), ct).ConfigureAwait(false);
            if (Failed(newest))
            {
                return CouldNotLoad("Your orders", AllOrders);
            }

            if (newest.Value.Items.Count == 0)
            {
                return Answer("You haven't placed any orders yet.", [], null, PlaceOrder);
            }

            reference = newest.Value.Items[0].Id;
            subject = "Your last order";
        }

        var read = await queries.SendAsync(new GetOrder(reference.Value), ct).ConfigureAwait(false);
        if (Failed(read))
        {
            return read?.Error.Kind == ErrorKind.NotFound
                ? Answer($"I couldn't find order {QuickText.ShortId(reference.Value)} among your orders.", [], null, AllOrders)
                : CouldNotLoad("Your order", AllOrders);
        }

        return OrderAnswer(read.Value, subject);
    }

    private static QuickAnswer OrderAnswer(OrderView order, string subject)
    {
        var text = order.Status switch
        {
            OrderStatus.Shipped => $"{subject} shipped on {QuickText.Date(order.ShippedAt ?? order.PlacedAt)}.",
            OrderStatus.Cancelled => $"{subject} was cancelled on {QuickText.Date(order.CancelledAt ?? order.PlacedAt)}.",
            _ => $"{subject} has not shipped yet. It was placed on {QuickText.Date(order.PlacedAt)}.",
        };

        return Answer(
            text,
            [
                new("Product", order.ProductName is { } name ? $"{name} ({order.Sku})" : order.Sku),
                new("Quantity", order.UnitPrice is { } price ? $"{QuickText.Count(order.Quantity)} × {QuickText.Money(price)}" : QuickText.Count(order.Quantity)),
                new("Status", order.Status.ToString()),
                new("Placed", QuickText.Date(order.PlacedAt)),
            ],
            null,
            new QuickLink("Open order", $"/orders/{order.Id}"));
    }

    private async Task<QuickReply> RecentOrdersAsync(CancellationToken ct)
    {
        var page = await queries.SendAsync(new GetOrders(RecentOrderCount, null), ct).ConfigureAwait(false);
        if (Failed(page))
        {
            return CouldNotLoad("Your orders", AllOrders);
        }

        var orders = page.Value.Items;
        if (orders.Count == 0)
        {
            return Answer("You haven't placed any orders yet.", [], null, PlaceOrder);
        }

        var table = new QuickTable(
            ["Order", "Product", "Quantity", "Status", "Placed"],
            [.. orders.Select(o => (IReadOnlyList<string>)[QuickText.ShortId(o.Id), o.ProductName ?? o.Sku, QuickText.Count(o.Quantity), o.Status.ToString(), QuickText.Date(o.PlacedAt)])]);

        var text = orders.Count == 1 ? "Your most recent order:" : $"Your {QuickText.Count(orders.Count)} most recent orders:";
        return Answer(text, [], table, AllOrders);
    }

    private async Task<QuickReply> ExportStatusAsync(CancellationToken ct)
    {
        var exports = await queries.SendAsync(new GetOrderExports(), ct).ConfigureAwait(false);
        if (Failed(exports))
        {
            return CouldNotLoad("Your exports", Exports);
        }

        if (exports.Value.Count == 0)
        {
            return Answer("You haven't requested an export yet.", [], null, Exports);
        }

        var latest = exports.Value.MaxBy(e => e.RequestedAt) ?? exports.Value[0];
        var text = latest.Status switch
        {
            OrderExportState.Ready => $"Your latest export is ready: {QuickText.Count(latest.RowCount ?? 0)} orders, finished on {QuickText.Date(latest.CompletedAt ?? latest.RequestedAt)}.",
            OrderExportState.Failed => "Your latest export failed. Request a new one from Exports.",
            _ => $"Your latest export is still being built. It was requested on {QuickText.Date(latest.RequestedAt)}, and you'll get a notification when it's ready.",
        };

        var facts = new List<QuickFact> { new("Requested", QuickText.Date(latest.RequestedAt)) };
        if (latest.CompletedAt is { } completed)
        {
            facts.Add(new("Finished", QuickText.Date(completed)));
        }

        if (latest.RowCount is { } rows)
        {
            facts.Add(new("Orders", QuickText.Count(rows)));
        }

        return Answer(text, facts, null, Exports);
    }

    private async Task<QuickReply> ProductAsync(string question, CancellationToken ct)
    {
        var found = await queries.SendAsync(new FindProducts(question, ProductCandidates), ct).ConfigureAwait(false);
        if (Failed(found))
        {
            return CouldNotLoad("The catalogue", Catalogue);
        }

        var matches = found.Value;
        if (matches.Count == 0)
        {
            return Answer("I couldn't find that product in the catalogue. Try its exact name or SKU.", [], null, Catalogue);
        }

        var best = matches[0];
        var exact = best.Score >= 0.999;
        if (!exact && matches.Count > 1 && best.Score - matches[1].Score < ProductLead)
        {
            return new QuickSuggestions(
                "Which product did you mean?",
                [.. matches.Take(IntentRouter.MaxSuggestions).Select(m => $"How much is {m.Name}?")]);
        }

        var name = exact ? best.Name : $"{best.Name}, the closest match,";
        return Answer(
            $"{name} costs {QuickText.Money(best.Price)}.",
            [new("Name", best.Name), new("SKU", best.Sku), new("Price", QuickText.Money(best.Price))],
            null,
            new QuickLink("Open product", $"/products/{best.Id}"));
    }

    private async Task<QuickReply> PopulationAsync(string question, CancellationToken ct)
    {
        var resolved = await parameters.AreaAsync(question, ct).ConfigureAwait(false);
        if (Failed(resolved))
        {
            return CouldNotLoad("Population figures", PopulationPage);
        }

        if (resolved.Value.Area is not { } area)
        {
            return resolved.Value.Candidates.Count > 1
                ? new QuickSuggestions("Which area did you mean?", [.. resolved.Value.Candidates.Select(c => $"What is the population of {c.Name}?")])
                : Answer("Which area? Ask, for example, \"What is the population of Copenhagen?\"", [], null, PopulationPage);
        }

        var figures = await queries.SendAsync(new GetPopulation(area.Code), ct).ConfigureAwait(false);
        if (Failed(figures))
        {
            return CouldNotLoad("Population figures", PopulationPage);
        }

        var p = figures.Value;
        return Answer(
            $"{p.AreaName} had {QuickText.Count(p.Population)} inhabitants in {p.Period}.",
            [new("Area", p.AreaName), new("Period", p.Period), new("Population", QuickText.Count(p.Population)), new("Source", p.Source)],
            null,
            PopulationPage);
    }

    private async Task<QuickReply> JobHealthAsync(string question, CancellationToken ct)
    {
        var window = ParameterResolver.Window(question, TimeSpan.FromHours(24));
        var health = await queries.SendAsync(new GetJobHealth(window), ct).ConfigureAwait(false);
        if (Failed(health))
        {
            return CouldNotLoad("Job health", JobRuns);
        }

        var h = health.Value;
        var span = QuickText.Window(window);
        var text = h.Failed + h.DeadLettered == 0
            ? $"No background job failed in {span}."
            : $"In {span}, {QuickText.Count(h.Failed)} job runs failed and {QuickText.Count(h.DeadLettered)} were dead-lettered.";

        return Answer(
            text,
            [new("Running", QuickText.Count(h.Running)), new("Succeeded", QuickText.Count(h.Succeeded)), new("Failed", QuickText.Count(h.Failed)), new("Dead-lettered", QuickText.Count(h.DeadLettered))],
            null,
            JobRuns);
    }

    private async Task<QuickReply> FailedJobsAsync(CancellationToken ct)
    {
        var runs = await queries.SendAsync(new GetJobRuns(JobRunStatus.Failed, null, 1, TableRows), ct).ConfigureAwait(false);
        if (Failed(runs))
        {
            return CouldNotLoad("Job runs", JobRuns);
        }

        var page = runs.Value;
        if (page.Items.Count == 0)
        {
            return Answer("No job runs have failed.", [], null, JobRuns);
        }

        var table = new QuickTable(
            ["Job", "Attempt", "Started"],
            [.. page.Items.Select(r => (IReadOnlyList<string>)[r.JobName, QuickText.Count(r.Attempt), QuickText.Date(r.StartedAt)])]);
        var text = page.Items.Count == 1
            ? $"The most recent failed job run, of {QuickText.Count(page.TotalCount)}:"
            : $"The {QuickText.Count(page.Items.Count)} most recent failed job runs, of {QuickText.Count(page.TotalCount)}:";

        return Answer(text, [], table, JobRuns);
    }

    private async Task<QuickReply> DeadLettersAsync(CancellationToken ct)
    {
        var letters = await queries.SendAsync(new GetDeadLetters(1, TableRows), ct).ConfigureAwait(false);
        if (Failed(letters))
        {
            return CouldNotLoad("Dead letters", JobRuns);
        }

        var page = letters.Value;
        if (page.Items.Count == 0)
        {
            return Answer("Nothing is dead-lettered.", [], null, JobRuns);
        }

        var table = new QuickTable(
            ["Message", "Exception", "Sent", "Replayable"],
            [.. page.Items.Select(d => (IReadOnlyList<string>)[d.MessageType, d.ExceptionType, QuickText.Date(d.SentAt), d.Replayable ? "Yes" : "No"])]);

        return Answer($"{QuickText.Count(page.TotalCount)} dead-lettered messages. The most recent:", [], table, JobRuns);
    }

    private async Task<QuickReply> TrafficAsync(string question, CancellationToken ct)
    {
        var window = ParameterResolver.Window(question, TimeSpan.FromHours(1));
        var summary = await queries.SendAsync(new GetTrafficSummary(window), ct).ConfigureAwait(false);
        if (Failed(summary))
        {
            return CouldNotLoad("Traffic", TrafficPage);
        }

        var t = summary.Value;
        return Answer(
            $"In {QuickText.Window(window)} the API served {QuickText.Rate(t.RequestsPerMinute)} requests a minute, with an error rate of {QuickText.Percent(t.ErrorRate)}.",
            [
                new("Requests", QuickText.Count(t.Overall.Total)),
                new("Requests a minute", QuickText.Rate(t.RequestsPerMinute)),
                new("Error rate", QuickText.Percent(t.ErrorRate)),
                new("p95 latency", QuickText.Milliseconds(t.Overall.P95Ms)),
            ],
            null,
            TrafficPage);
    }

    private async Task<QuickReply> IntegrationsAsync(CancellationToken ct)
    {
        var status = await queries.SendAsync(new GetExternalSystemStatus(), ct).ConfigureAwait(false);
        if (Failed(status))
        {
            return CouldNotLoad("External system status", IntegrationsPage);
        }

        var systems = status.Value.Systems;
        if (systems.Count == 0)
        {
            return Answer("No external systems are configured.", [], null, IntegrationsPage);
        }

        var attention = systems.Count(s => s.Stale || s.State is not ExternalSystemState.Healthy);
        var text = attention == 0
            ? $"All {QuickText.Count(systems.Count)} external systems are healthy."
            : $"{QuickText.Count(attention)} of {QuickText.Count(systems.Count)} external systems need attention.";
        var table = new QuickTable(
            ["System", "State", "Checked"],
            [.. systems.Select(s => (IReadOnlyList<string>)[
                s.Name,
                s.Stale ? "Stale" : s.State?.ToString() ?? "Not checked",
                s.CheckedAt is { } at ? QuickText.Date(at) : "Never"])]);

        return Answer(text, [], table, IntegrationsPage);
    }

    private async Task<QuickReply> SignInsAsync(string question, CancellationToken ct)
    {
        var window = ParameterResolver.Window(question, TimeSpan.FromHours(24));
        var health = await queries.SendAsync(new GetSignInHealth(window, ActiveWindow), ct).ConfigureAwait(false);
        if (Failed(health))
        {
            return CouldNotLoad("Sign-in health", SignInsPage);
        }

        // Counts only: LockedOutUsers names accounts, and an answer never does.
        var s = health.Value;
        return Answer(
            $"In {QuickText.Window(window)}: {QuickText.Count(s.Succeeded)} successful sign-ins, {QuickText.Count(s.BadCredentials + s.UnknownUser)} failed, {QuickText.Count(s.LockedOut)} refused by a lockout. {QuickText.Count(s.LockedOutUsers.Count)} accounts are locked out now.",
            [
                new("Successful", QuickText.Count(s.Succeeded)),
                new("Failed", QuickText.Count(s.BadCredentials + s.UnknownUser)),
                new("Locked out now", QuickText.Count(s.LockedOutUsers.Count)),
                new("Active now", QuickText.Count(s.Active.Count)),
            ],
            null,
            SignInsPage);
    }
}
