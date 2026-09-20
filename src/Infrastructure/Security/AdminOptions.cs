using AiFramework.Domain.Users;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Security;

/// <summary>
/// Who holds <see cref="UserRole.Admin"/>. Bound from the "Admin" configuration section in each
/// host's Program.cs — not in a registration method here, for the reason <c>CacheOptions</c> and
/// <c>JobOptions</c> give: binding in this layer would make the options depend on an
/// IConfiguration a bare ServiceCollection in a unit test does not have. Keyed — <c>Admin__Usernames__0</c>, double underscores, like every other key in this
/// application. A single underscore binds nothing and warns nothing.
/// </summary>
/// <remarks>
/// <para>
/// Configuration SEEDS the administrator list rather than mirroring it: there is then no
/// bootstrapping paradox — no administrator is needed to appoint the first administrator — and
/// the same mechanism works on a fresh database with no users, in <c>ApiFactory</c>, in the e2e
/// stack and in the kind overlay. Everyone named here is promoted at startup and nobody is
/// demoted, so a grant made in the application survives a restart and this list doubles as a
/// break-glass path back in. The cost is that removing a name revokes nothing, and that an
/// account demoted in the application while still named here is promoted straight back at the
/// next start. See ADR 0022, which supersedes ADR 0020 on this point.
/// </para>
/// <para>
/// Get-only list: the configuration binder fills an existing collection rather than assigning a
/// new one, the same shape <c>JobOptions.Schedules</c> relies on.
/// </para>
/// </remarks>
public sealed class AdminOptions
{
    /// <summary>
    /// Usernames as a person would type them. Matched through <see cref="User.Normalize"/>, so
    /// "Ada" and "ada" name the same account.
    /// </summary>
    public IList<string> Usernames { get; } = [];

    /// <summary>
    /// Whether <c>AdminReconciler</c> touches the database at startup. On everywhere real; set to
    /// <c>false</c> by the two commands that BOOT THE APPLICATION WITHOUT A DATABASE — OpenAPI
    /// document generation and <c>codegen write</c>.
    /// </summary>
    /// <remarks>
    /// Exactly parallel to <c>Wolverine__Durable=false</c>, and for the identical reason: those
    /// commands run the whole application against a connection string that is never opened, so
    /// any startup path that dials Postgres fails them. This one failed them loudly —
    /// <c>ObjectDisposedException</c> out of the document generator, because EnableRetryOnFailure
    /// keeps the scope alive retrying a refused connection past the point the generator disposes
    /// its provider. Found by the contract build going red, not reasoned out in advance.
    /// </remarks>
    public bool ReconcileOnStart { get; set; } = true;
}

/// <summary>
/// Fails startup on a configured username that could never match an account.
/// </summary>
/// <remarks>
/// An EMPTY list is legal and deliberately passes: "no administrators" is the correct
/// configuration for every environment that has no operator, and failing there would make the
/// monitoring feature impossible to deploy without one. A BLANK or over-long entry is the
/// opposite — neither can ever match a row, so it is a typo, and a typo here fails silently in
/// the direction of nobody having access. Loud at startup beats invisible at runtime.
/// </remarks>
internal sealed class AdminOptionsValidator : IValidateOptions<AdminOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        for (var index = 0; index < options.Usernames.Count; index++)
        {
            var username = options.Usernames[index];

            if (string.IsNullOrWhiteSpace(username))
            {
                failures.Add($"Admin:Usernames:{index} is blank.");
            }
            else if (username.Trim().Length > User.MaxUsernameLength)
            {
                failures.Add(
                    $"Admin:Usernames:{index} is longer than {User.MaxUsernameLength} characters, " +
                    "so it cannot name an account.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
