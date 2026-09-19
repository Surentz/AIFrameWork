using AiFramework.Application.Abstractions;
using AiFramework.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Notifications;

[ApiController]
[Route("api/notifications")]
[Authorize]
// S6960: the rule sees the two read actions and the two write actions as disjoint groups and
// proposes splitting them into separate controllers. Declined: api/notifications is ONE REST
// resource, and its reads and writes belong together the same way OrdersController's and
// ProductsController's do — both of which have the identical command/query split and happen to
// sit just under whatever threshold trips this. Splitting would fragment one resource across two
// files and make this the only controller in the solution organised by verb rather than by
// resource, which is a worse codebase for a cleaner metric.
#pragma warning disable S6960
public sealed class NotificationsController(
    ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
#pragma warning restore S6960
{
    /// <summary>Lists the caller's notifications, newest first, one page at a time.</summary>
    [HttpGet]
    [ProducesResponseType<NotificationPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> List(
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        [FromQuery] bool unreadOnly = false,
        CancellationToken cancellationToken = default)
    {
        var result = await queries.SendAsync(
            new GetNotifications(limit, cursor, unreadOnly), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new NotificationPageResponse
            {
                Items = [.. result.Value.Items.Select(NotificationMappings.ToResponse)],
                NextCursor = result.Value.NextCursor,
            })
            : result.Problem(HttpContext);
    }

    /// <summary>The caller's unread count, for a badge.</summary>
    [HttpGet("unread-count")]
    [ProducesResponseType<UnreadCountResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult> UnreadCount(CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(
            new GetUnreadNotificationCount(), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new UnreadCountResponse { UnreadCount = result.Value })
            : result.Problem(HttpContext);
    }

    /// <summary>
    /// Marks one notification read. Idempotent — marking an already-read one answers 200 with a
    /// MarkedCount of zero, not an error.
    /// </summary>
    [HttpPost("{id:guid}/read")]
    [ProducesResponseType<NotificationReadResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        var result = await commands.SendAsync(
            new MarkNotificationRead(id), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(result.Value.ToResponse())
            : result.Problem(HttpContext);
    }

    /// <summary>Marks every unread notification read.</summary>
    [HttpPost("read-all")]
    [ProducesResponseType<NotificationReadResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        var result = await commands.SendAsync(
            new MarkAllNotificationsRead(), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(result.Value.ToResponse())
            : result.Problem(HttpContext);
    }
}
