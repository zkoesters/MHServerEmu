using MHServerEmu.Commands;
using MHServerEmu.Commands.Implementations;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.PlayerManagement.Players;

namespace MHServerEmu.Tests
{
    public class CommandManagerInitializationTests
    {
        [Fact]
        public void Initialize_RegistersSuppliedAccountGroupBeforeParameterlessGroups()
        {
            AccountManager accountManager = new(JsonDBManager.Instance, JsonDBManager.Instance, PersistenceCapabilities.Json, new TestAccountSecurityNotifier());
            AccountCommands accountCommands = new(accountManager);
            CommandManager commandManager = new();

            Assert.Equal(0, commandManager.RegisteredGroupCount);
            Assert.True(commandManager.Initialize(accountCommands));
            Assert.True(commandManager.TryGetCommandGroup("account", out CommandGroup registeredAccountCommands));
            Assert.Same(accountCommands, registeredAccountCommands);
            Assert.True(commandManager.TryGetCommandGroup("help", out _));
            Assert.False(commandManager.Initialize());
        }

        private sealed class TestAccountSecurityNotifier : IAccountSecurityNotifier
        {
            public void Notify(ulong accountId, AccountSecurityChangeType changeType) { }
        }
    }
}
