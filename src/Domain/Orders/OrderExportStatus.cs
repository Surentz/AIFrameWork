namespace AiFramework.Domain.Orders;

/// <summary>
/// Where an order export is. Persisted by name, like <see cref="OrderStatus"/>: the names are a
/// stored contract.
///
/// There is deliberately no Failed. A failing build job throws, the worker's retry policy retries
/// it and then dead-letters it, and nothing here could record that without catching every
/// exception. Instead an export still <see cref="Requested"/> after
/// <see cref="OrderExport.StaleAfter"/> READS as failed — see <see cref="OrderExport.IsFailed"/>.
/// </summary>
public enum OrderExportStatus
{
    Requested,
    Ready,
}
