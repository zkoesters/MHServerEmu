using System.Net;

namespace MHServerEmu.Core.Network.Web
{
    public sealed class WebRequestException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public WebRequestException(HttpStatusCode statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    internal static class BoundedRequestBodyReader
    {
        public static async Task<byte[]> ReadAsync(Stream stream, long contentLength, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);

            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));

            if (contentLength > maxBytes)
                throw new WebRequestException((HttpStatusCode)413, "Request body exceeds the configured size limit.");

            using CancellationTokenSource timeoutCancellationTokenSource = new(timeout);
            using CancellationTokenSource linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellationTokenSource.Token);

            try
            {
                if (contentLength >= 0)
                {
                    byte[] body = new byte[(int)contentLength];
                    int bytesRead = 0;

                    while (bytesRead < body.Length)
                    {
                        int read = await stream.ReadAsync(body.AsMemory(bytesRead), linkedCancellationTokenSource.Token)
                            .AsTask()
                            .WaitAsync(linkedCancellationTokenSource.Token);

                        if (read == 0)
                            throw new WebRequestException(HttpStatusCode.BadRequest, "Request body ended before the declared content length.");

                        bytesRead += read;
                    }

                    return body;
                }

                byte[] buffer = new byte[maxBytes];
                int totalBytesRead = 0;

                while (totalBytesRead < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(totalBytesRead), linkedCancellationTokenSource.Token)
                        .AsTask()
                        .WaitAsync(linkedCancellationTokenSource.Token);

                    if (read == 0)
                    {
                        if (totalBytesRead == buffer.Length)
                            return buffer;

                        Array.Resize(ref buffer, totalBytesRead);
                        return buffer;
                    }

                    totalBytesRead += read;
                }

                byte[] overflowBuffer = new byte[1];
                int overflowRead = await stream.ReadAsync(overflowBuffer, linkedCancellationTokenSource.Token)
                    .AsTask()
                    .WaitAsync(linkedCancellationTokenSource.Token);

                if (overflowRead != 0)
                    throw new WebRequestException((HttpStatusCode)413, "Request body exceeds the configured size limit.");

                return buffer;
            }
            catch (OperationCanceledException) when (timeoutCancellationTokenSource.IsCancellationRequested && cancellationToken.IsCancellationRequested == false)
            {
                throw new WebRequestException(HttpStatusCode.RequestTimeout, "Request body read timed out.");
            }
        }
    }
}
