using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess.Tests.Models
{
    public class GuildMemberTransitionTests
    {
        [Fact]
        public void Constructor_RejectsNullChanges()
        {
            Assert.Throws<ArgumentNullException>(() => new GuildMemberTransition(1, 0, (GuildMemberChange[])null));
        }

        [Fact]
        public void Constructor_RejectsZeroChanges()
        {
            Assert.Throws<ArgumentException>(() => new GuildMemberTransition(1, 0));
        }

        [Fact]
        public void Constructor_RejectsThreeChanges()
        {
            Assert.Throws<ArgumentException>(() => new GuildMemberTransition(1, 0,
                new GuildMemberChange(1, null, 1),
                new GuildMemberChange(2, null, 1),
                new GuildMemberChange(3, null, 1)));
        }

        [Fact]
        public void Constructor_RejectsDuplicatePlayerChanges()
        {
            Assert.Throws<ArgumentException>(() => new GuildMemberTransition(1, 0,
                new GuildMemberChange(1, null, 1),
                new GuildMemberChange(1, 1, null)));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(4)]
        public void Constructor_RejectsMembershipOutsideSupportedRange(long membership)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GuildMemberTransition(1, 0,
                new GuildMemberChange(1, membership, null)));
        }

        [Fact]
        public void Constructor_RejectsZeroMembership()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GuildMemberTransition(1, 0,
                new GuildMemberChange(1, null, 0)));
        }
    }
}
