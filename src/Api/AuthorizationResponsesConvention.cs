using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace AiFramework.Api;

/// <summary>
/// Declares in the OpenAPI document the statuses authorization adds to an action: 401 wherever a
/// session is required, and 403 wherever a capability policy is (ADR 0024). Neither is something a
/// controller returns, so a per-action <c>[ProducesResponseType]</c> was easy to forget and mostly
/// was; here it follows from the <c>[Authorize]</c> attribute itself. StatusCodeProblems writes the
/// ProblemDetails both statuses carry.
/// </summary>
internal sealed class AuthorizationResponsesConvention : IActionModelConvention
{
    public void Apply(ActionModel action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var attributes = action.Controller.Attributes.Concat(action.Attributes).ToList();
        if (attributes.OfType<IAllowAnonymous>().Any())
        {
            return;
        }

        var authorize = attributes.OfType<IAuthorizeData>().ToList();
        if (authorize.Count == 0)
        {
            return;
        }

        action.Filters.Add(new ProducesResponseTypeAttribute<ProblemDetails>(StatusCodes.Status401Unauthorized));

        // Policies only, because they are the only source of a 403 here: Roles would be another,
        // and is banned in favour of capability policies (ADR 0024, src/Api/CLAUDE.md).
        if (authorize.Any(data => !string.IsNullOrEmpty(data.Policy)))
        {
            action.Filters.Add(new ProducesResponseTypeAttribute<ProblemDetails>(StatusCodes.Status403Forbidden));
        }
    }
}
