using FluentAssertions;
using MelonMQ.Common;
using Xunit;

namespace MelonMQ.Tests.Common;

public class TopicMatcherTests
{
    [Theory]
    [InlineData("user.order.created", "user.order.created", true)]
    [InlineData("user.order.created", "user.order.updated", false)]
    [InlineData("user.order.created", "user.*.created", true)]
    [InlineData("user.order.created", "user.*.updated", false)]
    [InlineData("user.order.created", "*.order.created", true)]
    [InlineData("user.order.created", "*.payment.created", false)]
    [InlineData("user.order.created", "*.*.*", true)]
    [InlineData("user.order.created", "*.*", false)]
    [InlineData("user.order.created", "user.#", true)]
    [InlineData("user.order.created", "user.order.#", true)]
    [InlineData("user.order.created", "order.#", false)]
    [InlineData("user.order.created.v2", "user.#", true)]
    [InlineData("user.order.created.v2", "user.order.#", true)]
    [InlineData("user.order.created.v2", "user.order.*.v2", true)]
    [InlineData("user", "user.#", true)]
    [InlineData("user", "user.*", false)]
    [InlineData("", "#", true)]
    [InlineData("", "*", false)]
    public void IsMatch_ShouldReturnCorrectResult(string routingKey, string pattern, bool expected)
    {
        // Act
        var result = TopicMatcher.IsMatch(routingKey, pattern);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("user..order", "user.*.order")]
    [InlineData("user.order.", "user.order.*")]
    [InlineData(".user.order", "*.user.order")]
    public void IsMatch_WithEmptySegments_ShouldHandleCorrectly(string routingKey, string pattern)
    {
        // Act
        var result = TopicMatcher.IsMatch(routingKey, pattern);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsMatch_WithNullRoutingKey_ShouldThrow()
    {
        // Act & Assert
        var act = () => TopicMatcher.IsMatch(null!, "pattern");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void IsMatch_WithNullPattern_ShouldThrow()
    {
        // Act & Assert
        var act = () => TopicMatcher.IsMatch("routing.key", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("a.b.c.d.e.f", "a.*.c.*.e.*", true)]
    [InlineData("a.b.c.d.e.f", "a.#.f", true)]
    [InlineData("a.b.c.d.e.f", "a.#.g", false)]
    [InlineData("very.long.routing.key.with.many.segments", "very.#.segments", true)]
    [InlineData("very.long.routing.key.with.many.segments", "very.*.routing.*.with.*.segments", true)]
    public void IsMatch_WithComplexPatterns_ShouldWork(string routingKey, string pattern, bool expected)
    {
        // Act
        var result = TopicMatcher.IsMatch(routingKey, pattern);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("user.order", "#")]
    [InlineData("user.order.created", "#")]
    [InlineData("", "#")]
    [InlineData("single", "#")]
    public void IsMatch_WithHashOnlyPattern_ShouldAlwaysMatch(string routingKey, string pattern)
    {
        // Act
        var result = TopicMatcher.IsMatch(routingKey, pattern);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("user.order.created", "user.order.created.#")]
    [InlineData("exact.match", "exact.match.#")]
    public void IsMatch_WithTrailingHash_ShouldMatch(string routingKey, string pattern)
    {
        // Act
        var result = TopicMatcher.IsMatch(routingKey, pattern);

        // Assert
        result.Should().BeTrue();
    }
}