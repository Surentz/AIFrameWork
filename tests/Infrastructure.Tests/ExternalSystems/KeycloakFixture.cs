using System.Text;
using AiFramework.PartnerSimulator;
using Testcontainers.Keycloak;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

/// <summary>
/// One Keycloak per test run (tests/CLAUDE.md: one container per collection). The realm is
/// generated at start-up because the private_key_jwt client must carry a certificate that only
/// exists once this run's TestPki does — nothing here is ever committed.
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "aiframework";
    public const string SecretClientId = "secret-client";
    public const string SecretClientSecret = "test-only-secret";
    public const string JwtClientId = "jwt-client";

    private readonly KeycloakContainer _container;
    private readonly byte[] _jwtClientPfx;

    public KeycloakFixture()
    {
        Pki = TestPki.Create("AiFramework Keycloak Test");
        var jwtClient = Pki.IssueClient(JwtClientId);
        _jwtClientPfx = Pki.ExportPfx(jwtClient, password: null);

        // The parameterless builder is obsolete (CS0618) and defaults to Keycloak 21.1; the image
        // goes to the constructor, pinned to a current major.
        _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.4")
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(RealmJson(Convert.ToBase64String(jwtClient.RawData))),
                "/opt/keycloak/data/import/aiframework-realm.json")
            .WithCommand("--import-realm")
            .Build();
    }

    public TestPki Pki { get; }

    public string Issuer => $"{_container.GetBaseAddress().TrimEnd('/')}/realms/{Realm}";

    public string TokenEndpoint => $"{Issuer}/protocol/openid-connect/token";

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        Pki.Dispose();
    }

    public static string WriteSecretFile(string directory)
    {
        var path = Path.Combine(directory, "client-secret");
        File.WriteAllText(path, SecretClientSecret + "\n");
        return path;
    }

    public string WriteJwtClientPfx(string directory)
    {
        var path = Path.Combine(directory, "jwt-client.pfx");
        File.WriteAllBytes(path, _jwtClientPfx);
        return path;
    }

    private static string RealmJson(string certificateBase64) => $$"""
        {
          "realm": "{{Realm}}",
          "enabled": true,
          "clients": [
            {
              "clientId": "{{SecretClientId}}",
              "enabled": true,
              "publicClient": false,
              "serviceAccountsEnabled": true,
              "standardFlowEnabled": false,
              "clientAuthenticatorType": "client-secret",
              "secret": "{{SecretClientSecret}}"
            },
            {
              "clientId": "{{JwtClientId}}",
              "enabled": true,
              "publicClient": false,
              "serviceAccountsEnabled": true,
              "standardFlowEnabled": false,
              "clientAuthenticatorType": "client-jwt",
              "attributes": {
                "use.jwks.url": "false",
                "jwt.credential.certificate": "{{certificateBase64}}",
                "token.endpoint.auth.signing.alg": "RS256"
              }
            }
          ]
        }
        """;
}

// CA1711: the name ends in "Collection" without implementing ICollection<T>. This is the xUnit
// collection-definition naming convention (PostgresCollection does the same), not a collection
// type, so the rule's intent does not apply here.
#pragma warning disable CA1711
[CollectionDefinition(nameof(KeycloakCollection))]
public sealed class KeycloakCollection : ICollectionFixture<KeycloakFixture>;
#pragma warning restore CA1711
