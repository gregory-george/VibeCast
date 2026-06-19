namespace VibeCast.Downloads;

/// <summary>
/// Maps an enclosure's declared media type to a file extension. Filename safety
/// hard rule (CLAUDE.md): derive the extension from the media type, never from the
/// URL or any feed-supplied name -- a feed is untrusted input. An unrecognized
/// media type falls back to ".bin", a safe, never-executable extension Windows has
/// no default handler for, rather than guessing a media extension that might be wrong.
/// </summary>
internal static class MediaTypeExtensions
{
    private static readonly Dictionary<string, string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = ".mp3",
        ["audio/mp3"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["audio/x-m4a"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["audio/ogg"] = ".ogg",
        ["audio/opus"] = ".opus",
        ["audio/flac"] = ".flac",
        ["audio/x-flac"] = ".flac",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/webm"] = ".weba",
        ["video/mp4"] = ".mp4",
        ["video/x-m4v"] = ".m4v",
        ["video/webm"] = ".webm",
        ["video/quicktime"] = ".mov",
    };

    public const string FallbackExtension = ".bin";

    public static string ToExtension(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return FallbackExtension;
        }

        // Strip parameters like "; codecs=opus".
        var bare = mediaType.Split(';')[0].Trim();
        return KnownTypes.TryGetValue(bare, out var ext) ? ext : FallbackExtension;
    }
}
