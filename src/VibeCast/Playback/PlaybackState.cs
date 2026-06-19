namespace VibeCast.Playback;

/// <summary>
/// What the persistent "now playing" component is showing. RSS items carry
/// <see cref="MediaUrl"/> (the loopback media endpoint); YouTube items carry
/// <see cref="YouTubeVideoId"/> for the IFrame embed. <see cref="ExternalTarget"/> is
/// what "Open in external app" hands to ShellExecute -- the local file path for RSS,
/// the plain watch?v= URL for YouTube. <see cref="IsVideo"/> drives whether the
/// expand-to-theater toggle is shown (no point enlarging audio-only RSS).
/// </summary>
internal sealed record PlaybackState(
    int EpisodeId,
    string Title,
    string FeedTitle,
    bool IsRss,
    string? MediaUrl,
    string? YouTubeVideoId,
    string ExternalTarget,
    int InitialPositionSeconds,
    bool IsVideo);
