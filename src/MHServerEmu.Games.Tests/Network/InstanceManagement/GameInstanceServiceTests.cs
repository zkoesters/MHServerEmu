using MHServerEmu.Core.Network;
using MHServerEmu.Games.Network.InstanceManagement;

namespace MHServerEmu.Games.Tests.Network.InstanceManagement
{
    public class GameInstanceServiceTests
    {
        [Fact]
        public async Task Run_WaitsForShutdownAfterStartingWorkers()
        {
            GameInstanceService service = new();
            Task run = Task.Run(service.Run);

            try
            {
                await WaitForStateAsync(service, GameServiceState.Running);
                Assert.False(run.IsCompleted);
            }
            finally
            {
                service.Shutdown();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.Equal(GameServiceState.Shutdown, service.State);
        }

        private static async Task WaitForStateAsync(GameInstanceService service, GameServiceState expectedState)
        {
            await Task.Run(() => SpinWait.SpinUntil(() => service.State == expectedState, TimeSpan.FromSeconds(5)));
            Assert.Equal(expectedState, service.State);
        }
    }
}
