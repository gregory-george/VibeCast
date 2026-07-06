using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeCast.Data;

namespace VibeCast.Tests;

/// <summary>
/// Backs tests that need a real <see cref="AppDbContext"/> with a SQLite in-memory
/// database. The connection is held open for the factory's lifetime so the schema
/// survives across the short-lived contexts each unit of work creates.
/// </summary>
internal sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly DbContextOptions<AppDbContext> options;

    public TestDbContextFactory()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        using var ctx = new AppDbContext(options);
        ctx.Database.EnsureCreated();
    }

    public AppDbContext CreateDbContext() => new(options);

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AppDbContext(options));

    public void Dispose() => connection.Dispose();
}

/// <summary>Returns a canned response for every request, for exercising HTTP-driven services.</summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(responder(request));
}

/// <summary>
/// Read-only, non-seekable stream. StreamContent auto-computes Content-Length from a
/// seekable stream, which would defeat the truncation test; a non-seekable stream forces
/// the declared length to be whatever the test sets explicitly.
/// </summary>
internal sealed class NonSeekableStream(byte[] data) : Stream
{
    private readonly MemoryStream inner = new(data);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
