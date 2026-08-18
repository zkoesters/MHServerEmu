using System.Globalization;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Metrics;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.System.Time;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData.LiveTuning;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Network.InstanceManagement
{
    public enum GameThreadState
    {
        Created,
        Starting,
        Running,
        Stopping,
        Stopped,
    }

    /// <summary>
    /// Represents a worker thread that processes <see cref="Game"/> instances.
    /// </summary>
    public class GameThread
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly GameThreadManager _threadManager;
        private readonly object _threadLock = new();
        private readonly Action _initializeThreadLocalStorage;
        private readonly Func<uint, bool> _failStart;

        private Thread _thread = null;
        private bool _threadStarted;
        private int _state = (int)GameThreadState.Created;

        public uint Id { get; }
        public GameThreadState State { get => (GameThreadState)Volatile.Read(ref _state); private set => Volatile.Write(ref _state, (int)value); }

        /// <summary>
        /// Constructs a new <see cref="GameThread"/> instance.
        /// </summary>
        public GameThread(GameThreadManager threadManager, uint id)
            : this(threadManager, id, null)
        {
        }

        internal GameThread(GameThreadManager threadManager, uint id, Action initializeThreadLocalStorage)
            : this(threadManager, id, initializeThreadLocalStorage, null)
        {
        }

        internal GameThread(GameThreadManager threadManager, uint id, Action initializeThreadLocalStorage, Func<uint, bool> failStart)
        {
            _threadManager = threadManager;
            Id = id;
            _initializeThreadLocalStorage = initializeThreadLocalStorage;
            _failStart = failStart;
        }

        public override string ToString()
        {
            return $"Id={Id}, ManagedId={_thread?.ManagedThreadId}";
        }

        /// <summary>
        /// Starts a newly created <see cref="GameThread"/>.
        /// </summary>
        public bool Start()
        {
            lock (_threadLock)
            {
                if (State != GameThreadState.Created)
                    return Logger.WarnReturn(false, $"Start(): Invalid state [{State}] for GameThread [{this}]");

                State = GameThreadState.Starting;

                if (_thread != null)
                    throw new InvalidOperationException($"Existing C# thread [{_thread}] found.");

                _thread = new(Run)
                {
                    Name = $"GameThread {Id}",  // We don't have a managed id until we create the thread
                    IsBackground = true,
                    CurrentCulture = CultureInfo.InvariantCulture,
                    Priority = ThreadPriority.AboveNormal,
                };

                try
                {
                    if (_failStart?.Invoke(Id) == true)
                        throw new InvalidOperationException($"Start(): Failed to start GameThread [{this}]");

                    _thread.Start();
                    _threadStarted = true;
                    return true;
                }
                catch
                {
                    _thread = null;
                    State = GameThreadState.Stopped;
                    throw;
                }
            }
        }

        /// <summary>
        /// Stops a <see cref="GameThread"/> that is currently in the <see cref="GameThreadState.Running"/> state.
        /// </summary>
        public bool Stop()
        {
            lock (_threadLock)
            {
                if (State != GameThreadState.Starting && State != GameThreadState.Running)
                    return Logger.WarnReturn(false, $"Stop(): Invalid state [{State}] for GameThread [{this}]");

                State = GameThreadState.Stopping;
                return true;
            }
        }

        /// <summary>
        /// Waits for the underlying managed thread to stop.
        /// </summary>
        internal void WaitForStop()
        {
            Thread thread;
            lock (_threadLock)
                thread = _threadStarted ? _thread : null;

            thread?.Join();
        }

        /// <summary>
        /// Processes <see cref="Game"/> instances that need to be updated in a loop until this <see cref="GameThread"/> is stopped.
        /// </summary>
        private void Run()
        {
            try
            {
                if (State != GameThreadState.Starting && State != GameThreadState.Stopping)
                    throw new InvalidOperationException($"Invalid state [{State}] for GameThread [{this}].");

                if (_initializeThreadLocalStorage != null)
                    _initializeThreadLocalStorage();
                else
                    InitializeThreadLocalStorage();

                lock (_threadLock)
                {
                    if (State == GameThreadState.Starting)
                        State = GameThreadState.Running;
                }

                Logger.Info($"Worker thread [{this}] started");

                while (State == GameThreadState.Running)
                    UpdateGame();
            }
            finally
            {
                lock (_threadLock)
                {
                    State = GameThreadState.Stopped;
                    _thread = null;
                    _threadStarted = false;
                }

                Logger.Info($"Worker thread [{this}] stopped");
            }
        }

        /// <summary>
        /// Initializes thread-local storage used by this <see cref="GameThread"/>.
        /// </summary>
        /// <remarks>
        /// This needs to be called from the underlying managed <see cref="Thread"/>.
        /// </remarks>
        private void InitializeThreadLocalStorage()
        {
            CollectionPoolSettings.UseThreadLocalStorage = true;
            ObjectPoolManager.UseThreadLocalStorage = true;

            EntityDestroyListNodePool.Instance = new(Id);

            InitializeLiveTuning();
        }

        /// <summary>
        /// Initializes <see cref="LiveTuningData"/> for this <see cref="GameThread"/>.
        /// </summary>
        private bool InitializeLiveTuning()
        {
            LiveTuningData liveTuningData = LiveTuningData.Current;
            if (liveTuningData != null)
                return false;

            liveTuningData = new();
            LiveTuningManager.Instance.CopyLiveTuningData(liveTuningData);
            liveTuningData.GetLiveTuningUpdate();   // pre-generate update protobuf

            LiveTuningData.Current = liveTuningData;

            Logger.Info($"Initialized LiveTuningData for worker thread [{this}]");
            return true;
        }

        /// <summary>
        /// Updates the <see cref="Game"/> instance with the highest priority. Sleeps if no instances need to be updated.
        /// </summary>
        private void UpdateGame()
        {
            Game game = _threadManager.GetGameToUpdate();

            try
            {
                if (game != null)
                {
                    TimeSpan beforeUpdate = Clock.GameTime;

                    Game.Current = game;

                    game.Update();

                    Game.Current = null;

                    TimeSpan afterUpdate = Clock.GameTime;

                    game.LastProcessingTime = afterUpdate - beforeUpdate;
                    game.LastFrameTime = afterUpdate - game.LastUpdateEndTime;
                    game.LastUpdateEndTime = afterUpdate;
                }
                else
                {
                    // No game to process, wait until there is work to do
                    Thread.Sleep(1);
                }
            }
            catch (Exception e)
            {
                game.Shutdown(GameShutdownReason.GameInstanceCrash);
                HandleGameInstanceCrash(game, e);
            }

            // Enqueue the game instance for the next update if it's still running
            if (game != null)
            {
                if (game.State == GameState.Running || game.State == GameState.ShuttingDown)
                    _threadManager.EnqueueGameToUpdate(game);
                else
                    Logger.Info($"Game [{game}] is no longer running");
            }
        }

        /// <summary>
        /// Creates and saves a crash report for the provided <see cref="Game"/> instance.
        /// </summary>
        private void HandleGameInstanceCrash(Game game, Exception exception)
        {
#if DEBUG
            const string buildConfiguration = "Debug";
#elif RELEASE
            const string buildConfiguration = "Release";
#endif

            DateTime now = DateTime.Now;

            string crashReportDir = Path.Combine(FileHelper.ServerRoot, "CrashReports");
            if (Directory.Exists(crashReportDir) == false)
                Directory.CreateDirectory(crashReportDir);

            string crashReportFilePath = Path.Combine(crashReportDir, $"GameInstanceCrash_{now.ToString(FileHelper.FileNameDateFormat)}.txt");

            using (StreamWriter writer = new(crashReportFilePath))
            {
                writer.WriteLine(string.Format("Assembly Version: {0} | {1} UTC | {2}\n",
                    AssemblyHelper.GetAssemblyInformationalVersion(),
                    AssemblyHelper.ParseAssemblyBuildTime().ToString("yyyy.MM.dd HH:mm:ss"),
                    buildConfiguration));

                writer.WriteLine($"Local Server Time: {now:yyyy.MM.dd HH:mm:ss.fff}\n");

                writer.WriteLine($"Thread: [{this}]\n");

                writer.WriteLine($"Game: {game}\n");

                writer.WriteLine($"Exception:\n{exception}\n");

                writer.WriteLine("Active Regions:");
                foreach (Region region in game.RegionManager)
                    writer.WriteLine(region.ToString());
                writer.WriteLine();

                writer.WriteLine("Scheduled Event Pool:");
                writer.Write(game.GameEventScheduler.GetPoolReportString());
                writer.WriteLine();

                writer.WriteLine($"Server Status:\n{ServerManager.Instance.GetServerStatusString()}\n");
                writer.WriteLine($"Performance Metrics:\n{MetricsManager.Instance.GeneratePerformanceReport(MetricsReportFormat.PlainText)}\n");
            }

            Logger.ErrorException(exception, $"Game instance crashed, report saved to {crashReportFilePath}");
        }
    }
}
