using AiFramework.Application.Abstractions;
using AiFramework.Domain.Users;
using FluentValidation;

namespace AiFramework.Application.Users;

public sealed record RegisterUser(string Username, string Password, string DisplayName)
    : ICommand<SessionView>;

public sealed class RegisterUserValidator : AbstractValidator<RegisterUser>
{
    public RegisterUserValidator()
    {
        RuleFor(c => c.Username)
            .NotEmpty()
            .MaximumLength(User.MaxUsernameLength)
            .Must(username => username is not null && Array.TrueForAll(
                username.ToCharArray(),
                c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            .WithMessage(
                "A username can only contain letters, digits, dots, underscores and hyphens.");

        RuleFor(c => c.Password).NotEmpty().MinimumLength(PasswordPolicy.MinimumLength);

        RuleFor(c => c.DisplayName).NotEmpty().MaximumLength(User.MaxDisplayNameLength);
    }
}

public sealed class RegisterUserHandler(
    IUserRepository users,
    IPasswordHasher hasher,
    IClock clock,
    IAdministratorDirectory administrators)
    : ICommandHandler<RegisterUser, SessionView>
{
    public async Task<Result<SessionView>> HandleAsync(
        RegisterUser command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The pre-check answers the common case with no failed insert, which EF would log at
        // Error. It is not the guard: two simultaneous registrations of one name both pass it,
        // and TryAddAsync below is where the second finds out.
        var taken = await users
            .GetByNormalizedUsernameAsync(User.Normalize(command.Username), cancellationToken)
            .ConfigureAwait(false);

        if (taken is not null)
        {
            return UsernameTaken(command.Username);
        }

        var user = User.Register(
            Guid.NewGuid(),
            command.Username,
            hasher.Hash(command.Password),
            command.DisplayName,
            clock.UtcNow);

        // Configuration is the authority on who administers this application, and it is asked
        // here as well as at startup. Without this, a fresh deployment's operator registers,
        // holds no access, and needs someone to restart the API before the reconciler can see
        // them - which is exactly the state AdminReconciler's "names accounts that do not
        // exist" warning describes. ADR 0020.
        if (administrators.IsAdministrator(user.Username))
        {
            user.ChangeRole(UserRole.Admin);
        }

        // The race: someone registered this name between the pre-check and here. The same 409,
        // so a caller cannot tell which of the two caught it.
        if (!await users.TryAddAsync(user, cancellationToken).ConfigureAwait(false))
        {
            return UsernameTaken(command.Username);
        }

        return Result.Success(
            new SessionView(user.Id, user.Username, user.DisplayName, user.SecurityStamp, user.Role));
    }

    private static Result<SessionView> UsernameTaken(string username) =>
        Result.Failure<SessionView>(new Error(
            ErrorKind.Conflict,
            "user.username_taken",
            $"The username '{username}' is already taken."));
}
