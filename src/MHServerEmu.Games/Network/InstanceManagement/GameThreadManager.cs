using MHServerEmu.Core.Logging;
using MHServerEmu.Core.System.Time;

namespace MHServerEmu.Games.Network.InstanceManagement
{
    /// <summary>
    /// Processes <see cref="Game"/> instances using a pool of <see cref="GameThread">GameThreads</see>.
    /// </summary>
    public class GameThreadManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly object _gameThreadsLock = new();
        private readonly Dictionary<uint, GameThread> _gameThreads = new();
        private readonly PriorityQueue<Game, TimeSpan> _gameUpdateQueue = new();

        private readonly GameInstanceService _gis;
        private readonly Action _initializeThreadLocalStorage;
        private readonly Action _beforeInitializeComplete;
        private readonly Func<uint, bool> _failStart;

        private bool _isInitialized = false;
        private uint _currentThreadId = 1;

        public int ThreadCount { get { lock (_gameThreadsLock) { return _gameThreads.Count; } } }
        public int GameCount { get { lock (_gameUpdateQueue) { return _gameUpdateQueue.Count; } } }

        /// <summary>
        /// Constructs a <see cref="GameThreadManager"/> for the provided <see cref="GameInstanceService"/>.
        /// </summary>
        public GameThreadManager(GameInstanceService gis)
            : this(gis, null)
        {
        }

        internal GameThreadManager(GameInstanceService gis, Action initializeThreadLocalStorage)
            : this(gis, initializeThreadLocalStorage, null, null)
        {
        }

        internal GameThreadManager(GameInstanceService gis, Action initializeThreadLocalStorage, Action beforeInitializeComplete, Func<uint, bool> failStart)
        {
            _gis = gis;
            _initializeThreadLocalStorage = initializeThreadLocalStorage;
            _beforeInitializeComplete = beforeInitializeComplete;
            _failStart = failStart;
        }

        /// <summary>
        /// Creates and starts the specified number of <see cref="GameThread"/> instances.
        /// </summary>
        public void Initialize()
        {
            bool initializationStarted = false;

            try
            {
                lock (_gameThreadsLock)
                {
                    if (_isInitialized)
                        throw new InvalidOperationException("GameThreadManager is already initialized.");

                    initializationStarted = true;

                    // Always have at least 1 runner thread
                    int numThreads = _gis.Config.NumWorkerThreads;

                    if (numThreads < 1)
                    {
                        Logger.Warn("Initialize(): numThreads < 1, defaulting to 1");
                        numThreads = 1;
                    }

                    for (int i = 0; i < numThreads; i++)
                    {
                        GameThread thread = CreateThread();
                        thread.Start();
                    }

                    _beforeInitializeComplete?.Invoke();
                    _isInitialized = true;
                }
            }
            catch
            {
                if (initializationStarted == false)
                    throw;

                GameThread[] gameThreads;

                lock (_gameThreadsLock)
                {
                    _isInitialized = false;
                    gameThreads = _gameThreads.Values.ToArray();
                }

                StopAndRemoveThreads(gameThreads);
                throw;
            }
        }

        /// <summary>
        /// Stops and removes all <see cref="GameThread"/> instances managed by this <see cref="GameThreadManager"/>.
        /// </summary>
        public void Shutdown()
        {
            GameThread[] gameThreads;

            lock (_gameThreadsLock)
            {
                if (_isInitialized == false)
                    return;

                _isInitialized = false;
                gameThreads = _gameThreads.Values.ToArray();
            }

            // There should be no running games by the time this gets shut down
            lock (_gameUpdateQueue)
            {
                int gameCount = _gameUpdateQueue.Count;

                if (gameCount > 0)
                    Logger.Warn($"Shutdown(): {gameCount} games still need updating");
            }

            StopAndRemoveThreads(gameThreads);
        }

        /// <summary>
        /// Enqueues a <see cref="Game"/> instance to be processed by a <see cref="GameThread"/>.
        /// </summary>
        public void EnqueueGameToUpdate(Game game)
        {
            lock (_gameUpdateQueue)
                _gameUpdateQueue.Enqueue(game, game.NextUpdateTime);
        }

        /// <summary>
        /// Retrieves a <see cref="Game"/> instance that is ready to be updated. Returns <see langword="null"/> if no game needs processing.
        /// </summary>
        public Game GetGameToUpdate()
        {
            TimeSpan now = Clock.GameTime;

            lock (_gameUpdateQueue)
            {
                if (_gameUpdateQueue.TryPeek(out Game game, out TimeSpan updateTime))
                {
                    if (now >= updateTime)
                        return _gameUpdateQueue.Dequeue();
                }
            }

            return null;
        }

        /// <summary>
        /// Constructs a new <see cref="GameThread"/> instance.
        /// </summary>
        private GameThread CreateThread()
        {
            GameThread thread = new(this, _currentThreadId++, _initializeThreadLocalStorage, _failStart);
            _gameThreads.Add(thread.Id, thread);

            Logger.Trace($"Created GameThread {thread.Id}");
            return thread;
        }

        /// <summary>
        /// Removes the <see cref="GameThread"/> with the specified id.
        /// </summary>
        private bool RemoveThread(uint threadId)
        {
            lock (_gameThreadsLock)
            {
                if (_gameThreads.Remove(threadId) == false)
                    return Logger.WarnReturn(false, $"RemoveThread(): Failed to remove GameThread {threadId}");
            }

            Logger.Trace($"Removed GameThread {threadId}");
            return false;
        }

        private void StopAndRemoveThreads(GameThread[] gameThreads)
        {
            foreach (GameThread thread in gameThreads)
            {
                if (thread.State == GameThreadState.Starting || thread.State == GameThreadState.Running)
                    thread.Stop();
            }

            foreach (GameThread thread in gameThreads)
                thread.WaitForStop();

            foreach (GameThread thread in gameThreads)
                RemoveThread(thread.Id);
        }
    }
}
