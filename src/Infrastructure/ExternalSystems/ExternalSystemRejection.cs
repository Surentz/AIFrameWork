using System.Net;

namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// A partner's non-success answer, as an adapter's <c>expected</c> callback sees it: the status,
/// and the raw body when there was one, because some partners say what went wrong only there
/// (Statistics Denmark answers every error with a 400 and an <c>errorTypeCode</c>). The body is the
/// partner's text: parse it to choose an error, but never copy it into an
/// <see cref="Application.Abstractions.Error"/> message, which reaches the browser, or into a log.
/// </summary>
public sealed record ExternalSystemRejection(HttpStatusCode Status, string? Content);
