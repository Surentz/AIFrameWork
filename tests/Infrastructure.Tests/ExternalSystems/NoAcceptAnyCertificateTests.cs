using System.Runtime.CompilerServices;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

/// <summary>
/// Server trust in src/ is CertificateChainPolicy, never a callback: a callback is the only way
/// to write "accept anything", so having none makes that impossible rather than discouraged.
/// </summary>
public sealed class NoAcceptAnyCertificateTests
{
    private static readonly string[] Forbidden =
    [
        "ServerCertificateCustomValidationCallback",
        "RemoteCertificateValidationCallback",
        "DangerousAcceptAnyServerCertificateValidator",
    ];

    [Fact]
    public void Source_UnderSrc_HasNoCertificateValidationCallback()
    {
        var offenders = SourceFiles()
            .SelectMany(file => Forbidden
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(RepoRoot(), file)}: {token}"))
            .ToArray();

        string.Join(Environment.NewLine, offenders).Should().BeEmpty();
    }

    [Fact]
    public void Scan_FindsTheSourceTree()
    {
        SourceFiles().Should().Contain(f => f.EndsWith("FileCertificateProvider.cs", StringComparison.Ordinal),
            "an empty scan would make the rule above pass for the wrong reason");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..")); // CallerFilePath is never empty when the compiler fills it.
}
