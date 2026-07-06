using VibeCast.Feeds;
using Xunit;

namespace VibeCast.Tests;

public class SlugGeneratorTests
{
    [Theory]
    [InlineData("The Daily", "the-daily")]
    [InlineData("Hello, World!", "hello-world")]
    [InlineData("  Spaced  Out  ", "spaced-out")]
    [InlineData("Multiple---dashes", "multiple-dashes")]
    [InlineData("Trailing!!!", "trailing")]
    [InlineData("Episode 42", "episode-42")]
    public void Slugify_ProducesLowercaseDashSeparated(string input, string expected)
    {
        Assert.Equal(expected, SlugGenerator.Slugify(input, 60));
    }

    [Fact]
    public void Slugify_TruncatesToMaxLength_WithoutTrailingDash()
    {
        var result = SlugGenerator.Slugify("aaaa bbbb cccc", 6);
        Assert.True(result.Length <= 6);
        Assert.False(result.EndsWith('-'));
    }

    [Fact]
    public void Slugify_AllPunctuation_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SlugGenerator.Slugify("!!!///", 60));
    }

    [Fact]
    public void Generate_ReturnsBaseSlug_WhenUnique()
    {
        Assert.Equal("the-daily", SlugGenerator.Generate("The Daily", []));
    }

    [Fact]
    public void Generate_AppendsSuffix_OnCollision()
    {
        var slug = SlugGenerator.Generate("The Daily", ["the-daily"]);
        Assert.Equal("the-daily-2", slug);
    }

    [Fact]
    public void Generate_WalksPastMultipleCollisions()
    {
        var slug = SlugGenerator.Generate("The Daily", ["the-daily", "the-daily-2", "the-daily-3"]);
        Assert.Equal("the-daily-4", slug);
    }

    [Fact]
    public void Generate_CollisionCheck_IsCaseInsensitive()
    {
        var slug = SlugGenerator.Generate("The Daily", ["THE-DAILY"]);
        Assert.Equal("the-daily-2", slug);
    }

    [Fact]
    public void Generate_EmptySeed_FallsBackToFeed()
    {
        Assert.Equal("feed", SlugGenerator.Generate("!!!", []));
    }
}
