using AiFramework.Application.Assistant;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant;

public sealed class AssistantTitleTests
{
    [Fact]
    public void From_ShortQuestion_IsTheQuestion()
    {
        AssistantTitle.From("Where is my order?").Should().Be("Where is my order?");
    }

    [Fact]
    public void From_WhitespaceAndNewlines_CollapseToSingleSpaces()
    {
        AssistantTitle.From("  Where\n\nis   my\torder?  ").Should().Be("Where is my order?");
    }

    [Fact]
    public void From_LongQuestion_IsCutAtAWordBoundaryWithAnEllipsis()
    {
        var title = AssistantTitle.From("Why did the nightly order export fail for three customers in the last week and what should I do?");

        title.Should().EndWith("…");
        title.Length.Should().BeLessThanOrEqualTo(AssistantTitle.MaxLength);
        title.Should().Be("Why did the nightly order export fail for three customers…");
    }

    [Fact]
    public void From_OneVeryLongWord_IsCutMidWord()
    {
        var title = AssistantTitle.From(new string('x', 80));

        title.Should().Be(new string('x', AssistantTitle.MaxLength - 1) + "…");
    }
}
