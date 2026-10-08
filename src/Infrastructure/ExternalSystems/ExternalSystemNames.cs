namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// The one place a system name is turned into a client or check name. Composing these anywhere
/// else lets the two sides drift with no error — the CacheScope lesson.
/// </summary>
internal static class ExternalSystemNames
{
    public static string Probe(string system) => $"external:{system}:probe";

    public static string TokenBackchannel(string system) => $"external:{system}:token-endpoint";

    public static string ProbeCheck(string system) => system;

    public static string CertificateCheck(string system) => $"{system}:certificate";

    public static string TokenCheck(string system) => $"{system}:token";
}
