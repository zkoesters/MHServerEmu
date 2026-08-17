using System.Globalization;
using System.Runtime.InteropServices;
using MHServerEmu.Commands;
using MHServerEmu.Commands.Implementations;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Logging.Targets;
using MHServerEmu.Core.Metrics;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.Frontend;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.LiveTuning;
using MHServerEmu.Games.MTXStore;
using MHServerEmu.Games.Network.InstanceManagement;
using MHServerEmu.Grouping;
using MHServerEmu.Leaderboards;
using MHServerEmu.PlayerManagement;
using MHServerEmu.PlayerManagement.Players;
using MHServerEmu.Persistence;
using MHServerEmu.WebFrontend;

namespace MHServerEmu
{
#if OS_WINDOWS
    // Default precision for thread sleep timing on Windows is 1000/64=15.625 ms.
    // This is not precise enough for our needs, so we request higher resolution timing.
    // According to MS docs, this should be per-process as of Windows 10 2004.
    // https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod
    // Also see this for more context:
    // https://randomascii.wordpress.com/2020/10/04/windows-timer-resolution-the-great-rule-change/
    internal static class WinMM
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern int TimeBeginPeriod(int uPeriod);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        public static extern int TimeEndPeriod(int uPeriod);
    }
#endif

    public class ServerApp
    {
        private enum State
        {
            Starting,
            Running,
            Stopping,
            Stopped,
        }

#if DEBUG
        public const string BuildConfiguration = "Debug";
#elif RELEASE
        public const string BuildConfiguration = "Release";
#endif

        public static readonly string VersionInfo = $"Version {AssemblyHelper.GetAssemblyInformationalVersion()} | {AssemblyHelper.ParseAssemblyBuildTime():yyyy.MM.dd HH:mm:ss} UTC | {BuildConfiguration}";

        private static readonly Logger Logger = LogManager.CreateLogger();
        private State _state = State.Stopped;
        private readonly ServerStartupDependencies _startupDependencies;
        private readonly ServerManager _serverManager;
        private readonly TaskCompletionSource<bool> _shutdownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PersistenceServices _persistence;
        private PersistenceRuntime _persistenceRuntime;
        private AccountManager _accountManager;
        private int _shutdownRequested;

        public static ServerApp Instance { get; } = new();
        public DateTime StartupTime { get; private set; }

        private ServerApp()
            : this(ServerStartupDependencies.CreateProduction(), ServerManager.Instance)
        {
        }

        internal ServerApp(ServerStartupDependencies startupDependencies, ServerManager serverManager)
        {
            _startupDependencies = startupDependencies ?? throw new ArgumentNullException(nameof(startupDependencies));
            _serverManager = serverManager ?? throw new ArgumentNullException(nameof(serverManager));
        }

        public async Task RunAsync()
        {
            if (_state != State.Stopped)
                throw new InvalidOperationException();
            _state = State.Starting;

#if OS_WINDOWS
            WinMM.TimeBeginPeriod(1);
#endif

            StartupTime = DateTime.Now;
            AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

            Console.ForegroundColor = ConsoleColor.Yellow;
            PrintBanner();
            PrintVersionInfo();
            Console.ResetColor();
            InitLoggers();
            Logger.Info("MHServerEmu starting...");
            MetricsManager.Instance.Initialize();

            try
            {
                if (BitConverter.IsLittleEndian == false)
                {
                    Logger.Fatal("This computer's architecture uses big-endian byte order, which is not compatible with MHServerEmu.");
                    return;
                }

                _persistenceRuntime = await _startupDependencies.CreatePersistenceAsync(RequestFatalShutdown, CancellationToken.None);
                _persistence = _persistenceRuntime.Services;
                if (Volatile.Read(ref _shutdownRequested) != 0 || _startupDependencies.InitializeSystems() == false)
                    return;

                _accountManager = new(_persistence.Accounts, _persistence.Players, _persistence.Capabilities, new ServerAccountSecurityNotifier());
                _serverManager.Initialize();
                _startupDependencies.RegisterServices(_serverManager, _persistence, _accountManager);
                if (_serverManager.RunServices() == false)
                    return;

                _startupDependencies.NotifyServicesStarted();
                _state = State.Running;
                Logger.Info("Type '!commands' for a list of available commands");
                while (_state == State.Running)
                {
                    Task<string> readTask = _startupDependencies.ReadConsoleLineAsync();
                    if (await Task.WhenAny(readTask, _shutdownSignal.Task) != readTask)
                        break;

                    string input = await readTask;
                    if (input == null || _state != State.Running)
                        break;

                    CommandManager.Instance.TryParse(input);
                }
            }
            catch (Exception exception)
            {
                Logger.FatalException(exception, "MHServerEmu startup failed.");
            }
            finally
            {
                _state = State.Stopping;
                _serverManager.ShutdownServices();
                if (_persistenceRuntime != null)
                    await _persistenceRuntime.DisposeAsync();
                _state = State.Stopped;

#if OS_WINDOWS
                WinMM.TimeEndPeriod(1);
#endif
            }
        }

        /// <summary>
        /// Shuts down all services and exits the application.
        /// </summary>
        public void Shutdown()
        {
            RequestShutdown();
        }

        internal void RequestFatalShutdown(PersistenceFatalFailure failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            Logger.Fatal($"Persistence requested controlled shutdown: {failure.Code} during {failure.Operation}.");
            RequestShutdown();
        }

        private void RequestShutdown()
        {
            if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
                _shutdownSignal.TrySetResult(true);
        }

        /// <summary>
        /// Prints a fancy ASCII banner to console.
        /// </summary>
        private void PrintBanner()
        {
            Console.WriteLine(@"  __  __ _    _  _____                          ______                 ");
            Console.WriteLine(@" |  \/  | |  | |/ ____|                        |  ____|                ");
            Console.WriteLine(@" | \  / | |__| | (___   ___ _ ____   _____ _ __| |__   _ __ ___  _   _ ");
            Console.WriteLine(@" | |\/| |  __  |\___ \ / _ \ '__\ \ / / _ \ '__|  __| | '_ ` _ \| | | |");
            Console.WriteLine(@" | |  | | |  | |____) |  __/ |   \ V /  __/ |  | |____| | | | | | |_| |");
            Console.WriteLine(@" |_|  |_|_|  |_|_____/ \___|_|    \_/ \___|_|  |______|_| |_| |_|\__,_|");
            Console.WriteLine();
        }

        /// <summary>
        /// Prints formatted version info to console.
        /// </summary>
        private void PrintVersionInfo()
        {
            Console.WriteLine($"\t{VersionInfo}");
            Console.WriteLine();
        }

        /// <summary>
        /// Handles unhandled exceptions.
        /// </summary>
        private void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception;

            if (e.IsTerminating)
            {
                DateTime now = DateTime.Now;

                string crashReportDir = Path.Combine(FileHelper.ServerRoot, "CrashReports");
                if (Directory.Exists(crashReportDir) == false)
                    Directory.CreateDirectory(crashReportDir);

                string crashReportFilePath = Path.Combine(crashReportDir, $"ServerCrash_{now.ToString(FileHelper.FileNameDateFormat)}.txt");

                using (StreamWriter writer = new(crashReportFilePath))
                {
                    writer.WriteLine($"{VersionInfo}\n");
                    writer.WriteLine($"Local Server Time: {now:yyyy.MM.dd HH:mm:ss.fff}\n");
                    writer.WriteLine($"Exception:\n{exception}\n");
                    writer.WriteLine($"Server Status:\n{ServerManager.Instance.GetServerStatusString()}\n");
                    writer.WriteLine($"Performance Metrics:\n{MetricsManager.Instance.GeneratePerformanceReport(MetricsReportFormat.PlainText)}\n");
                }

                Logger.FatalException(exception, $"MHServerEmu terminating because of unhandled exception, report saved to {crashReportFilePath}");
                RequestShutdown();
            }
            else
            {
                Logger.ErrorException(exception, "Caught unhandled exception.");
            }

        }

        /// <summary>
        /// Initializes log targets.
        /// </summary>
        private void InitLoggers()
        {
            var config = ConfigManager.Instance.GetConfig<LoggingConfig>();

            LogManager.Enabled = config.EnableLogging;

            // Attach console log target
            if (config.EnableConsole)
            {
                ConsoleTarget target = new(config.GetConsoleSettings());
                LogManager.AttachTarget(target);
            }

            // Attach file log target
            if (config.EnableFile)
            {
                FileTarget target = new(config.GetFileSettings(), $"MHServerEmu_{StartupTime.ToString(FileHelper.FileNameDateFormat)}", config.FileSplitOutput, false);
                LogManager.AttachTarget(target);
            }

            if (config.SynchronousMode)
                Logger.Debug($"Synchronous logging enabled");
        }

        /// <summary>
        /// Initializes systems needed to run the servers.
        /// </summary>
        internal static bool InitializeProductionSystems()
        {
            // LiveTuningManager uses data from LiveTuningEventScheduler initialization,
            // and LiveTuningEventScheduler needs GameDatabase to be initialized to get TimeZone from GlobalsPrototype.
            bool initialized = PakFileSystem.Instance.Initialize()
                && ProtocolDispatchTable.Instance.Initialize()
                && GameDatabase.IsInitialized
                && LiveTuningEventScheduler.Instance.Initialize()
                && LiveTuningManager.Instance.Initialize()
                && CatalogManager.Instance.Initialize();

            return initialized;
        }
    }
}
