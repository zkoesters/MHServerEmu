using MHServerEmu.Commands;
using MHServerEmu.Commands.Implementations;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.Frontend;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.GameData.LiveTuning;
using MHServerEmu.Games.Network.InstanceManagement;
using MHServerEmu.Grouping;
using MHServerEmu.Leaderboards;
using MHServerEmu.PlayerManagement;
using MHServerEmu.PlayerManagement.Players;
using MHServerEmu.WebFrontend;

namespace MHServerEmu
{
    internal sealed class ServerStartupDependencies
    {
        public ServerStartupDependencies(
            Func<Action<PersistenceFatalFailure>, CancellationToken, Task<PersistenceRuntime>> createPersistenceAsync,
            Func<bool> initializeSystems,
            Action<ServerManager, PersistenceServices, AccountManager> registerServices,
            Func<Task<string>> readConsoleLineAsync,
            Action notifyServicesStarted = null,
            ConfigManager configManager = null)
        {
            CreatePersistenceAsync = createPersistenceAsync ?? throw new ArgumentNullException(nameof(createPersistenceAsync));
            InitializeSystems = initializeSystems ?? throw new ArgumentNullException(nameof(initializeSystems));
            RegisterServices = registerServices ?? throw new ArgumentNullException(nameof(registerServices));
            ReadConsoleLineAsync = readConsoleLineAsync ?? throw new ArgumentNullException(nameof(readConsoleLineAsync));
            NotifyServicesStarted = notifyServicesStarted ?? (() => { });
            ConfigManager = configManager ?? ConfigManager.Instance;
        }

        public Func<Action<PersistenceFatalFailure>, CancellationToken, Task<PersistenceRuntime>> CreatePersistenceAsync { get; }
        public Func<bool> InitializeSystems { get; }
        public Action<ServerManager, PersistenceServices, AccountManager> RegisterServices { get; }
        public Func<Task<string>> ReadConsoleLineAsync { get; }
        public Action NotifyServicesStarted { get; }
        public ConfigManager ConfigManager { get; }

        public static ServerStartupDependencies CreateProduction()
        {
            return new(
                Persistence.PersistenceComposition.CreateAsync,
                ServerApp.InitializeProductionSystems,
                RegisterProductionServices,
                Console.In.ReadLineAsync,
                () => LiveTuningEventScheduler.Instance.SendEventMessageTextToGroupingManager(),
                ConfigManager.Instance);
        }

        private static void RegisterProductionServices(ServerManager serverManager, PersistenceServices persistence, AccountManager accountManager)
        {
            LeaderboardService leaderboardService = new(persistence.Players, persistence.Leaderboards);
            CommandManager.Instance.Initialize(new AccountCommands(accountManager), new LeaderboardsCommands(leaderboardService.Administration));
            CommandManager.Instance.SetClientOutput(new FrontendClientChatOutput());
            ICommandParser.Instance = new CommandParser();

            serverManager.RegisterGameService(new GameInstanceService(), GameServiceType.GameInstance);
            serverManager.RegisterGameService(leaderboardService, GameServiceType.Leaderboard);
            serverManager.RegisterGameService(new PlayerManagerService(accountManager, persistence.Players, persistence.Guilds, persistence.Capabilities), GameServiceType.PlayerManager);
            serverManager.RegisterGameService(new GroupingManagerService(), GameServiceType.GroupingManager);
            serverManager.RegisterGameService(new FrontendServer(), GameServiceType.Frontend);
            serverManager.RegisterGameService(new WebFrontendService(), GameServiceType.WebFrontend);
        }
    }
}
