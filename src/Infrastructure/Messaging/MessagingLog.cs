using Microsoft.Extensions.Logging;

namespace AiFramework.Infrastructure.Messaging;

/// <summary>
/// The [LoggerMessage] partials for Behaviors.LoggedAsync. A separate file from Behaviors.cs,
/// not more members on it, because the source generator requires the containing type to be
/// partial and Behaviors already hosts four unrelated methods — mirrors CacheScope being its own
/// file beside CachedAsync/EvictAsync for the same reason.
/// </summary>
internal static partial class MessagingLog
{
    /// <summary>
    /// Every dispatch that returns without throwing, success or failure alike, logs exactly one
    /// of these two. Debug for a success: every query goes through this, and at a level a request
    /// as ordinary as a page load would already be logging one record per dispatch — the store
    /// fills with "it worked" and failures, the thing worth keeping at default level, get lost in
    /// it. See docs/superpowers/plans/2026-09-13-centralized-logging.md, Task 2.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "{Kind} {Name} succeeded in {ElapsedMs}ms")]
    internal static partial void Succeeded(ILogger logger, string kind, string name, long elapsedMs);

    /// <summary>
    /// Level is not fixed on the attribute — omitting it here is what makes the generator emit an
    /// extra LogLevel parameter instead of three near-identical methods, one per level a failed
    /// Result can map to. See Behaviors.LevelFor: Validation/NotFound is the caller being wrong,
    /// not the system, and stays at Debug; Conflict/Unauthorized is Information; anything else is
    /// Warning, because a 404 that pages someone is a broken logging system.
    /// </summary>
    [LoggerMessage(Message = "{Kind} {Name} failed with {ErrorCode} in {ElapsedMs}ms")]
    internal static partial void Failed(
        ILogger logger, LogLevel level, string kind, string name, string errorCode, long elapsedMs);

    /// <summary>
    /// The handler threw. Always Warning — GlobalExceptionHandler already logs the exception
    /// itself at Error once it reaches the boundary, so this does not carry the exception object
    /// (that would double-report the same failure) and exists only to record which dispatch was
    /// in flight when it happened, and for how long.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Kind} {Name} threw after {ElapsedMs}ms")]
    internal static partial void Faulted(ILogger logger, string kind, string name, long elapsedMs);
}
