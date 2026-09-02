using AiFramework.Domain.Abstractions;
using FluentAssertions;

namespace AiFramework.Domain.Tests.Abstractions;

public sealed class EntityTests
{
    private sealed record Raised : IDomainEvent;

    private sealed class Thing : Entity
    {
        public void Do() => Raise(new Raised());
    }

    [Fact]
    public void Raise_AppendsToDomainEvents()
    {
        var thing = new Thing();

        thing.Do();

        thing.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<Raised>();
    }

    [Fact]
    public void ClearDomainEvents_EmptiesTheCollection()
    {
        var thing = new Thing();
        thing.Do();

        thing.ClearDomainEvents();

        thing.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void DomainEvents_OnANewEntity_IsEmpty()
    {
        var thing = new Thing();

        thing.DomainEvents.Should().BeEmpty();
    }
}
