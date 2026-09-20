using System.Data.Common;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Security;

/// <summary>
/// Makes the stored administrator roles match <see cref="AdminOptions"/> at startup, before the
/// host begins serving. See ADR 0020.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both API replicas run this.</b> That is safe rather than merely tolerable: the reconcile is
/// idempotent, and <c>User.ChangeRole</c> reports whether it actually moved, so a run against an
/// unchanged list dirties no entity and issues no UPDATE at all. Two replicas starting together
/// compute the same target state and write the same values.
/// </para>
/// <para>
/// <b>It never fails the host.</b> An API that refuses to start because it could not confirm the
/// administrator list turns a configuration problem into an outage — and this runs before
/// migrations have necessarily been applied, which is the normal case in <c>ApiFactory</c>, where
/// the test host is built before <c>MigrateAsync</c> runs.
/// </para>
/// <para>
/// <b>It lives in Infrastructure rather than beside the policy it feeds, for one concrete
/// reason:</b> surviving an absent database means catching what an absent database throws, and
/// that is two types — <see cref="DbException"/> for a refusal the provider reports directly, and
/// <see cref="RetryLimitExceededException"/> once <c>EnableRetryOnFailure</c> has exhausted its
/// attempts on a transient one. The second is an EF type, and <c>src/Api</c> contains no EF
/// reference at all; putting this here keeps it that way. Found by <c>HealthTests</c> going red —
/// it stands up a host with a placeholder connection string precisely to prove /health needs no
/// database, and it is the canary for exactly this class of change, as its own comment about the
/// Wolverine spike records.
/// </para>
/// </remarks>
internal sealed partial class AdminReconciler(
    IServiceScopeFactory scopes,
    IOptions<AdminOptions> options,
    ILogger<AdminReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The commands that boot this application without a database set this false. Checked
        // before anything resolves a scope, so those hosts never reach Postgres at all.
        if (!options.Value.ReconcileOnStart)
        {
            AdminLog.Skipped(logger);
            return;
        }

        var usernames = options.Value.Usernames;

        // Two narrow catches below rather than one blanket one, which CA1031 makes an error
        // anyway. They are the two shapes "the database is not ready" actually arrives in: a
        // missing table or a refused connection surfaces from the provider, and a TRANSIENT
        // failure surfaces wrapped by EF's execution strategy instead, because AddInfrastructure
        // turns retrying on and the strategy reports its own exhaustion rather than the inner
        // fault. Anything else is a programming error and must still surface.
        await using var scope = scopes.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        try
        {
            var result = await commands
                .SendAsync(new ReconcileAdministrators([.. usernames]), cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                AdminLog.Failed(logger, result.Error.Code, result.Error.Message);
                return;
            }

            Report(result.Value);
        }
        catch (DbException exception)
        {
            AdminLog.Unavailable(logger, exception.Message);
        }
        catch (RetryLimitExceededException exception)
        {
            AdminLog.Unavailable(logger, exception.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Report(AdministratorReconciliation reconciliation)
    {
        // Information, not Debug: a role changing is rare and consequential, unlike the routine
        // dispatch outcomes Behaviors.LoggedAsync keeps at Debug. A no-op reconcile says nothing.
        if (reconciliation.Promoted > 0 || reconciliation.Demoted > 0)
        {
            AdminLog.Reconciled(logger, reconciliation.Promoted, reconciliation.Demoted);
        }

        // The likeliest mistake in this whole feature: a typo in the configured list leaves
        // nobody with access, and every symptom of that is a 403 somewhere else entirely.
        if (reconciliation.Unknown.Count > 0)
        {
            AdminLog.Unknown(logger, string.Join(", ", reconciliation.Unknown));
        }
    }
}

/// <summary>
/// [LoggerMessage] delegates rather than direct ILogger calls, for the CA1848 reason the outbox
/// and the messaging behaviors already follow.
/// </summary>
internal static partial class AdminLog
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Administrator roles reconciled: {Promoted} promoted, {Demoted} demoted.")]
    public static partial void Reconciled(ILogger logger, int promoted, int demoted);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Admin:Usernames names accounts that do not exist: {Unknown}. "
            + "They hold no access until they register.")]
    public static partial void Unknown(ILogger logger, string unknown);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "Administrator reconcile could not reach the database ({Reason}). "
            + "Roles are unchanged; the next start will reconcile them.")]
    public static partial void Unavailable(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "Administrator reconcile failed: {Code} {Reason}.")]
    public static partial void Failed(ILogger logger, string code, string reason);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Debug,
        Message = "Administrator reconcile skipped: Admin:ReconcileOnStart is false.")]
    public static partial void Skipped(ILogger logger);
}
