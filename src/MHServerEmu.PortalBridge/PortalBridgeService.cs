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
        private readonly ManualResetEventSlim _shutdownEvent = new(false);

        private WebService _webService;
        private HmacRequestValidator _requestValidator;

        public GameServiceState State { get; private set; } = GameServiceState.Created;
        public bool IsAvailable { get => _webService?.IsRunning == true; }

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
            State = GameServiceState.Starting;
            TryStartListener();
            State = GameServiceState.Running;
            _shutdownEvent.Wait();

            _webService?.Stop();
            _requestValidator?.Dispose();
            State = GameServiceState.Shutdown;
        }

        public void Shutdown()
        {
            if (State != GameServiceState.Running)
                return;

            State = GameServiceState.ShuttingDown;
            _webService?.Stop();
            _shutdownEvent.Set();
        }

        public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage
        {
            Logger.Warn($"ReceiveServiceMessage(): Unexpected message type {typeof(T).Name}");
        }

        public void GetStatus(Dictionary<string, long> statusDict)
        {
            statusDict["PortalBridgeAvailable"] = IsAvailable ? 1 : 0;
            statusDict["PortalBridgeHandledRequests"] = _webService?.HandledRequests ?? 0;
        }

        private void TryStartListener()
        {
            if (_config.Enabled == false)
            {
                Logger.Info("PortalBridge is disabled");
                return;
            }

            PortalBridgeSettings settings = null;
            HmacRequestValidator validator = null;
            WebService webService = null;
            try
            {
                if (_config.TryCreateSettings(out settings, out string error) == false)
                {
                    Logger.Warn($"PortalBridge configuration is invalid: {error}");
                    return;
                }

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

                _requestValidator = validator;
                _webService = webService;
                validator = null;
                webService = null;
            }
            catch (Exception exception)
            {
                Logger.Error($"PortalBridge listener failed to start: {exception.GetType().Name}");
            }
            finally
            {
                webService?.Stop();
                validator?.Dispose();
                settings?.Dispose();
            }
        }
    }
}
