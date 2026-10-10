using AiFramework.Infrastructure.ExternalSystems;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class ExternalSystemsOptionsValidatorTests
{
    private static ExternalSystemsOptions With(Action<ExternalSystemOptions> configure)
    {
        var system = new ExternalSystemOptions { BaseAddress = "https://partner.example/api/" };
        configure(system);
        var options = new ExternalSystemsOptions();
        options.Systems["Partner"] = system;
        return options;
    }

    private static string Failures(ExternalSystemsOptions options) =>
        string.Join(" | ", new ExternalSystemsOptionsValidator().Validate(null, options).Failures ?? []);

    [Fact]
    public void Validate_AMinimalSystem_Succeeds()
    {
        var result = new ExternalSystemsOptionsValidator().Validate(null, With(_ => { }));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_NoSystems_Succeeds()
    {
        var result = new ExternalSystemsOptionsValidator().Validate(null, new ExternalSystemsOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("ftp://partner.example/")]
    public void Validate_ABaseAddressThatIsNotAbsoluteHttp_Fails(string baseAddress)
    {
        var failures = Failures(With(s => s.BaseAddress = baseAddress));

        failures.Should().Contain("ExternalSystems:Systems:Partner:BaseAddress");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("colon:name")]
    [InlineData("")]
    public void Validate_ASystemNameOutsideLettersDigitsAndDashes_Fails(string name)
    {
        var options = new ExternalSystemsOptions();
        options.Systems[name] = new ExternalSystemOptions { BaseAddress = "https://partner.example/" };

        var failures = Failures(options);

        failures.Should().Contain("name");
    }

    [Theory]
    [InlineData("external")]
    [InlineData("External")]
    [InlineData("EXTERNAL")]
    public void Validate_ASystemNamedLikeTheExternalTag_Fails(string name)
    {
        var options = new ExternalSystemsOptions();
        options.Systems[name] = new ExternalSystemOptions { BaseAddress = "https://partner.example/" };

        var failures = Failures(options);

        failures.Should().Contain("must not be \"external\"");
    }

    [Fact]
    public void Validate_AnAttemptTimeoutLongerThanTheTotal_Fails()
    {
        var failures = Failures(With(s =>
        {
            s.Resilience.TotalRequestTimeout = TimeSpan.FromSeconds(5);
            s.Resilience.AttemptTimeout = TimeSpan.FromSeconds(6);
        }));

        failures.Should().Contain("AttemptTimeout");
    }

    [Fact]
    public void Validate_ZeroRetryAttempts_FailsAndPointsAtWithoutRetry()
    {
        var failures = Failures(With(s => s.Resilience.MaxRetryAttempts = 0));

        failures.Should().Contain("WithoutRetry");
    }

    [Fact]
    public void Validate_ClientSecretWithoutASecretFile_Fails()
    {
        var failures = Failures(With(s =>
        {
            s.Auth.Kind = ExternalSystemAuthKind.ClientSecret;
            s.Auth.TokenEndpoint = "https://idp.example/token";
            s.Auth.ClientId = "client";
        }));

        failures.Should().Contain("ClientSecretFile");
    }

    [Fact]
    public void Validate_PrivateKeyJwtWithoutACertificate_Fails()
    {
        var failures = Failures(With(s =>
        {
            s.Auth.Kind = ExternalSystemAuthKind.PrivateKeyJwt;
            s.Auth.TokenEndpoint = "https://idp.example/token";
            s.Auth.Issuer = "https://idp.example/";
            s.Auth.ClientId = "client";
        }));

        failures.Should().Contain("ClientCertificate");
    }

    [Fact]
    public void Validate_PrivateKeyJwtWithoutAnIssuer_Fails()
    {
        var failures = Failures(With(s =>
        {
            s.Auth.Kind = ExternalSystemAuthKind.PrivateKeyJwt;
            s.Auth.TokenEndpoint = "https://idp.example/token";
            s.Auth.ClientId = "client";
            s.ClientCertificate = new ClientCertificateOptions { Path = "client.pfx" };
        }));

        failures.Should().Contain("Issuer");
    }

    [Fact]
    public void Validate_AuthWithoutAClientId_Fails()
    {
        var failures = Failures(With(s =>
        {
            s.Auth.Kind = ExternalSystemAuthKind.ClientSecret;
            s.Auth.TokenEndpoint = "https://idp.example/token";
            s.Auth.ClientSecretFile = "secret";
        }));

        failures.Should().Contain("ClientId");
    }

    [Fact]
    public void Validate_AnEmptyServerTrustPath_Fails()
    {
        var failures = Failures(With(s => s.ServerTrust = new ServerTrustOptions { CaBundlePath = "" }));

        failures.Should().Contain("CaBundlePath");
    }

    [Fact]
    public void Bind_FromConfiguration_ReadsNestedOptionsAndLeavesAbsentOnesNull()
    {
        var section = ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:Sim:BaseAddress"] = "https://localhost:55690/",
            ["Systems:Sim:Auth:Kind"] = "ClientSecret",
            ["Systems:Sim:Resilience:MaxRetryAttempts"] = "4",
        });

        var options = section.Get<ExternalSystemsOptions>()!; // Section() always has a Systems child here.

        options.Find("sim").Should().NotBeNull("system names are case-insensitive, like every config key");
        options.Find("Sim")!.Auth.Kind.Should().Be(ExternalSystemAuthKind.ClientSecret);
        options.Find("Sim")!.Resilience.MaxRetryAttempts.Should().Be(4);
        options.Find("Sim")!.ClientCertificate.Should().BeNull();
    }
}
