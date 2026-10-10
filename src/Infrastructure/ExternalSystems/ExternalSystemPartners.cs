namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// Every partner's client, one registration chain each: the one place the <c>/external-system</c>
/// command adds to. <see cref="ExternalSystemsRegistration.AddExternalSystems"/> calls it, so both
/// hosts get every partner (ADR 0031) and neither host's Program.cs names a partner's types, which
/// stay internal to this assembly.
/// </summary>
/// <remarks>
/// A partner is registered whether or not it is configured: an environment without its
/// <c>ExternalSystems:Systems:&lt;Name&gt;</c> section fails that partner's calls fast as
/// unavailable instead of refusing to start.
/// </remarks>
internal static class ExternalSystemPartners
{
    public static void Add(ExternalSystemsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // One chain per partner, in name order: AddClient of the partner's Refit interface under
        // its configuration name, then WithAdapter of the Application port and its adapter, and
        // WithoutRetry(reason) when its writes take no idempotency key. None yet.
    }
}
