using VibeCast.Episodes;
using Xunit;

namespace VibeCast.Tests;

// Show notes are raw feed-supplied HTML rendered into the app's own DOM -- the
// untrusted-input hard rule (CLAUDE.md) says scripts and unsafe markup must be
// stripped while links stay clickable. These pin the sanitizer's contract so a
// library upgrade or config change can't silently reopen an XSS hole.
public class ShowNotesSanitizerTests
{
    private readonly ShowNotesSanitizer sanitizer = new();

    [Fact]
    public void ScriptTags_AreStripped()
    {
        var result = sanitizer.Sanitize("<p>notes</p><script>alert(1)</script>");
        Assert.Contains("notes", result);
        Assert.DoesNotContain("<script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", result);
    }

    [Fact]
    public void EventHandlerAttributes_AreStripped()
    {
        var result = sanitizer.Sanitize("""<img src="https://example.com/x.jpg" onerror="alert(1)">""");
        Assert.DoesNotContain("onerror", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JavascriptUrls_AreStripped()
    {
        var result = sanitizer.Sanitize("""<a href="javascript:alert(1)">click</a>""");
        Assert.DoesNotContain("javascript:", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Iframes_AreStripped()
    {
        var result = sanitizer.Sanitize("""<iframe src="https://evil.example/"></iframe><p>ok</p>""");
        Assert.DoesNotContain("<iframe", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ok", result);
    }

    [Fact]
    public void HttpsLinks_StayClickable()
    {
        var result = sanitizer.Sanitize("""<a href="https://example.com/page">episode link</a>""");
        Assert.Contains("href=\"https://example.com/page\"", result);
        Assert.Contains("episode link", result);
    }

    [Fact]
    public void Anchors_OpenInNewTab_WithNoOpener()
    {
        // The post-process hook must retarget every anchor so external links don't
        // navigate the single-page app away (and noopener blocks reverse tabnabbing).
        var result = sanitizer.Sanitize("""<a href="https://example.com/">x</a>""");
        Assert.Contains("target=\"_blank\"", result);
        Assert.Contains("rel=\"noopener noreferrer\"", result);
    }

    [Fact]
    public void BenignFormatting_IsPreserved()
    {
        var result = sanitizer.Sanitize("<p>Para</p><ul><li>Item</li></ul><strong>bold</strong>");
        Assert.Contains("<p>Para</p>", result);
        Assert.Contains("<li>Item</li>", result);
        Assert.Contains("<strong>bold</strong>", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespace_ReturnsEmpty(string? html)
    {
        Assert.Equal(string.Empty, sanitizer.Sanitize(html));
    }
}
