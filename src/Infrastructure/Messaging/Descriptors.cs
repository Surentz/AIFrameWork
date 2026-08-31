namespace AiFramework.Infrastructure.Messaging;

/// <summary>
/// A command's dispatch closure, captured at registration so dispatch needs no reflection.
/// The delegate returns object? because the closed generic is known only inside AddCommand.
/// </summary>
public sealed record CommandDescriptor(
    Type CommandType,
    Func<IServiceProvider, object, CancellationToken, Task<object?>> Invoke);

/// <summary>The query-side equivalent. See CommandDescriptor.</summary>
public sealed record QueryDescriptor(
    Type QueryType,
    Func<IServiceProvider, object, CancellationToken, Task<object?>> Invoke);
