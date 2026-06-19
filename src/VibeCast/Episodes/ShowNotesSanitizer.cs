using AngleSharp.Html.Dom;
using Ganss.Xss;

namespace VibeCast.Episodes;

/// <summary>
/// Sanitizes feed-supplied show-note HTML before rendering, per CLAUDE.md's
/// untrusted-input rule: strip scripts and unsafe markup, keep links clickable.
/// Episodes store the raw HTML; sanitization always happens at render time, never
/// at ingest, so a sanitizer fix doesn't require re-fetching every feed.
/// </summary>
internal sealed class ShowNotesSanitizer
{
    private readonly HtmlSanitizer sanitizer = new();

    public ShowNotesSanitizer()
    {
        // External show-note links should open in a new tab rather than navigating
        // away from the single-page app in the current one.
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is IHtmlAnchorElement anchor)
            {
                anchor.SetAttribute("target", "_blank");
                anchor.SetAttribute("rel", "noopener noreferrer");
            }
        };
    }

    public string Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : sanitizer.Sanitize(html);
}
