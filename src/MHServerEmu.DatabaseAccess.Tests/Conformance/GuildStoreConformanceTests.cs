using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess.Tests.Conformance
{
    internal static class GuildStoreConformanceTests
    {
        internal static void AssertCreateLoadAndMutate(IGuildStore store, DBGuild guild, DBGuildMember leader)
        {
            Assert.Equal(GuildStoreResult.Success, store.CreateGuild(guild, leader));

            List<DBGuild> loaded = new();
            Assert.True(store.LoadGuilds(loaded));
            DBGuild persisted = Assert.Single(loaded);
            Assert.Equal(guild.Id, persisted.Id);
            Assert.Equal(guild.Name, persisted.Name);
            Assert.Single(persisted.Members);
            Assert.Equal(leader.PlayerDbGuid, persisted.Members.Single().PlayerDbGuid);

            Assert.Equal(GuildStoreResult.Success, store.ChangeGuildName(guild, "Renamed"));
            Assert.Equal(GuildStoreResult.Success, store.ChangeGuildMotd(guild, "Welcome"));
            Assert.Equal("Renamed", guild.Name);
            Assert.Equal("Welcome", guild.Motd);
        }
    }
}
