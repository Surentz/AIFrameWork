using AiFramework.Application.Assistant;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant;

public sealed class AskAssistantValidatorTests
{
    private static bool IsValid(string question) =>
        new AskAssistantValidator().Validate(new AskAssistant(null, question, ToolScope.Member)).IsValid;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankQuestion_Fails(string question)
    {
        IsValid(question).Should().BeFalse();
    }

    [Fact]
    public void Validate_MaxLengthAfterTrimming_Passes()
    {
        IsValid("  " + new string('q', AskAssistantValidator.MaxQuestionLength) + "  ").Should().BeTrue();
    }

    [Fact]
    public void Validate_OneOverMaxLength_Fails()
    {
        IsValid(new string('q', AskAssistantValidator.MaxQuestionLength + 1)).Should().BeFalse();
    }
}
