using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.PortalBridge.Authentication;
using MHServerEmu.PortalBridge.Handlers;

namespace MHServerEmu.PortalBridge
{
    public sealed class PortalBridgeService : IGameService
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly PortalBridgeConfig _config;
        private readonly PortalBridgeMetadata _metadata;
        private readonly Func<GameServiceState?> _playerManagerStateProvider;
        private readonly TimeProvider _timeProvider;
        private readonly INonceReplayCache _replayCache;
        private readonly object _lifecycleLock = new();
        private readonly ManualResetEventSlim _shutdownEvent = new(false);

        private WebService _webService;
        private HmacRequestValidator _requestValidator;
        private GameServiceState _state = GameServiceState.Created;
        private bool _shutdownRequested;

        public GameServiceState State
        {
            get
            {
                lock (_lifecycleLock)
                    return _state;
            }
        }

        public bool IsAvailable
        {
            get
            {
                lock (_lifecycleLock)
                    return _webService?.IsRunning == true;
            }
        }

        public PortalBridgeService(PortalBridgeConfig config, PortalBridgeMetadata metadata,
            Func<GameServiceState?> playerManagerStateProvider, TimeProvider timeProvider = null,
            INonceReplayCache replayCache = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            _playerManagerStateProvider = playerManagerStateProvider ?? throw new ArgumentNullException(nameof(playerManagerStateProvider));
            _timeProvider = timeProvider ?? TimeProvider.System;
            _replayCache = replayCache ?? new InMemoryNonceReplayCache();
        }

        public void Run()
        {
            lock (_lifecycleLock)
                _state = GameServiceState.Starting;

            if (IsShutdownRequested() == false)
                TryStartListener();

            lock (_lifecycleLock)
                _state = GameServiceState.Running;
            _shutdownEvent.Wait();

            WebService webService;
            HmacRequestValidator requestValidator;
            lock (_lifecycleLock)
            {
                _state = GameServiceState.ShuttingDown;
                webService = _webService;
                requestValidator = _requestValidator;
            }

            webService?.StopAsync().GetAwaiter().GetResult();
            requestValidator?.Dispose();

            lock (_lifecycleLock)
                _state = GameServiceState.Shutdown;
        }

        public void Shutdown()
        {
            WebService webService;
            lock (_lifecycleLock)
            {
                if (_state == GameServiceState.Shutdown)
                    return;

                _shutdownRequested = true;
                if (_state == GameServiceState.Running)
                    _state = GameServiceState.ShuttingDown;
                webService = _webService;
            }

            webService?.Stop();
            _shutdownEvent.Set();
        }

        public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage
        {
            Logger.Warn($"ReceiveServiceMessage(): Unexpected message type {typeof(T).Name}");
        }

        public void GetStatus(Dictionary<string, long> statusDict)
        {
            statusDict["PortalBridgeAvailable"] = IsAvailable ? 1 : 0;
            lock (_lifecycleLock)
                statusDict["PortalBridgeHandledRequests"] = _webService?.HandledRequests ?? 0;
        }

        private void TryStartListener()
        {
            if (_config.Enabled == false)
            {
                Logger.Info("PortalBridge is disabled");
                return;
            }

            PortalBridgeSettings settings;
            try
            {
                if (_config.TryCreateSettings(out settings, out _) == false)
                {
                    Logger.Warn("PortalBridge configuration is invalid");
                    return;
                }
            }
            catch (Exception)
            {
                Logger.Warn("PortalBridge configuration is invalid");
                return;
            }

            HmacRequestValidator validator = null;
            WebService webService = null;
            try
            {
                if (IsShutdownRequested())
                    return;

                validator = new HmacRequestValidator(settings.Secret, settings.KeyId, _replayCache, _timeProvider);
                webService = new WebService(new WebServiceSettings
                {
                    Name = "PortalBridge",
                    ListenUrl = $"http://{settings.Address}:{settings.Port}/",
                    FallbackHandler = new PortalBridgeNotFoundWebHandler(),
                    RequestAuthorizer = new PortalBridgeRequestAuthorizer(validator),
                    ExceptionWriter = new PortalBridgeExceptionWriter(),
                });
                webService.RegisterHandler(CapabilitiesWebHandler.Path,
                    new CapabilitiesWebHandler(_metadata, settings.ServerInstanceId));
                webService.RegisterHandler(HealthWebHandler.Path,
                    new HealthWebHandler(_playerManagerStateProvider, _timeProvider));

                if (webService.Start() == false)
                    return;

                lock (_lifecycleLock)
                {
                    if (_shutdownRequested)
                        webService.Stop();

                    _requestValidator = validator;
                    _webService = webService;
                }
                validator = null;
                webService = null;
            }
            catch (Exception exception)
            {
                Logger.ErrorException(exception, "PortalBridge listener failed to start");
            }
            finally
            {
                webService?.StopAsync().GetAwaiter().GetResult();
                validator?.Dispose();
                settings?.Dispose();
            }
        }

        private bool IsShutdownRequested()
        {
            lock (_lifecycleLock)
                return _shutdownRequested;
        }
    }
}
