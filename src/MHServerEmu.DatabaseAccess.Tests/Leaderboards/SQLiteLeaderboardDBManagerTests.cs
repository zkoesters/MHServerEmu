using MHServerEmu.DatabaseAccess.SQLite;

namespace MHServerEmu.DatabaseAccess.Tests.Leaderboards
{
    public sealed class SQLiteLeaderboardDBManagerTests : LeaderboardDBManagerContract
    {
        protected override ILeaderboardDBManager CreateManager(string databasePath)
        {
            return new SQLiteLeaderboardDBManager(databasePath);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void Constructor_BlankPath_Throws(string path)
        {
            Assert.Throws<ArgumentException>(() => new SQLiteLeaderboardDBManager(path));
        }
    }
}
