using System.Diagnostics;
using System.Net;
using MHServerEmu.Core.Logging;

namespace MHServerEmu.Core.Network.Web
{
    public class WebService
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly Dictionary<string, WebHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lifecycleLock = new();

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _runTask = Task.CompletedTask;

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
            lock (_lifecycleLock)
            {
                if (IsRunning)
                    return false;

                Debug.Assert(_listener == null);
                Debug.Assert(_cts == null);

                string url = Settings.ListenUrl;
                HttpListener listener = new();

                try
                {
                    listener.Prefixes.Add(url);
                    listener.Start();
                }
                catch
                {
                    listener.Close();
                    throw;
                }

                CancellationTokenSource cts = new();
                _listener = listener;
                _cts = cts;
                IsRunning = true;
                _runTask = Task.Run(() => HandleRequestsAsync(listener, cts, cts.Token));
                return true;
            }
        }

        /// <summary>
        /// Stops the currently running REST service. Returns <see langword="true"/> if successful.
        /// </summary>
        public bool Stop()
        {
            return Stop(out _);
        }

        /// <summary>
        /// Stops accepting requests and returns a task that completes after accepted requests finish.
        /// </summary>
        public Task StopAsync()
        {
            Stop(out Task runTask);
            return runTask;
        }

        /// <summary>
        /// Returns a task that completes after the current accept loop and accepted request finish.
        /// </summary>
        public Task WaitForStopAsync()
        {
            lock (_lifecycleLock)
                return _runTask;
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
            {
                Logger.Warn($"Error handling {context}: {exception}");
                return;
            }

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
        private async Task HandleRequestsAsync(HttpListener listener, CancellationTokenSource cts, CancellationToken cancellationToken)
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
                try
                {
                    listener.Close();
                }
                finally
                {
                    lock (_lifecycleLock)
                    {
                        if (_listener == listener && _cts == cts)
                        {
                            _listener = null;
                            _cts = null;
                            IsRunning = false;
                        }
                    }
                    cts.Dispose();
                }
            }
        }

        private async Task HandleRequestAsync(WebRequestContext requestContext)
        {
            IWebRequestAuthorizer requestAuthorizer = Settings.RequestAuthorizer;
            if (requestAuthorizer != null && await requestAuthorizer.AuthorizeAsync(requestContext) == false)
                return;

            // This may be either a registered handler or a fallback handler.
            WebHandler handler = GetHandler(requestContext.LocalPath);
            if (handler != null)
                await handler.HandleAsync(requestContext);
        }

        private bool Stop(out Task runTask)
        {
            HttpListener listener;
            CancellationTokenSource cts;
            lock (_lifecycleLock)
            {
                runTask = _runTask;
                if (_listener == null && _cts == null)
                    return false;

                listener = _listener;
                cts = _cts;
                _listener = null;
                _cts = null;
                IsRunning = false;

                cts?.Cancel();
                listener?.Close();
            }

            return true;
        }
    }
}
