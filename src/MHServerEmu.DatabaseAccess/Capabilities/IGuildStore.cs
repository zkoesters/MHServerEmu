using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess
{
    public interface IGuildStore
    {
        public bool LoadGuilds(List<DBGuild> guilds);
        public bool SaveGuild(DBGuild guild);
        public bool DeleteGuild(DBGuild guild);
        public bool SaveGuildMember(DBGuildMember guildMember);
        public bool DeleteGuildMember(DBGuildMember guildMember);
    }
}
