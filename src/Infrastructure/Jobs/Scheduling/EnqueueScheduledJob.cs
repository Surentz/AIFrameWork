using AiFramework.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace AiFramework.Infrastructure.Jobs.Scheduling;

/// <summary>
/// <b>The only Quartz job this codebase has, and should ever have.</b> Quartz decides when; this
/// hands the work to Wolverine, which runs it on its lane with its retry policy like any other job.
/// ADR 0017.
/// </summary>
/// <remarks>
/// <para>
/// Creates its own scope rather than depending on how Quartz resolves jobs: <see cref="IJobScheduler"/>
/// is scoped, and a scoped dependency resolved from a root provider is exactly the bug scope
/// validation exists to catch. (Task 1's own finding: Quartz 4.1 resolves a job instance from a DI
/// child scope it creates and disposes per execution — so this scope is not strictly needed to avoid
/// a captive dependency, but the code must not rely on that resolution detail either. Depending on it
/// would mean a Quartz internal implementation choice becomes load-bearing for this codebase's own
/// scoping discipline; creating the scope explicitly keeps that discipline true regardless of how
/// Quartz resolves <see cref="Quartz.IJob"/> itself.)
/// </para>
/// <para>
/// No catch: an exception propagates to Quartz, which logs it against the trigger. A job that
/// could not even be ENQUEUED is a scheduling fault, not a job failure — Wolverine's retry policy
/// has nothing to retry yet.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class EnqueueScheduledJob(IServiceScopeFactory scopes) : Quartz.IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var name = context.MergedJobDataMap.GetString(JobSchedules.JobNameKey);
        var job = name is null ? null : JobSchedules.Find(name);

        if (job?.EnqueueNew is not { } enqueue)
        {
            // The synchronizer deletes stale entries at startup, so this means a job was removed
            // from the code while a trigger for it was already firing on another node.
            throw new InvalidOperationException(
                $"Quartz fired '{name}', which is not a scheduled job in this build.");
        }

        await using var scope = scopes.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        await enqueue(jobs, cancellationToken).ConfigureAwait(false);
    }
}
