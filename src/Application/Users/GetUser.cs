using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Users;

public sealed record GetUser(Guid Id) : IQuery<SessionView>;

public sealed class GetUserHandler(IUserRepository users) : IQueryHandler<GetUser, SessionView>
{
    public async Task<Result<SessionView>> HandleAsync(
        GetUser query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var user = await users.GetAsync(query.Id, cancellationToken).ConfigureAwait(false);

        return user is null
            ? Result.Failure<SessionView>(new Error(
                ErrorKind.NotFound, "user.not_found", $"No user with id '{query.Id}'."))
            : Result.Success(new SessionView(
                user.Id, user.Username, user.DisplayName, user.SecurityStamp, user.Role));
    }
}
