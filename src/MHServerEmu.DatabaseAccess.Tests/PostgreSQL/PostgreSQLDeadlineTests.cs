using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.PostgreSQL.Migrations;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    public class PostgreSQLDeadlineTests
    {
        [Fact]
        public async Task Remaining_DecreasesAndClampsToZero()
        {
            PostgreSQLOperationDeadline deadline = new(TimeSpan.FromMilliseconds(40));
            TimeSpan initial = deadline.Remaining;

            await Task.Delay(70);

            Assert.True(deadline.Remaining < initial);
            Assert.Equal(TimeSpan.Zero, deadline.Remaining);
        }

        [Fact]
        public async Task CreateCancellationSource_CancelsAtDeadline()
        {
            PostgreSQLOperationDeadline deadline = new(TimeSpan.FromMilliseconds(30));
            using CancellationTokenSource cancellationSource = deadline.CreateCancellationSource();

            await Task.Delay(100);

            Assert.True(cancellationSource.IsCancellationRequested);
        }

        [Fact]
        public void CreateCancellationSource_ExpiredDeadline_ThrowsTimeoutException()
        {
            PostgreSQLOperationDeadline deadline = new(TimeSpan.FromMilliseconds(1));
            Thread.Sleep(20);

            Assert.Throws<TimeoutException>(() => deadline.CreateCancellationSource());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Constructor_NonPositiveTimeout_Throws(int milliseconds)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSQLOperationDeadline(TimeSpan.FromMilliseconds(milliseconds)));
        }

        [Fact]
        public void RemainingCommandTimeoutSeconds_UsesCeilingWithMinimumOne()
        {
            PostgreSQLOperationDeadline deadline = new(TimeSpan.FromMilliseconds(10));

            Assert.Equal(1, deadline.RemainingCommandTimeoutSeconds);
        }

        [Theory]
        [InlineData(100, 200, "migration_timeout")]
        [InlineData(200, 100, "migration_lock_timeout")]
        public void GetAdvisoryLockTimeoutCode_UsesTheEarlierDeadline(int migrationMilliseconds, int migrationLockMilliseconds, string expectedCode)
        {
            string code = PostgreSQLMigrationRunner.GetAdvisoryLockTimeoutCode(TimeSpan.FromMilliseconds(migrationMilliseconds), TimeSpan.FromMilliseconds(migrationLockMilliseconds));

            Assert.Equal(expectedCode, code);
        }
    }
}
