using System.Net;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.Core.Tests.Network.Web
{
    public class BoundedRequestBodyReaderTests
    {
        [Fact]
        public async Task ReadAsync_KnownOversizedBody_ThrowsPayloadTooLarge()
        {
            using MemoryStream stream = new(new byte[17]);

            WebRequestException exception = await Assert.ThrowsAsync<WebRequestException>(() =>
                BoundedRequestBodyReader.ReadAsync(stream, 17, 16, TimeSpan.FromSeconds(1)));

            Assert.Equal((HttpStatusCode)413, exception.StatusCode);
        }

        [Fact]
        public async Task ReadAsync_UnknownOversizedBody_ThrowsPayloadTooLarge()
        {
            using MemoryStream stream = new(new byte[17]);

            WebRequestException exception = await Assert.ThrowsAsync<WebRequestException>(() =>
                BoundedRequestBodyReader.ReadAsync(stream, -1, 16, TimeSpan.FromSeconds(1)));

            Assert.Equal((HttpStatusCode)413, exception.StatusCode);
        }

        [Fact]
        public async Task ReadAsync_PrematureEndOfStream_ThrowsBadRequest()
        {
            using MemoryStream stream = new(new byte[4]);

            WebRequestException exception = await Assert.ThrowsAsync<WebRequestException>(() =>
                BoundedRequestBodyReader.ReadAsync(stream, 5, 16, TimeSpan.FromSeconds(1)));

            Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        }

        [Fact]
        public async Task ReadAsync_BlockedStream_ThrowsRequestTimeout()
        {
            await using BlockingStream stream = new();

            WebRequestException exception = await Assert.ThrowsAsync<WebRequestException>(() =>
                BoundedRequestBodyReader.ReadAsync(stream, -1, 16, TimeSpan.FromMilliseconds(50)));

            Assert.Equal(HttpStatusCode.RequestTimeout, exception.StatusCode);
        }

        private sealed class BlockingStream : Stream
        {
            public override bool CanRead { get => true; }
            public override bool CanSeek { get => false; }
            public override bool CanWrite { get => false; }
            public override long Length { get => throw new NotSupportedException(); }
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return new(Task.Delay(Timeout.InfiniteTimeSpan).ContinueWith(_ => 0));
            }
        }
    }
}
