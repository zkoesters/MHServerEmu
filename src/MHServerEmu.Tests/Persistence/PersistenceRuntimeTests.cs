using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.SQLite;

namespace MHServerEmu.Tests.Persistence
{
    public class PersistenceRuntimeTests
    {
        [Fact]
        public async Task DisposeAsync_DisposesOwnedLifetimeOnce()
        {
            int disposalCount = 0;
            PersistenceRuntime runtime = new(CreateServices(), () =>
            {
                disposalCount++;
                return ValueTask.CompletedTask;
            });

            await runtime.DisposeAsync();
            await runtime.DisposeAsync();

            Assert.Equal(1, disposalCount);
        }

        [Fact]
        public async Task DisposeAsync_ConcurrentCallersAwaitTheSameCleanup()
        {
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int disposalCount = 0;
            PersistenceRuntime runtime = new(CreateServices(), async () =>
            {
                disposalCount++;
                started.SetResult();
                await release.Task;
            });

            Task first = runtime.DisposeAsync().AsTask();
            await started.Task;
            Task second = runtime.DisposeAsync().AsTask();

            Assert.False(second.IsCompleted);
            release.SetResult();
            await Task.WhenAll(first, second);

            Assert.Equal(1, disposalCount);
        }

        [Fact]
        public async Task DisposeAsync_FailedCleanupCanBeRetried()
        {
            int attempts = 0;
            PersistenceRuntime runtime = new(CreateServices(), () =>
            {
                attempts++;
                return attempts == 1
                    ? ValueTask.FromException(new InvalidOperationException("cleanup failed"))
                    : ValueTask.CompletedTask;
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.DisposeAsync().AsTask());
            await runtime.DisposeAsync();

            Assert.Equal(2, attempts);
        }

        private static PersistenceServices CreateServices()
        {
            JsonDBManager manager = JsonDBManager.Instance;
            return new PersistenceServices(manager, manager, manager, new SQLiteLeaderboardDBManager("unused.db"), PersistenceCapabilities.Json);
        }
    }
}
