using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace VibeCast.Data;

internal sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Feed> Feeds => Set<Feed>();

    public DbSet<Episode> Episodes => Set<Episode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite's EF Core provider can't translate ORDER BY on DateTimeOffset, so
        // every timestamp here is DateTime, always UTC by convention (the "Utc"
        // suffix on each property is load-bearing, not decorative). SQLite drops
        // Kind on round-trip, so this converter re-stamps Kind=Utc on read.
        var utcDateTimeConverter = new ValueConverter<DateTime, DateTime>(
            v => v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        modelBuilder.Entity<Feed>(feed =>
        {
            feed.Property(f => f.Type).HasConversion<string>();
            feed.Property(f => f.DateAddedUtc).HasConversion(utcDateTimeConverter);
            feed.Property(f => f.LastRefreshedUtc).HasConversion(utcDateTimeConverter);
            // Explicit DB-level defaults so a migration backfills existing rows the
            // same way as the C# property initializer, not the CLR type default.
            feed.Property(f => f.AutoDownloadEnabled).HasDefaultValue(true);
            feed.Property(f => f.AutoDownloadMaxAgeDays).HasDefaultValue(90);
            feed.HasIndex(f => f.Slug).IsUnique();
            feed.HasIndex(f => f.FeedUrl).IsUnique();
        });

        modelBuilder.Entity<Episode>(episode =>
        {
            episode.Property(e => e.PublishedAtUtc).HasConversion(utcDateTimeConverter);
            episode.Property(e => e.ScheduledStartUtc).HasConversion(utcDateTimeConverter);

            // The composite de-dup key: scoped per feed, so the same GUID/video ID
            // appearing in two feeds stays distinct. Also the additive-refresh guard
            // against re-inserting an episode the app has already seen.
            episode.HasIndex(e => new { e.FeedId, e.DedupKey }).IsUnique();

            episode.HasOne(e => e.Feed)
                .WithMany(f => f.Episodes)
                .HasForeignKey(e => e.FeedId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
