using System.Text;

namespace SettlementRecon.Tests.TestSupport;

internal static class TestStreams
{
    public static MemoryStream FromText(string text, bool withBom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return withBom ? new MemoryStream([.. Encoding.UTF8.Preamble, .. bytes]) : new MemoryStream(bytes);
    }

    /// <summary>Returns at most a few bytes per read, so lines straddle pipe buffer segments.</summary>
    public static Stream Trickle(string text, int maxChunk = 7) => new TrickleStream(Encoding.UTF8.GetBytes(text), maxChunk);

    private sealed class TrickleStream(byte[] data, int maxChunk) : Stream
    {
        private int _position;
        private int _turn;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var size = Math.Min(Math.Min(buffer.Length, data.Length - _position), 1 + (_turn++ % maxChunk));
            data.AsSpan(_position, size).CopyTo(buffer);
            _position += size;
            return size;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
