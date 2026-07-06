using VibeCast.Downloads;
using Xunit;

namespace VibeCast.Tests;

public class MediaTypeExtensionsTests
{
    [Theory]
    [InlineData("audio/mpeg", ".mp3")]
    [InlineData("audio/mp3", ".mp3")]
    [InlineData("audio/x-m4a", ".m4a")]
    [InlineData("video/mp4", ".mp4")]
    [InlineData("audio/ogg", ".ogg")]
    public void ToExtension_MapsKnownTypes(string mediaType, string expected)
    {
        Assert.Equal(expected, MediaTypeExtensions.ToExtension(mediaType));
    }

    [Fact]
    public void ToExtension_IsCaseInsensitive()
    {
        Assert.Equal(".mp3", MediaTypeExtensions.ToExtension("AUDIO/MPEG"));
    }

    [Fact]
    public void ToExtension_StripsParameters()
    {
        Assert.Equal(".opus", MediaTypeExtensions.ToExtension("audio/opus; codecs=opus"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("application/x-evil")]
    [InlineData("text/html")]
    public void ToExtension_UnknownOrEmpty_FallsBackToSafeBin(string? mediaType)
    {
        // The fallback must never be an executable/script extension a feed could exploit.
        Assert.Equal(MediaTypeExtensions.FallbackExtension, MediaTypeExtensions.ToExtension(mediaType));
        Assert.Equal(".bin", MediaTypeExtensions.ToExtension(mediaType));
    }
}
