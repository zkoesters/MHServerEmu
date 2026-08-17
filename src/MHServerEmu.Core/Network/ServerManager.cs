using System.Globalization;
using System.Text;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.System.Time;

namespace MHServerEmu.Core.Network
{
    // Services start in this order and are shut down in reverse order.
    public enum GameServiceType
    {
        GameInstance,
        Leaderboard,
        PlayerManager,
        GroupingManager,
        Frontend,
        WebFrontend,
        NumServiceTypes
    }

    public enum GameServiceState
    {
        Created,
        Starting,
        Running,
        ShuttingDown,
        Shutdown,
    }

    public enum ServerManagerState
    {
        Created,
        Starting,
        Running,
        ShuttingDown,
        Shutdown,
    }

    /// <summary>
    /// Manages <see cref="IGameService"/> instances and routes <see cref="IGameServiceMessage"/> instances between them.
    /// </summary>
    public class ServerManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly IGameService[] _services = new IGameService[(int)GameServiceType.NumServiceTypes];
        private readonly Thread[] _serviceThreads = new Thread[(int)GameServiceType.NumServiceTypes];
        private readonly bool[] _startedServices = new bool[(int)GameServiceType.NumServiceTypes];
        private readonly TaskCompletionSource<Exception>[] _serviceFaults = new TaskCompletionSource<Exception>[(int)GameServiceType.NumServiceTypes];
        private readonly TaskCompletionSource<Exception> _fault = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lifecycleLock = new();

        private ServerManagerState _state = ServerManagerState.Created;

        public static ServerManager Instance { get; } = new();

        public TimeSpan StartupTime { get; private set; }

        public ServerManager() { }

        public Task<Exception> WaitForFaultAsync() => _fault.Task;

        /// <summary>
        /// Initializes the <see cref="ServerManager"/> instance.
        /// </summary>
        public void Initialize()
        {
            StartupTime = Clock.UnixTime;
        }

        /// <summary>
        /// Registers an <see cref="IGameService"/> for the specified <see cref="GameServiceType"/>.
        /// </summary>
        public void RegisterGameService(IGameService service, GameServiceType serviceType)
        {
            ArgumentNullException.ThrowIfNull(service);

            int index = (int)serviceType;

            if (index < 0 || index >= _services.Length)
                throw new ArgumentOutOfRangeException($"Invalid service type [{serviceType}].");

            if (_services[index] != null)
                throw new InvalidOperationException($"Service for type [{serviceType}] is already registered.");

            _services[index] = service;

            Logger.Info($"Registered service for type [{serviceType}]");
        }

        /// <summary>
        /// Unregisters the current <see cref="IGameService"/> for the specified <see cref="GameServiceType"/>.
        /// </summary>
        public void UnregisterGameService(GameServiceType serviceType)
        {
            int index = (int)serviceType;

            if (index < 0 || index >= _services.Length)
                throw new ArgumentOutOfRangeException($"Invalid service type [{serviceType}].");

            if (_services[index] == null)
                throw new InvalidOperationException($"No registered service for type [{serviceType}].");

            _services[index] = null;

            Logger.Info($"Unregistered service for type {serviceType}");
        }

        /// <summary>
        /// Returns the registered <see cref="IGameService"/> for the specified <see cref="GameServiceType"/>. Returns <see langword="null"/> if not registered.
        /// </summary>
        public IGameService GetGameService(GameServiceType serviceType)
        {
            int index = (int)serviceType;

            if (index < 0 || index >= _services.Length)
                throw new ArgumentOutOfRangeException($"Invalid service type [{serviceType}].");

            return _services[index];
        }

        /// <summary>
        /// Routes the provided <typeparamref name="T"/> instance to the <see cref="IGameService"/> registered for the specified <see cref="GameServiceType"/>.
        /// </summary>
        public bool SendMessageToService<T>(GameServiceType serviceType, in T message) where T: struct, IGameServiceMessage
        {
            int index = (int)serviceType;

            if (index < 0 || index >= _services.Length)
                throw new ArgumentOutOfRangeException($"Invalid service type [{serviceType}].");

            IGameService service = _services[index];

            if (service == null)
                return Logger.WarnReturn(false, $"RouteMessage(): No service is registered for type [{serviceType}]");

            switch (service.State)
            {
                // Treat Starting and ShuttingDown same as Running because services can exchange confirmations during startup / shutdown.
                case GameServiceState.Starting:
                case GameServiceState.Running:
                case GameServiceState.ShuttingDown:
                    break;

                default:
                    Logger.Warn($"Unexpected state [{service.State}] for type [{serviceType}] when sending [{typeof(T).Name}]");
                    break;
            }

            service.ReceiveServiceMessage(message);

            return true;
        }

        /// <summary>
        /// Runs all registered <see cref="IGameService"/> instances.
        /// </summary>
        public bool RunServices() => RunServices(CancellationToken.None);

