using MHServerEmu.Commands;
using MHServerEmu.Commands.Attributes;
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
        public void Initialize_SkipsInjectedGroupsUntilTheyAreSupplied()
        {
            CommandManager withoutSuppliedAccountGroup = new();

            Assert.True(withoutSuppliedAccountGroup.Initialize());
            Assert.False(withoutSuppliedAccountGroup.TryGetCommandGroup("account", out _));

            AccountCommands accountCommands = new(CreateAccountManager());
            CommandManager withSuppliedAccountGroup = new();

            Assert.True(withSuppliedAccountGroup.Initialize(accountCommands));
            Assert.True(withSuppliedAccountGroup.TryGetCommandGroup("account", out CommandGroup registeredAccountCommands));
            Assert.Same(accountCommands, registeredAccountCommands);
            Assert.True(withSuppliedAccountGroup.TryGetCommandGroup("help", out _));
            Assert.False(withSuppliedAccountGroup.Initialize());
        }

        [Fact]
        public void Initialize_DuplicateSuppliedGroups_RollsBackAndCanRetry()
        {
            CommandManager commandManager = new();

            Assert.Throws<InvalidOperationException>(() => commandManager.Initialize(new DuplicateCommandGroup(), new DuplicateCommandGroup()));
            Assert.Equal(0, commandManager.RegisteredGroupCount);
            Assert.True(commandManager.Initialize());
        }

        private static AccountManager CreateAccountManager()
        {
            return new(JsonDBManager.Instance, JsonDBManager.Instance, PersistenceCapabilities.Json, new TestAccountSecurityNotifier());
        }

        [CommandGroup("duplicate")]
        private sealed class DuplicateCommandGroup : CommandGroup { }

        private sealed class TestAccountSecurityNotifier : IAccountSecurityNotifier
        {
            public void Notify(ulong accountId, AccountSecurityChangeType changeType) { }
        }
    }
}
