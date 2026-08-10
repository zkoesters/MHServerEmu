using System.Diagnostics;
using System.Net;
using MHServerEmu.Core.Logging;

namespace MHServerEmu.Core.Network.Web
{
    public class WebService
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly Dictionary<string, WebHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

        private HttpListener _listener;
        private CancellationTokenSource _cts;

        public WebServiceSettings Settings { get; }
        public bool IsRunning { get; private set; }

        public int HandlerCount { get => _handlers.Count; }
        public int HandledRequests { get; private set; }

        public WebService(WebServiceSettings settings)
        {
            Settings = settings;
        }

        public override string ToString()
        {
            return Settings.Name;
        }

        /// <summary>
        /// Starts the web service. Returns <see langword="true"/> if successful.
        /// </summary>
        public bool Start()
        {
            if (IsRunning)
                return false;

            Debug.Assert(_listener == null);
            Debug.Assert(_cts == null);

            string url = Settings.ListenUrl;

            HttpListener listener = new();
            listener.Prefixes.Add(url);
            listener.Start();

            CancellationTokenSource cts = new();

            _listener = listener;
            _cts = cts;

            IsRunning = true;
            Task.Run(() => HandleRequestsAsync(listener, cts.Token));
            return true;
        }

        /// <summary>
        /// Stops the currently running REST service. Returns <see langword="true"/> if successful.
        /// </summary>
        public bool Stop()
        {
            if (_listener == null && _cts == null)
                return false;

            HttpListener listener = _listener;
            CancellationTokenSource cts = _cts;

            _cts = null;
            _listener = null;
            IsRunning = false;

            try
            {
                cts?.Cancel();
                listener?.Close();
            }
            finally
            {
                cts?.Dispose();
            }

            return true;
        }

        /// <summary>
        /// Returns the currently registered <see cref="WebHandler"/> for the specified local path if available.
        /// Returns the fallback handler if no handler is registered for the local path, which may be <see langword="null"/>.
        /// </summary>
        public WebHandler GetHandler(string localPath)
        {
            if (_handlers.TryGetValue(localPath, out WebHandler handler) == false)
                return Settings.FallbackHandler;

            return handler;
        }

        /// <summary>
        /// Registers the provided <see cref="WebHandler"/> for the specified local path.
        /// Returns <see langword="true"/> if successful.
        /// </summary>
        public bool RegisterHandler(string localPath, WebHandler handler)
        {
            bool added = _handlers.TryAdd(localPath, handler);

            if (added)
                handler.Register(this, localPath);
            else
                Logger.Warn($"RegisterHandler(): Local path {localPath} already has a registered handler");

            return added;
        }

        /// <summary>
        /// Removed the currently registered <see cref="WebHandler"/> for the specified local path.
        /// Returns <see langword="true"/> if successful.
        /// </summary>
        public bool RemoveHandler(string localPath)
        {
            bool removed = _handlers.Remove(localPath, out WebHandler handler);

            if (removed)
                handler.Unregister();
            else
                Logger.Warn($"RemoveHandler(): No handler is registered for local path {localPath}");

            return removed;
        }

        internal async Task WriteExceptionAsync(WebRequestContext context, Exception exception)
        {
            context.StatusCode = (int)HttpStatusCode.InternalServerError;

            IWebExceptionWriter exceptionWriter = Settings.ExceptionWriter;
            if (exceptionWriter == null)
                return;

            try
            {
                await exceptionWriter.WriteAsync(context, exception);
            }
            catch (Exception e)
            {
                context.StatusCode = (int)HttpStatusCode.InternalServerError;
                Logger.Warn($"WriteExceptionAsync(): {e}");
            }
        }

        /// <summary>
        /// Handles incoming requests asynchronously.
        /// </summary>
        private async Task HandleRequestsAsync(HttpListener listener, CancellationToken cancellationToken)
        {
            Logger.Info($"{this} is listening on {Settings.ListenUrl}...");

            try
            {
                while (cancellationToken.IsCancellationRequested == false)
                {
                    HttpListenerContext httpContext;

                    try
                    {
                        httpContext = await listener.GetContextAsync().WaitAsync(cancellationToken);
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"HandleRequestsAsync(): {e}");
                        return;
                    }

                    WebRequestContext requestContext = new(httpContext);

                    try
                    {
                        await HandleRequestAsync(requestContext);
                    }
                    catch (Exception e)
                    {
                        Logger.Warn($"Error handling {requestContext}: {e}");
                        await WriteExceptionAsync(requestContext, e);
                    }
                    finally
                    {
                        try
                        {
                            httpContext.Response.Close();
                        }
                        catch (Exception e)
                        {
                            Logger.Warn($"Failed to close response for {requestContext}: {e}");
                        }

                        HandledRequests++;
                    }
                }
            }
            finally
            {
                IsRunning = false;
            }
        }

        private async Task HandleRequestAsync(WebRequestContext requestContext)
        {
            IWebRequestAuthorizer requestAuthorizer = Settings.RequestAuthorizer;
            if (requestAuthorizer != null && await requestAuthorizer.AuthorizeAsync(requestContext) == false)
            {
                requestContext.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            // This may be either a registered handler or a fallback handler.
            WebHandler handler = GetHandler(requestContext.LocalPath);
            if (handler == null)
            {
                requestContext.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            await handler.HandleAsync(requestContext);
        }
    }
}
