using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Monitoring;

public static class MonitoringRegistration
{
    /// <summary>
    /// The monitoring page's read side, its retention sweeps, and traffic recording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own method rather than more lines inside <c>AddJobs</c>, where these first accreted:
    /// the sign-in reader and the traffic recorder are not job concerns, and a registration method
    /// that grows past what one screen holds stops being a list anyone reads.
    /// </para>
    /// <para>
    /// <b>Called by every host, including the worker.</b> Without the flush service there, every
    /// command and query a JOB dispatches would be invisible on the traffic page — each host
    /// records under its own instance id and the page sums them (ADR 0021).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMonitoring(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Singleton, necessarily: it accumulates across requests, and a scoped one would count
        // nothing. Registered as itself as well, because the flush service needs the concrete
        // type's TakeClosedBuckets, which is not part of the port.
        services.AddSingleton<TrafficRecorder>();
        services.AddSingleton<ITrafficRecorder>(sp => sp.GetRequiredService<TrafficRecorder>());
        services.AddHostedService<TrafficFlushService>();

        services.AddScoped<IJobRunReader, JobRunReader>();
        services.AddScoped<IDeadLetterStore, DeadLetterStore>();
        services.AddScoped<ITriggerableJobs, TriggerableJobs>();
        services.AddScoped<ISignInEventReader, SignInEventReader>();
        services.AddScoped<ITrafficReader, TrafficReader>();
        services.AddScoped<IAdminActionReader, AdminActionReader>();

        services.AddScoped<ExternalSystemStatusStore>();
        services.AddScoped<IExternalSystemStatusReader>(sp => sp.GetRequiredService<ExternalSystemStatusStore>());
        services.AddScoped<IExternalSystemStatusStore>(sp => sp.GetRequiredService<ExternalSystemStatusStore>());

        services.AddScoped<IJobRunRetention, JobRunRetention>();
        services.AddScoped<ISignInEventRetention, SignInEventRetention>();
        services.AddScoped<ITrafficRetention, TrafficRetention>();
        services.AddScoped<IAdminActionRetention, AdminActionRetention>();

        return services;
    }
}
