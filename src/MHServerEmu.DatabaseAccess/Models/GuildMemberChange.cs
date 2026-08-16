namespace MHServerEmu.DatabaseAccess.Models
{
    public readonly record struct GuildMemberChange(long PlayerDbGuid, long? ExpectedMembership, long? NewMembership);
}
