using System.Text;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Orders;

/// <summary>
/// Exports of the caller's own orders. Any signed-in user, no capability policy: every read here is
/// the caller's own data (ADR 0007). ADR 0029.
/// </summary>
/// <remarks>
/// <b>Nothing here may be stored by a cache</b>, the browser's included: the list must show Ready the
/// moment the notification says so, and the file is a copy of someone's order history.
/// </remarks>
[ApiController]
[Route("api/orders/exports")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
// S6960: declined for the reason NotificationsController gives — api/orders/exports is ONE REST
// resource, and splitting its request from its reads would organise it by verb rather than by
// resource, unlike every other controller here.
#pragma warning disable S6960
public sealed class OrderExportsController(ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
#pragma warning restore S6960
{
    /// <summary>
    /// Asks for a CSV of every order the caller has placed. Built in the background; the caller is
    /// notified when it is ready. While one is still being built, returns that one rather than
    /// starting another.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<OrderExportResponse>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult> RequestExport(CancellationToken cancellationToken)
    {
        var result = await commands.SendAsync(new RequestOrderExport(), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Accepted(new Uri("/api/orders/exports", UriKind.Relative), OrderExportResponse.From(result.Value))
            : result.Problem(HttpContext);
    }

    /// <summary>The caller's exports, newest first. Kept for seven days.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<OrderExportResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult> List(CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(new GetOrderExports(), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(result.Value.Select(OrderExportResponse.From).ToArray())
            : result.Problem(HttpContext);
    }

    /// <summary>
    /// The CSV of a Ready export. 404 if it is someone else's or not built yet — the same answer for
    /// both, so an export id reveals nothing.
    /// </summary>
    [HttpGet("{id:guid}/download")]
    [ProducesResponseType(typeof(Stream), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(new GetOrderExportFile(id), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Problem(HttpContext);
        }

        // Still a CSV until the build job renders a PDF: the byte-order mark keeps Excel reading UTF-8.
        byte[] bytes = [.. Encoding.UTF8.Preamble, .. result.Value.Document];
        return File(bytes, "text/csv; charset=utf-8", result.Value.FileName);
    }
}
