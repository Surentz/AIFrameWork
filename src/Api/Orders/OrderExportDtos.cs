using AiFramework.Application.Orders;

namespace AiFramework.Api.Orders;

public sealed record OrderExportResponse
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Serialized as its name. Failed is never stored: an export still Requested after 45 minutes
    /// reads as Failed, because its build job has given up (ADR 0029).
    /// </summary>
    public required OrderExportState Status { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>Null until Ready.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>How many orders the file holds. Null until Ready.</summary>
    public int? RowCount { get; init; }

    public static OrderExportResponse From(OrderExportView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new OrderExportResponse
        {
            Id = view.Id,
            Status = view.Status,
            RequestedAt = view.RequestedAt,
            CompletedAt = view.CompletedAt,
            RowCount = view.RowCount,
        };
    }
}
