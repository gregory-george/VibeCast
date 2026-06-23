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

    /// <summary>
    /// Falls back to sniffing the image's magic-number signature when the server
    /// omits Content-Type or sends an unrecognized one (seen on some CDNs that
    /// serve artwork with no Content-Type header at all). Still never trusts the
    /// URL/filename -- this reads the actual bytes, same trust boundary as before.
    /// </summary>
    public static bool TryDetectFromSignature(ReadOnlySpan<byte> header, out string extension)
    {
        extension = string.Empty;

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            extension = ".jpg";
            return true;
        }

        if (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            extension = ".png";
            return true;
        }

        if (header.Length >= 6 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38
            && (header[4] == 0x37 || header[4] == 0x39) && header[5] == 0x61)
        {
            extension = ".gif";
            return true;
        }

        if (header.Length >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
        {
            extension = ".webp";
            return true;
        }

        return false;
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
