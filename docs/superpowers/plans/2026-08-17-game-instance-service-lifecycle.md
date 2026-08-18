# Game Instance Service Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the game-instance service alive on its manager-owned thread until orderly shutdown, so production startup does not report it as unexpectedly exited.

**Architecture:** `GameInstanceService.Run()` will initialize its game workers, transition to `Running`, and wait on a private shutdown signal. `Shutdown()` will stop game workers, transition to `Shutdown`, and release that wait so `ServerManager` observes a normal return only after it has begun shutdown. `GameThread` will accept a stop request while starting, and `GameThreadManager` will iterate a snapshot when removing workers, preventing collection mutation during shutdown.

**Tech Stack:** C#, .NET 8, xUnit, `ManualResetEventSlim`.

---

## File Structure

- Create: `src/MHServerEmu.Games.Tests/Network/InstanceManagement/GameInstanceServiceTests.cs` - verifies the concrete game-instance service remains active after startup and returns only after shutdown.
- Modify: `src/MHServerEmu.Games/Network/InstanceManagement/GameInstanceService.cs:8-44` - owns the service-thread lifetime with a shutdown signal and stops its game workers.
- Modify: `src/MHServerEmu.Games/Network/InstanceManagement/GameThreadManager.cs:61-80` - safely removes worker threads while iterating the managed-thread collection.
- Modify: `src/MHServerEmu.Games/Network/InstanceManagement/GameThread.cs:80-109` - honors a stop request that arrives before a worker completes startup.

### Task 1: Add Lifecycle Regression Coverage

**Files:**
- Create: `src/MHServerEmu.Games.Tests/Network/InstanceManagement/GameInstanceServiceTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
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
            }

            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(GameServiceState.Shutdown, service.State);
        }

        private static async Task WaitForStateAsync(GameInstanceService service, GameServiceState expectedState)
        {
            await Task.Run(() => SpinWait.SpinUntil(() => service.State == expectedState, TimeSpan.FromSeconds(5)));
            Assert.Equal(expectedState, service.State);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Games.Tests/MHServerEmu.Games.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~GameInstanceServiceTests.Run_WaitsForShutdownAfterStartingWorkers
```

Expected: FAIL at `Assert.False(run.IsCompleted)` because `GameInstanceService.Run()` returns immediately after setting `Running`.

- [ ] **Step 3: Commit the failing regression test**

```bash
git add src/MHServerEmu.Games.Tests/Network/InstanceManagement/GameInstanceServiceTests.cs
git commit -m "test(games): cover game instance service lifetime"
```

### Task 2: Own the Service Thread Until Shutdown

**Files:**
- Modify: `src/MHServerEmu.Games/Network/InstanceManagement/GameInstanceService.cs:8-44`
- Modify: `src/MHServerEmu.Games/Network/InstanceManagement/GameThreadManager.cs:61-80`
- Modify: `src/MHServerEmu.Games/Network/InstanceManagement/GameThread.cs:80-109`
- Test: `src/MHServerEmu.Games.Tests/Network/InstanceManagement/GameInstanceServiceTests.cs`

- [ ] **Step 1: Add a shutdown signal and wait in `Run()`**

Add the field below beside the existing logger field:

```csharp
private readonly ManualResetEventSlim _shutdown = new();
```

Replace `Run()` and `Shutdown()` with:

```csharp
public void Run()
{
    GameThreadManager.Initialize();

    State = GameServiceState.Running;
    _shutdown.Wait();
}

public void Shutdown()
{
    // All game instances should be shut down by the PlayerManager before we get here
    int gameCount = GameManager.GameCount;
    if (gameCount > 0)
        Logger.Warn($"Shutdown(): {gameCount} games are still running");

    GameThreadManager.Shutdown();
    State = GameServiceState.Shutdown;
    _shutdown.Set();
}
```

- [ ] **Step 2: Iterate a snapshot when stopping game workers**

In `GameThreadManager.Shutdown()`, replace the loop that iterates `_gameThreads.Values` with:

```csharp
foreach (GameThread thread in _gameThreads.Values.ToArray())
{
    thread.Stop();
    RemoveThread(thread.Id);
}
```

This permits `RemoveThread()` to mutate `_gameThreads` without invalidating the active enumeration.

- [ ] **Step 3: Honor stop requests that arrive during worker startup**

Replace `GameThread.Stop()` with:

```csharp
public bool Stop()
{
    if (State is not (GameThreadState.Starting or GameThreadState.Running))
        return Logger.WarnReturn(false, $"Stop(): Invalid state [{State}] for GameThread [{this}]");

    State = GameThreadState.Stopping;
    return true;
}
```

Replace the state transition at the start of `GameThread.Run()` with:

```csharp
if (State is not (GameThreadState.Starting or GameThreadState.Stopping))
    throw new InvalidOperationException($"Invalid state [{State}] for GameThread [{this}].");

InitializeThreadLocalStorage();

if (State == GameThreadState.Starting)
    State = GameThreadState.Running;
```

If shutdown changed the state to `Stopping` before worker initialization completed, the existing update loop is skipped and the worker transitions directly to `Stopped`.

- [ ] **Step 4: Run the focused regression test**

Run:

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Games.Tests/MHServerEmu.Games.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~GameInstanceServiceTests.Run_WaitsForShutdownAfterStartingWorkers
```

Expected: PASS with one test passing; the service stays running until `Shutdown()` and then exits cleanly.

- [ ] **Step 5: Run affected lifecycle tests**

Run:

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Core.Tests/MHServerEmu.Core.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~ServerManagerLifecycleTests
```

Expected: PASS. The existing stopped-service test continues to prove that a non-shutdown `Run()` return is still a `ServerManager` fault.

- [ ] **Step 6: Build the solution**

Run:

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet build MHServerEmu.sln --configuration Release -p:Platform=x64 --no-restore
```

Expected: Build succeeded with zero warnings and zero errors.

- [ ] **Step 7: Commit the lifecycle fix**

```bash
git add src/MHServerEmu.Games/Network/InstanceManagement/GameInstanceService.cs src/MHServerEmu.Games/Network/InstanceManagement/GameThread.cs src/MHServerEmu.Games/Network/InstanceManagement/GameThreadManager.cs
git commit -m "fix(games): keep game instance service running"
```