        public bool RunServices(CancellationToken cancellationToken)
        {
            lock (_lifecycleLock)
            {
                if (_state != ServerManagerState.Created)
                    throw new InvalidOperationException($"Invalid state {_state} when starting the ServerManager.");

                _state = ServerManagerState.Starting;
            }

            using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(ShutdownServices);

            if (cancellationToken.IsCancellationRequested)
                return false;

            for (int i = 0; i < _services.Length; i++)
            {
                GameServiceType serviceType = (GameServiceType)i;

                IGameService service = _services[i];
                if (service == null)
                    continue;

                if (service.State != GameServiceState.Created)
                    throw new InvalidOperationException($"Invalid service state [{service.State}] for type [{serviceType}].");

                if (_serviceThreads[i] != null)
                    throw new InvalidOperationException($"Service thread already created for type [{serviceType}].");

                Logger.Info($"Starting service for type [{serviceType}]...");

                int serviceIndex = i;
                TaskCompletionSource<Exception> serviceFault = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _serviceFaults[serviceIndex] = serviceFault;
                _serviceThreads[serviceIndex] = new(() => RunService(service, IsStopping, exception => ReportServiceFault(serviceIndex, exception))) { Name = $"Service [{serviceType}]", IsBackground = true, CurrentCulture = CultureInfo.InvariantCulture };
                _serviceThreads[i].Start();

                while (service.State != GameServiceState.Running && serviceFault.Task.IsCompleted == false && _fault.Task.IsCompleted == false && IsStarting())
                    Thread.Sleep(1);

                if (service.State != GameServiceState.Running || serviceFault.Task.IsCompleted || _fault.Task.IsCompleted)
                {
                    Exception exception = serviceFault.Task.IsCompleted
                        ? serviceFault.Task.GetAwaiter().GetResult()
                        : _fault.Task.IsCompleted
                            ? _fault.Task.GetAwaiter().GetResult()
                        : new OperationCanceledException($"Service for type [{serviceType}] stopped while starting.");
                    Logger.ErrorException(exception, $"Service for type [{serviceType}] failed during startup");
                    ShutdownServices();
                    return false;
                }

                _startedServices[i] = true;

                Logger.Info($"Service for type [{serviceType}] started");                
            }

            lock (_lifecycleLock)
            {
                if (_state != ServerManagerState.Starting)
                    return false;

                _state = ServerManagerState.Running;
            }
            return true;
        }

        /// <summary>
        /// Shuts down all running <see cref="IGameService"/> instances.
        /// </summary>
        public void ShutdownServices()
        {
            lock (_lifecycleLock)
            {
                if (_state == ServerManagerState.ShuttingDown || _state == ServerManagerState.Shutdown)
                    return;

                _state = ServerManagerState.ShuttingDown;
            }

            // Shut down services in reverse
            for (int i = _services.Length - 1; i >= 0; i--)
            {
                GameServiceType serviceType = (GameServiceType)i;

                IGameService service = _services[i];
                if (service == null || (_startedServices[i] == false && _serviceThreads[i] == null))
                    continue;

                if (service.State == GameServiceState.Shutdown)
                    continue;

                Logger.Info($"Shutting down service for type [{serviceType}]...");

                _services[i].Shutdown();

                while (service.State != GameServiceState.Shutdown && _serviceFaults[i]?.Task.IsCompleted == false)
                    Thread.Sleep(1);

                _serviceThreads[i] = null;
                _startedServices[i] = false;

                Logger.Info($"Service for type [{serviceType}] shut down");
            }

            Logger.Info("All services shut down");

            lock (_lifecycleLock)
                _state = ServerManagerState.Shutdown;
        }

        private bool IsStarting()
        {
            lock (_lifecycleLock)
                return _state == ServerManagerState.Starting;
        }

        private bool IsStopping()
        {
            lock (_lifecycleLock)
                return _state is ServerManagerState.ShuttingDown or ServerManagerState.Shutdown;
        }

        private static void RunService(IGameService service, Func<bool> isStopping, Action<Exception> reportFault)
        {
            try
            {
                service.Run();
                if (isStopping() == false)
                    reportFault(new InvalidOperationException("Service exited unexpectedly."));
            }
            catch (Exception exception)
            {
                reportFault(exception);
            }
        }

        private void ReportServiceFault(int index, Exception exception)
        {
            _serviceFaults[index].TrySetResult(exception);
            _fault.TrySetResult(exception);
        }

        /// <summary>
        /// Adds structured server status data to the provided dictionary.
        /// </summary>
        public void GetServerStatus(Dictionary<string, long> statusDict)
        {
            statusDict["StartupTime"] = (long)StartupTime.TotalSeconds;
            statusDict["CurrentTime"] = (long)Clock.UnixTime.TotalSeconds;

            for (int i = 0; i < _services.Length; i++)
                _services[i]?.GetStatus(statusDict);
        }

        /// <summary>
        /// Returns a <see cref="string"/> representing the current status of all running <see cref="IGameService"/> instances.
        /// </summary>
        public string GetServerStatusString()
        {
            using var statusDictHandle = DictionaryPool<string, long>.Instance.Get(out Dictionary<string, long> statusDict);
            GetServerStatus(statusDict);

            StringBuilder sb = new();

            foreach (var kvp in statusDict)
                sb.AppendLine($"{kvp.Key}: {kvp.Value}");

            return sb.ToString();
        }
    }
}
