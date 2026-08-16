using System.Text.Json.Serialization;

namespace MHServerEmu.DatabaseAccess.Models
{
    public class DBGuild
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Motd { get; set; }
        public long CreatorDbGuid { get; set; }
        public long CreationTime { get; set; }

        [JsonIgnore]
        public long PersistenceRevision { get; set; } = 0;
        [JsonIgnore]
        public PersistenceState PersistenceState { get; set; } = PersistenceState.Clean;
        [JsonIgnore]
        public DateTime? CreatedAtUtc { get; set; }
        [JsonIgnore]
        public DateTime? UpdatedAtUtc { get; set; }

        // CreatorDbGuid and CreationTime are just additional metadata for tracking/moderation.

        public List<DBGuildMember> Members { get; init; } = new();

        public DBGuild(long id, string name, string motd, long creatorDbGuid, long creationTime)
        {
            Id = id;
            Name = name;
            Motd = motd;
            CreatorDbGuid = creatorDbGuid;
            CreationTime = creationTime;
        }

        public override string ToString()
        {
            return $"{Name} ({Id})";
        }
    }
}
