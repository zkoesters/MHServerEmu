using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess
{
    public interface IGuildStore
    {
        public bool LoadGuilds(List<DBGuild> guilds);
        public GuildStoreResult CreateGuild(DBGuild guild, DBGuildMember creator);
        public GuildStoreResult ChangeGuildName(DBGuild guild, string name);
        public GuildStoreResult ChangeGuildMotd(DBGuild guild, string motd);
        public GuildStoreResult ApplyMembershipTransition(DBGuild guild, GuildMemberTransition transition);
        public GuildStoreResult DeleteGuild(DBGuild guild);
    }
}
