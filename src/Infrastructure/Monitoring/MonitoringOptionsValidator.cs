using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// Only the one value that can make the page lie. A period above three minutes makes every status
/// row read as stale (the page's threshold), and below a second the checks would hammer partners.
/// </summary>
internal sealed class MonitoringOptionsValidator : IValidateOptions<MonitoringOptions>
{
    public static readonly TimeSpan MinStatusPeriod = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaxStatusPeriod = TimeSpan.FromMinutes(1);

    public ValidateOptionsResult Validate(string? name, MonitoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.ExternalSystemStatusPeriod < MinStatusPeriod || options.ExternalSystemStatusPeriod > MaxStatusPeriod
            ? ValidateOptionsResult.Fail(
                "Monitoring:ExternalSystemStatusPeriod must be between 1 second and 1 minute: " +
                "the monitoring page calls a status row stale after 3 minutes.")
            : ValidateOptionsResult.Success;
    }
}
