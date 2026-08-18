# Game Instance Service Lifecycle Design

## Problem

`GameInstanceService.Run()` starts the game worker threads, sets its state to `Running`, and returns. `ServerManager` correctly treats a service method returning before shutdown as a failure, so server startup aborts even though the game worker threads were initialized successfully.

## Design

`GameInstanceService` will own the lifetime of the manager-created service thread. After initializing its game worker threads and entering `Running`, `Run()` will wait for a shutdown signal.

`Shutdown()` will preserve its current game-count warning, stop the game worker threads, set the service state to `Shutdown`, and signal `Run()` to return. This makes the return expected because `ServerManager` has already transitioned to shutdown.

`ServerManager` remains generic: any service that returns before shutdown continues to be reported as a runtime failure. No service-specific exception or interface capability is added.

## Tests

Add lifecycle coverage proving that a `GameInstanceService` can start without being reported as an unexpectedly exited service, and that an orderly server shutdown releases the service thread and completes. Existing tests for unexpected service returns remain unchanged to preserve the manager's runtime-failure behavior.

## Scope

This change is limited to the game-instance service lifecycle and its tests. It does not alter persistence providers, command registration, game worker scheduling, or startup/shutdown behavior for other services.
