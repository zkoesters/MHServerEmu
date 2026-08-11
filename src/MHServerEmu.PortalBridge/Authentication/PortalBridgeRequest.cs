using System.Collections.ObjectModel;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.PortalBridge.Authentication
{
    public sealed class PortalBridgeRequest
    {
        private static readonly string[] RequiredHeaders =
        {
            HmacRequestValidator.ContractVersionHeader,
            HmacRequestValidator.TimestampHeader,
            HmacRequestValidator.NonceHeader,
            HmacRequestValidator.BodyDigestHeader,
            HmacRequestValidator.OperationIdHeader,
            HmacRequestValidator.KeyIdHeader,
            HmacRequestValidator.SignatureHeader,
        };
        private readonly IReadOnlyDictionary<string, string[]> _headers;

        public string Method { get; }
        public string RawUrl { get; }
        public bool HasEntityBody { get; }
        public long ContentLength64 { get; }
        public string TransferEncoding { get; }
        public string BodySha256 { get; }
        public IReadOnlyDictionary<string, string[]> Headers { get => CopyHeaders(); }

        public PortalBridgeRequest(string method, string rawUrl, bool hasEntityBody, long contentLength64,
            string transferEncoding, IReadOnlyDictionary<string, string[]> headers, string bodySha256 = null)
        {
            Method = method;
            RawUrl = rawUrl;
            HasEntityBody = hasEntityBody;
            ContentLength64 = contentLength64;
            TransferEncoding = transferEncoding;
            BodySha256 = bodySha256;

            Dictionary<string, string[]> headerCopies = new(StringComparer.OrdinalIgnoreCase);
            if (headers != null)
            {
                foreach ((string name, string[] values) in headers)
                    headerCopies[name] = values?.ToArray() ?? Array.Empty<string>();
            }

            _headers = new ReadOnlyDictionary<string, string[]>(headerCopies);
        }

        public static async Task<PortalBridgeRequest> FromContextAsync(WebRequestContext context)
        {
            Dictionary<string, string[]> headers = new(StringComparer.OrdinalIgnoreCase);
            foreach (string header in RequiredHeaders)
                headers[header] = context.GetHeaderValues(header);

            return new PortalBridgeRequest(context.HttpMethod, context.RawUrl, context.HasEntityBody,
                context.ContentLength64, context.TransferEncoding, headers, await context.GetBodySha256Async());
        }

        internal bool TryGetHeaderValues(string name, out string[] values)
        {
            return _headers.TryGetValue(name, out values);
        }

        private IReadOnlyDictionary<string, string[]> CopyHeaders()
        {
            Dictionary<string, string[]> headerCopies = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, string[] values) in _headers)
                headerCopies[name] = values.ToArray();

            return new ReadOnlyDictionary<string, string[]>(headerCopies);
        }
    }
}
