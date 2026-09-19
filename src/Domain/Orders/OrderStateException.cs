namespace AiFramework.Domain.Orders;

/// <summary>
/// An order was asked to make a transition its current state does not allow — shipping twice,
/// cancelling something already in the post.
/// </summary>
/// <remarks>
/// Its own type rather than a bare <see cref="DomainException"/> because the two failure classes
/// <see cref="Order.Cancel"/> can produce have to reach the caller as DIFFERENT HTTP statuses.
/// "Already shipped" is a conflict with existing state (409); "no reason given" is a malformed
/// request the caller can fix by editing their input (400, which
/// <c>GlobalExceptionHandler</c> already maps every <see cref="DomainException"/> to).
///
/// Without the distinction, a handler catching <see cref="DomainException"/> around a call that
/// throws for both reasons has to answer one status for both. That was only ever correct in this
/// codebase because <c>CancelOrderValidator</c> happens to duplicate the same input rules and
/// rejects them first — so the bug was real but unreachable, and would have surfaced the moment
/// the validator and the aggregate drifted apart.
/// </remarks>
public class OrderStateException : DomainException
{
    public OrderStateException()
    {
    }

    public OrderStateException(string message) : base(message)
    {
    }

    public OrderStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
