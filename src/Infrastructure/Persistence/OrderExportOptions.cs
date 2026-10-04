using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Persistence;

/// <summary>
/// The <c>OrderExports</c> configuration section. Bound by the worker, which runs the sweep.
/// </summary>
public sealed class OrderExportOptions
{
    public const string SectionName = "OrderExports";

    /// <summary>
    /// Seven days, per ADR 0029. An export is a copy of someone's whole order history, so this is
    /// how long this application keeps that copy — long enough to come back for it after a weekend,
    /// short enough that copies do not pile up. Lengthening it is a decision, not a tuning knob.
    /// </summary>
    public int RetentionDays { get; set; } = 7;
}

/// <summary>
/// Refuses a retention below one day. Zero would put the sweep's cutoff at "now" and delete every
/// export, the ones still being built included, and a negative value every export ever; a typo in
/// <c>OrderExports__RetentionDays</c> should stop the worker, not empty the table.
/// </summary>
public sealed class OrderExportOptionsValidator : IValidateOptions<OrderExportOptions>
{
    public ValidateOptionsResult Validate(string? name, OrderExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.RetentionDays >= 1
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"OrderExports:RetentionDays must be at least 1, but is {options.RetentionDays}.");
    }
}
