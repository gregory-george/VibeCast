namespace VibeCast.Feeds;

/// <summary>
/// Maps an artwork response's declared Content-Type to a file extension. Same
/// filename-safety rule as episode downloads (CLAUDE.md): derive the extension
/// from the server-declared type, never from the URL. Unlike episode enclosures,
/// an unrecognized type has no safe fallback -- it's simply rejected, since
/// artwork must actually be a displayable image.
/// </summary>
internal static class ArtworkContentType
{
    private static readonly Dictionary<string, string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
    };

    public static bool TryGetExtension(string? contentType, out string extension)
    {
        extension = string.Empty;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        // Strip parameters like "; charset=...".
        var bare = contentType.Split(';')[0].Trim();
        return KnownTypes.TryGetValue(bare, out extension!);
    }

    public static string ToContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        foreach (var (contentType, ext) in KnownTypes)
        {
            if (string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase))
            {
                return contentType;
            }
        }

        return "application/octet-stream";
    }
}
