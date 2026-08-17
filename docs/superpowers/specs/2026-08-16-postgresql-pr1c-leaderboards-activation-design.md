# PostgreSQL PR 1C Leaderboards and Activation Design

Status: Approved

Date: 2026-08-16

Implementation baseline: `release/1.0.1` at `0dc90592c71c15d82b263503ddbf496c5c71baf4`

## 1. Summary

PR 1C completes Phase 1 persistence on the `1.0.1` baseline. It adds a
provider-neutral leaderboard capability, PostgreSQL leaderboard schema and
store, atomic leaderboard lifecycle and reward operations, explicit runtime
ownership, and complete PostgreSQL application activation.

JSON and SQLite deployments continue to use the existing SQLite leaderboard
file. PostgreSQL deployments use PostgreSQL for every persistent subsystem and
never create, open, or probe the SQLite leaderboard file. The existing SQLite
leaderboard schema and file format remain unchanged.

This pull request also changes application startup to await PostgreSQL
bootstrap, owns provider lifetime through shutdown, turns fatal fencing and
uncertain global writes into controlled shutdown, removes leaderboard
persistence singletons, and adds complete setup and operational documentation.

The current-upstream forward-port rehearsal remains a separate post-merge
Phase 1 completion exercise.

## 2. Goals

- Persist complete `1.0.1` leaderboard state in PostgreSQL.
- Give SQLite and PostgreSQL equivalent compound lifecycle and reward
  semantics without changing SQLite schema version 1.
- Remove `LeaderboardDatabase.Instance` and
  `SQLiteLeaderboardDBManager.Instance` from production code.
- Make `LeaderboardService` own its runtime database and reward manager.
- Commit schedule, lifecycle, score, visibility, and reward changes before
  mutating or publishing runtime state.
- Enable `[Persistence] Provider=PostgreSQL` only after all four capabilities
  are composed from PostgreSQL.
- Initialize, fence, migrate, and own PostgreSQL before starting game systems.
- Drain service writes before provider disposal.
- Preserve full `ulong` score ordering semantics across providers.
- Verify PostgreSQL behavior against versions 16 and 17.

## 3. Non-Goals

- Changing the existing SQLite leaderboard schema, `user_version`, file name,
  or historical rows.
- Importing JSON or SQLite data into PostgreSQL.
- Exactly-once game item delivery across a process crash.
- Multiple active server processes against one database.
- Runtime provider switching or fallback after a PostgreSQL failure.
- Removing unrelated game-data, time, pooled-collection, or message-routing
  statics.
- Adding production Docker artifacts, a backup scheduler, or a public database
  health endpoint.
- Performing the current-upstream forward-port rehearsal in this pull request.

## 4. Provider Architecture

### 4.1 Leaderboard Capability

Add `ILeaderboardStore` to `PersistenceServices`. The interface exposes domain
operations rather than the current SQLite CRUD surface. Public methods remain
synchronous and provider-neutral.

The common result enum is:

```text
LeaderboardStoreResult
  Success
  NotFound
  Conflict
  StaleState
  InvalidData
  Failed
  OutcomeUncertain
```

Reward finalization uses:

```text
RewardFinalizationResult
  Finalized
  AlreadyFinalized
  NotFound
  Failed
  OutcomeUncertain
```

The capability contains these operations:

- `Initialize`: open or validate provider-specific leaderboard persistence.
- `ReconcileSchedule`: atomically reconcile desired definitions, instances,
  enabled state, active pointers, and meta mappings; return a committed
  `LeaderboardSnapshot` used to build runtime state.
- `LoadEntries`: load all opaque entries for one instance without provider
  ordering assumptions.
- `LoadInstance` and `LoadVisibleInstances`: load archived instance metadata by
  exact ownership key or bounded pagination without requiring startup to
  materialize all visible history.
- `ActivateInstance`: compare the expected active pointer and `Created` state,
  then persist `Created -> Active` before runtime activation is published.
- `SaveScoreBatch`: atomically upsert one instance-scoped dirty batch while the
  expected instance state remains active.
- `ExpireInstance`: save final entries and move an expected active instance to
  expired in one transaction.
- `RotateActiveInstance`: verify the expected current pointer/state, create the
  next instance and meta mappings, and change the pointer/states atomically.
- `MaintainVisibility`: update visibility and return the bounded normal archive
  window from the same transaction snapshot.
- `GenerateRewards`: conflict-safely insert one instance's complete reward set
  and commit its expected lifecycle transition atomically.
- `GetPendingRewards`: return pending rows for one participant.
- `FinalizeReward`: conditionally finalize the complete leaderboard, instance,
  participant protocol key.

Request DTOs carry explicit expected pointers and states. They never expose a
connection, transaction, SQL fragment, provider exception, or generic entity
repository. All identifier and timestamp fields retain their existing signed
`long` database bit patterns.

The exact contract shape is:

```text
LeaderboardStoreResult Initialize()
LeaderboardStoreResult ReconcileSchedule(
    LeaderboardReconciliation request,
    out LeaderboardSnapshot snapshot)
LeaderboardStoreResult LoadEntries(
    long instanceId,
    out IReadOnlyList<DBLeaderboardEntry> entries)
LeaderboardStoreResult LoadInstance(
    long leaderboardId,
    long instanceId,
    out DBLeaderboardInstance instance)
LeaderboardStoreResult LoadVisibleInstances(
    long leaderboardId,
    long beforeInstanceId,
    int limit,
    out IReadOnlyList<DBLeaderboardInstance> instances)
LeaderboardStoreResult ActivateInstance(LeaderboardActivation request)
LeaderboardStoreResult SaveScoreBatch(LeaderboardScoreBatch request)
LeaderboardStoreResult ExpireInstance(LeaderboardExpiration request)
LeaderboardStoreResult RotateActiveInstance(
    LeaderboardRotation request,
    out DBLeaderboardInstance committedInstance)
LeaderboardStoreResult MaintainVisibility(
    LeaderboardVisibilityRequest request,
    out LeaderboardVisibilitySnapshot snapshot)
LeaderboardStoreResult GenerateRewards(LeaderboardRewardGeneration request)
LeaderboardStoreResult GetPendingRewards(
    long participantId,
    out IReadOnlyList<DBRewardEntry> rewards)
RewardFinalizationResult FinalizeReward(
    LeaderboardRewardKey key,
    long rewardedDate)
```

`LeaderboardReconciliation` contains the validated desired definitions,
initial-instance specifications, and complete meta topology.
`LeaderboardSnapshot` contains committed definitions, active/nonterminal
instances, the bounded normal archive window, and required meta mappings.
`LeaderboardActivation` contains leaderboard ID,
expected active instance ID, target instance ID, and expected `Created` state.
`LeaderboardScoreBatch` contains leaderboard ID, instance ID, expected `Active`
state, and entries. `LeaderboardExpiration` adds the expected active pointer
and complete final dirty batch. `LeaderboardRotation` contains expected pointer
and previous state, the complete next instance, requested previous/next states,
and new meta mappings. Visibility input contains per-leaderboard archive limit
and current time. Reward generation contains the expected pointer/state and
complete reward set. `LeaderboardRewardKey` contains all three protocol IDs.

On `Success`, every output collection is non-null and may be empty. A missing
instance makes `LoadEntries` or `LoadInstance` return `NotFound`; an existing
instance without entries returns `Success` with an empty collection. Visible
pagination requires limit `1..100`; `beforeInstanceId = 0` starts at the newest
instance, and later pages use the last returned ID as an exclusive cursor. No
pending rewards is `Success` with an empty collection. Outputs on failure are
empty, never partial.

Allowed results are explicit:

- Read/initialize operations: `Success`, `NotFound` where specified,
  `InvalidData`, or `Failed`.
- Reconciliation: `Success`, `Conflict`, `InvalidData`, `Failed`, or
  `OutcomeUncertain`.
- Activation, score, expiration, rotation, and visibility writes: `Success`,
  `NotFound`, `StaleState`, `Conflict`, `InvalidData`, `Failed`, or
  `OutcomeUncertain`.
- Reward generation: the same write results.
- Finalization: only the dedicated finalization results.

Reconciliation and score upserts are naturally idempotent. Activation returns
`Success` when the same pointer is already active. Rotation returns `Success`
and the committed instance when the requested next instance and topology are
already present exactly; mismatched replay is `Conflict`. Expiration replay is
`Success` only when the instance is already expired and every submitted final
entry matches persisted data; otherwise it is `StaleState` or `Conflict`.

### 4.2 Provider Implementations

JSON and SQLite profiles construct an instance-owned SQLite leaderboard store
over the existing configured file. Constructing the adapter does not open the
file. `LeaderboardService` calls `Initialize` only when leaderboards are
enabled, preserving the existing disabled-mode behavior.

PostgreSQL constructs `PostgreSQLLeaderboardStore` over the process-wide data
source and `PostgreSQLStoreExecutor`. PostgreSQL provider bootstrap runs all
migrations even when runtime leaderboards are disabled, but no SQLite
leaderboard path is accessed.

Every PostgreSQL read and write uses one configured absolute operation
deadline. Writes also validate the writer fence. PostgreSQL uses set-based
score and reward writes. SQLite may retain per-row Dapper statements when the
complete operation uses one transaction.

### 4.3 Runtime Ownership

`LeaderboardService` owns one non-static `LeaderboardDatabase` and one
non-static `LeaderboardRewardManager`. It receives `ILeaderboardStore`, a
narrow player-name resolver, and runtime configuration through its constructor.

`LeaderboardDatabase` owns the runtime leaderboard collection. `Leaderboard`,
`LeaderboardInstance`, `LeaderboardEntry`, and the reward manager receive only
the store, name resolver, and callbacks they need. Constructors perform no
hidden persistence.

`LeaderboardsCommands` receives a service-owned `ILeaderboardAdministration`
facade and is registered as a preconstructed command group beside
`AccountCommands`. Reflection continues to create only parameterless command
groups.

The administration facade exposes:

```text
LeaderboardAdminResult ReloadSchedule()
LeaderboardAdminResult TryGetInstance(long instanceId, out LeaderboardInstanceSummary summary)
LeaderboardAdminResult TryGetLeaderboard(long leaderboardId, out LeaderboardSummary summary)
LeaderboardAdminResult GetLeaderboards(out IReadOnlyList<LeaderboardSummary> summaries)
```

`LeaderboardAdminResult` is `Success`, `Unavailable`, `NotFound`, `InvalidData`,
or `Failed`. Summary DTOs are immutable scalar snapshots and contain no runtime
object or store reference. The facade posts a request to the leaderboard
service mailbox and synchronously waits up to five seconds. Timeout returns
`Unavailable`; commands never access service-owned dictionaries from the
frontend thread. Reload parsing and reconciliation run on the leaderboard
service thread, and command text is formatted from returned DTOs.

Before schedule reload reconciles, the service drains every queued score batch
and force-saves all dirty entries for definitions that reconciliation may
disable or replace. Any safe score-save failure aborts reload and leaves the
existing schedule/runtime state active. An uncertain save follows fatal
shutdown policy. Reconciliation therefore never terminalizes an instance with
accepted dirty scores.

Production source guards reject:

- `LeaderboardDatabase.Instance`
- `SQLiteLeaderboardDBManager.Instance`
- Direct concrete leaderboard-store access outside composition and provider
  projects.

## 5. Persistence Lifetime and Composition

### 5.1 Owned Runtime

Persistence composition returns `PersistenceRuntime`, an `IAsyncDisposable`
owner containing one immutable `PersistenceServices` bundle. Services remain
capabilities only; the runtime owner controls provider lifetime.

JSON and SQLite use a no-op lifetime implementation after disposing any owned
adapter resources. PostgreSQL uses a public facade in the PostgreSQL project.
The facade constructs and starts the internal provider, then exposes account,
player, guild, and leaderboard interfaces without exposing `NpgsqlDataSource`,
the store executor, writer owner, or migration runner.

The PostgreSQL facade accepts a provider-neutral fatal callback containing only
a stable failure code and operation name. It never exposes raw provider
exceptions or connection details.

### 5.2 Selection

`PersistenceComposition.CreateAsync` performs canonical provider selection:

- `Json`: JSON account/player/guild plus owned SQLite leaderboard adapter.
- `SQLite`: SQLite account/player/guild plus owned SQLite leaderboard adapter.
- `PostgreSQL`: one PostgreSQL facade supplying all four capabilities.

Unknown/conflicting configuration fails before provider construction.
PostgreSQL validation, connection, migration, or writer-lock failure fails
startup. No path falls back to JSON or SQLite.

`PersistenceCapabilities.PostgreSQL` remains the source of authentication and
durable-security behavior. `EnablePersistence=false` still suppresses player
saves only; required reads and provider initialization still occur.

## 6. PostgreSQL Migration 0003

Add immutable embedded migration `0003_LeaderboardPersistence.sql`. Do not
modify migrations `0001` or `0002` after their merged release.

All objects are schema-qualified under `mhserveremu`. Index identifiers follow
PostgreSQL syntax and remain unqualified while target tables are qualified.

### 6.1 `leaderboard`

Columns:

- `leaderboard_id bigint PRIMARY KEY`
- `prototype_name text NOT NULL`
- `active_instance_id bigint NULL`
- `is_enabled boolean NOT NULL`
- `start_time bigint NOT NULL`
- `max_reset_count integer NOT NULL CHECK (max_reset_count >= 0)`

After the instance table exists, add a deferrable, initially deferred composite
foreign key from `(leaderboard_id, active_instance_id)` to
`leaderboard_instance(leaderboard_id, instance_id)`. A null active instance is
valid before first activation and maps to runtime ID zero.

### 6.2 `leaderboard_instance`

Columns:

- `instance_id bigint PRIMARY KEY`
- `leaderboard_id bigint NOT NULL`
- `state smallint NOT NULL CHECK (state BETWEEN 0 AND 5)`
- `activation_date bigint NOT NULL`
- `visible boolean NOT NULL`

It has `UNIQUE (leaderboard_id, instance_id)` and a cascading leaderboard
foreign key. Index `(leaderboard_id, state, visible, instance_id)` supports
lifecycle and archive selection.

### 6.3 `leaderboard_entry`

Columns:

- `instance_id bigint NOT NULL`
- `participant_id bigint NOT NULL`
- `score bigint NOT NULL`
- `high_score bigint NOT NULL`
- `rule_states bytea NOT NULL`

Primary key is `(instance_id, participant_id)`. Instance deletion cascades.
Index `(instance_id, high_score)` supports instance access but is not the
authoritative unsigned ranking order.

Rule-state bytes remain opaque and byte-for-byte compatible with the existing
little-endian serialized format. The persistence layer does not parse or
rewrite them.

### 6.4 `leaderboard_meta_entry`

Columns:

- `leaderboard_id bigint NOT NULL`
- `instance_id bigint NOT NULL`
- `sub_leaderboard_id bigint NOT NULL`
- `sub_instance_id bigint NOT NULL`

Primary key is `(leaderboard_id, instance_id, sub_leaderboard_id)`. Composite
foreign keys enforce parent and sub-instance ownership and cascade when either
owned instance is deleted. An index covers parent traversal.

### 6.5 `leaderboard_reward`

Columns:

- `leaderboard_id bigint NOT NULL`
- `instance_id bigint NOT NULL`
- `participant_id bigint NOT NULL`
- `reward_id bigint NOT NULL`
- `rank integer NOT NULL CHECK (rank > 0)`
- `creation_date bigint NOT NULL`
- `rewarded_date bigint NULL`

Primary key is `(leaderboard_id, instance_id, participant_id)`. A composite
foreign key enforces leaderboard/instance ownership and cascades with the
instance. A partial index on participant ID where `rewarded_date IS NULL`
supports pending delivery. SQL null maps to runtime rewarded-date zero.

## 7. Schedule Reconciliation

Schedule JSON and live prototype data define the desired schedule. Database
state remains authoritative for active instances, scores, visibility, and
rewards.

Startup resolves desired schedule deterministically:

- If the configured schedule file exists, parse it as the operator-owned
  schedule.
- If it does not exist, generate the same canonical file from current live,
  public leaderboard prototypes before contacting the store. This is the
  fresh-install path for every provider; it does not depend on detecting a new
  SQLite file.
- Live prototypes define valid leaderboard IDs, prototype names, leaderboard
  kinds, and meta/sub-leaderboard topology.
- JSON defines enabled state, start time, and reset count. It cannot invent an
  ID absent from live prototypes or change topology.
- Duplicate IDs, duplicate prototype names, invalid meta references, invalid
  dates/reset values, malformed JSON, or an unwritable generated file fail
  leaderboard-service startup before persistence.
- A live prototype absent from an existing schedule is not implicitly added.
  A new definition is created only when a valid schedule entry introduces it.

`LeaderboardDatabase` parses and validates the complete desired schedule before
calling the store. The reconciliation transaction:

1. Loads current definitions, relevant instances, and meta mappings.
2. Inserts schedule definitions absent from the database.
3. Creates each new definition's initial instance, active pointer, and meta
   mappings atomically.
4. Updates enabled, start-time, and reset-count values for existing rows.
5. Creates required instances for disabled-to-enabled transitions.
6. Marks database definitions absent from the desired schedule disabled without
   deleting history.
7. Repairs zero/missing activation dates as part of reconciliation rather than
   from runtime constructors.
8. Returns committed definitions, active/nonterminal instances, the bounded
   normal archive window, and required meta mappings as one snapshot.

A repeated reconciliation with unchanged input is a no-op. Conflict-safe
inserts allow startup retry but never overwrite scores or reward state.

Initial state is explicit. An enabled fresh definition receives one `Created`,
visible instance and points to it. A disabled fresh definition receives one
terminal `Rewarded`, invisible instance and points to it so application ID
sequencing remains deterministic.

Disabling an existing definition marks it disabled and moves its current
`Created`, `Active`, `Expired`, `Reward`, or `RewardsPending` instance to
`Rewarded`. It sets visibility false unless that instance has any reward row,
in which case reward-retention policy keeps it visible. No score or reward row
is deleted.

Re-enabling creates the next deterministic instance ID, state `Created`, sets
its activation date from the reconciled schedule, and moves the active pointer
to it in the same transaction. It never reuses a terminal instance.

Instance IDs use the existing provider-neutral algorithm. Treat IDs as unsigned
bit patterns. The initial ID is
`(leaderboardId & 0xFFFFFFFF00000000) | 1`. The next ID preserves those upper
32 bits and uses one plus the greatest persisted lower-32-bit value for that
leaderboard. Existing instances with different upper bits, a duplicate
candidate, or lower counter `0xFFFFFFFF` make reconciliation `InvalidData`;
wrapping to zero is forbidden. Reconciliation computes and returns the exact
committed ID so providers cannot choose different values.

Changing start time or reset count updates the definition but does not
retroactively change a nonzero activation date or current active cycle. The
next created instance uses the new schedule. A zero activation date is repaired
during reconciliation using the current definition; constructors never write
it.

For meta leaderboards, related definitions, instances, pointers, and mappings
are reconciled in one transaction. A missing or desynchronized sub-instance is
`InvalidData`; no partial topology commits.

## 8. Lifecycle and Score Semantics

### 8.1 Tick Ordering

Each leaderboard-service tick performs:

1. Drain queued score-update batches into runtime entries.
2. Persist due dirty score batches.
3. Evaluate and commit lifecycle transitions.
4. Process reward requests and confirmations.

This prevents a score batch already accepted by the service from being dropped
because expiration ran first.

### 8.2 Ranking

Runtime score and high-score values are `ulong`. SQLite and PostgreSQL retain
the existing signed `bigint` bit pattern. Stores load all rows for an instance
without relying on signed SQL ordering.

`Score` is the authoritative field for table ordering, percentile buckets,
rank, and reward selection, matching the current in-memory sorter. `HighScore`
remains persisted historical scoring-rule state and is not used to order a
loaded table.

Runtime code compares `unchecked((ulong)Score)` and applies the configured
ascending/descending rule. Equal scores sort by unsigned participant ID
ascending as the deterministic tie breaker. High-bit values therefore rank
identically in SQLite, PostgreSQL, and memory.

Equal scores receive the same competition rank for reward and percentile
evaluation. The first entry is rank 1; after a tie, the next different score
uses its one-based sorted position (`1, 1, 3`). Participant-ID ordering only
stabilizes presentation within the tie and never changes tied reward rank.

### 8.3 Score Batches

`SaveScoreBatch` verifies that the target instance remains in the expected
active state and writes the complete batch in one transaction. PostgreSQL uses
one set-based `INSERT ... ON CONFLICT DO UPDATE`; SQLite uses its existing
update/insert pattern inside one transaction.

Empty batches are successful no-ops. A failure leaves every entry's
`SaveRequired` flag set. Flags clear only after confirmed commit. An uncertain
result is fatal because the committed score set is unknown.

### 8.4 Expiration and Rotation

`ActivateInstance` compares the definition's active pointer, target instance
ID, and expected `Created` state, then commits `Created -> Active`. If the same
instance is already active it succeeds idempotently. A different pointer or
state is `StaleState`. Runtime activation and game notification occur only
after success.

`ExpireInstance` compares the expected active pointer and `Active` state, saves
the final dirty entries, and changes the instance to `Expired` atomically. Only
after success does runtime state change or publish expiration.

On replay, an already-expired instance whose persisted final entry set matches
the request returns `Success` even if a later rotation moved the active pointer.
The pointer predicate applies to the first transition only. A mismatched final
entry set is `Conflict`; an instance in another state is `StaleState`.

`RotateActiveInstance` compares the expected pointer and old-instance state,
creates the next instance and required meta mappings, applies requested old/new
states, and updates the pointer in one transaction. Stale requests return
`StaleState`; duplicate retries reconcile with the already committed state and
do not create an extra instance.

## 9. Visibility and Restart

Visibility maintenance updates empty/expired instance visibility and selects
the configured normal archive window inside one transaction or consistent
snapshot.

Normal archive retention follows configured count limits. By explicit PR 1C
policy, which intentionally strengthens the earlier SQLite visibility behavior,
an instance with any reward row, pending or finalized, keeps `visible = true`
beyond the normal archive window. This preserves delivery state and completed
history across restart; no reward-bearing instance is silently hidden.

Visibility does not imply eager materialization. The startup reconciliation
snapshot contains active/nonterminal instances and the configured normal archive
window only. Reward delivery reads `leaderboard_reward` directly by participant
and does not require loading its historical instance or entry table. Any older
reward-bearing instance, pending or finalized, remains visible in persistence
but is retrieved only through exact lookup or bounded
`LoadVisibleInstances` pagination.

On-demand archived instances and their entries use one least-recently-used
cache bounded to the configured archive-retention count, with a minimum of one.
Eviction drops only runtime metadata and entry objects; database visibility and
reward rows remain unchanged. The database history may grow with reward-bearing
instances by operator choice, but startup memory and response sizes remain
bounded.

Startup builds runtime objects only from the bounded committed snapshot and
loads entries for its active/nonterminal and normal-window instances. No
reward-bearing history is eagerly loaded merely for reward delivery, and no
singleton collection survives an in-process restart test.

## 10. Reward Semantics

### 10.1 Generation

Reward generation computes the complete participant reward set from the final
unsigned ranking. `GenerateRewards` verifies the expected leaderboard pointer
and `Expired` instance state, inserts rewards using the three-part primary key
with conflict-safe idempotency, and commits `Expired -> Rewarded` in the same
transaction. After commit, runtime may publish the existing `Reward`,
`RewardsPending`, and `Rewarded` notifications in order without treating the
intermediate values as separately durable states.

Repeating generation succeeds when existing rows match participant, reward ID,
and rank; it preserves each existing creation date rather than replacing it
with a retry timestamp. A conflicting reward ID or rank is `Conflict`. Runtime
publishes reward-related state only after commit.

The submitted reward set must contain unique participant keys. Duplicates are
`InvalidData`. Idempotent replay requires bidirectional set equality: persisted
and requested participant-key sets must match exactly, and every reward ID and
rank must match. Extra or missing persisted/requested rows are `Conflict`; the
store never treats a partial reward computation as successful.

### 10.2 Pending Delivery

`GetPendingRewards` returns rows where rewarded date is null. Pending rows
survive process and service restart.

Game-side item grant and database finalization cannot share a transaction. The
delivery contract remains at-least-once: a crash after item grant but before
confirmation may offer the same reward again. Exactly-once delivery is not
claimed.

### 10.3 Finalization

`FinalizeReward` addresses `(leaderboard_id, instance_id, participant_id)` and
executes a conditional update only when the rewarded timestamp is null.

- First confirmation returns `Finalized` and records the supplied game clock
  timestamp.
- A matching row already finalized returns `AlreadyFinalized` without changing
  its timestamp.
- A missing or mismatched protocol key returns `NotFound`.
- A safe failure retains the pending cache entry for retry.
- An uncertain outcome requests controlled shutdown; startup reconstructs the
  pending state from the database.

The reward manager removes an in-memory pending row only after `Finalized` or
`AlreadyFinalized`.

On a safe `Failed` finalization, the manager retains the confirmation and
retries after 1, 2, 4, 8, 16, then 30 seconds, remaining at the 30-second cap
until success or shutdown. There is no retry-count expiry. `NotFound` removes
the cache entry and records a safe warning because no durable row can be
finalized. A late or duplicate game confirmation always addresses the full key
and may call `FinalizeReward` even if the in-memory cache was reconstructed or
evicted.

Failure to send a pending reward response does not change the database or cache;
the game may request it again. Shutdown stops retry scheduling without marking
rows finalized. On restart, pending database rows remain deliverable, preserving
the documented at-least-once behavior. Retry iteration uses a stable work list
and never removes dictionary entries while enumerating them.

## 11. Runtime Startup and Shutdown

### 11.1 Async Startup

`Program.Main` becomes `static async Task Main`. `ServerApp.RunAsync` performs:

1. Validate single-run state and initialize logging/configuration.
2. Validate machine prerequisites.
3. Await persistence selection and bootstrap.
4. For PostgreSQL: connect, acquire exclusive writer ownership, migrate through
   `0003`, claim the durable fence, retain the shared lock, and start monitoring.
5. Initialize game-data and remaining non-database systems.
6. Construct account and leaderboard administration services.
7. Explicitly register command groups.
8. Construct/register services with all persistence capabilities.
9. Start services and enter the console/shutdown wait loop.

The application state distinguishes `Starting`, `Running`, `Stopping`, and
`Stopped`. A fatal callback during startup latches startup failure and cancels
remaining initialization; it does not call service shutdown before services
exist.

The running wait uses an asynchronous console-read task raced against an
internal shutdown signal. Fatal shutdown therefore exits the wait immediately;
it never depends on an operator pressing Enter to release `Console.ReadLine`.

### 11.2 Service Startup Failure

Service-thread initialization is wrapped so failure/exception is reported as a
faulted startup state. `ServerManager.RunServices` stops waiting when a service
faults and returns failure; it cannot wait forever for `Running`.

Partially started services shut down in reverse start order. Persistence is
disposed from the application `finally` path.

### 11.3 Controlled Fatal Shutdown

The provider and application use one thread-safe idempotent shutdown request.
These conditions request fatal shutdown exactly once:

- Writer-lock monitor loss.
- Writer-fence mismatch.
- Uncertain guild transition.
- Any uncertain leaderboard write.

Ordinary safe store failures do not switch providers or shut down the process.
They retain dirty/pending state and may retry on a later service tick.

An uncertain player save propagates `PlayerStoreResult.OutcomeUncertain` through
`PlayerHandle` to the player-manager mailbox. The mailbox disconnects that
client, does not enqueue another save, and discards the loaded aggregate after
disconnect. A later login must load a fresh aggregate from PostgreSQL.

Add exact provider-neutral contract
`AccountStoreResult ReconcileAccount(DBAccount account)`. It performs a bounded
read by immutable account ID, replaces only persisted account scalar/security
fields and metadata, and clears `PersistenceState.OutcomeUncertain` on success;
it does not replace the loaded player aggregate. Missing ID returns
`AccountNotFound`; safe read failure returns `Failed`.

PostgreSQL account mutation methods reject an uncertain object before SQL. The
next explicit account operation on that same object first attempts
reconciliation. If reconciliation fails, the operation returns database
failure and authentication/authorization for that object remains fail-closed.
Reconciliation never automatically replays the mutation whose outcome was
uncertain. Fresh email authentication queries create a new reconciled account
object.

SQLite implements reconciliation by loading account scalar fields by ID and
updating the supplied object after a successful read. JSON succeeds only for
its configured default account ID and copies the current default scalar fields.
For a clean object both providers may return `Success` as a no-op. Neither
provider changes its persistent format.

An uncertain account mutation changes no in-memory security version and emits
no notification. An uncertain guild write invokes the same global fatal
callback as an uncertain leaderboard write before returning its error result;
runtime guild state remains unpublished while shutdown begins.

### 11.4 Shutdown Order

Shutdown:

1. Prevents new console/client work.
2. Signals services to stop accepting new messages.
3. Drains queued leaderboard score updates.
4. Saves final active leaderboard/player state where outcome remains safe.
5. Shuts down services in reverse dependency order.
6. Disposes persistence runtime.
7. PostgreSQL disposes monitor, writer lock connection, then pooled data source.
8. Marks the application stopped.

The same cleanup path runs on normal shutdown, startup failure, and terminating
exception. Disposal is idempotent.

## 12. SQLite Compatibility

The SQLite leaderboard adapter is no longer singleton-owned but uses the same
file, initialization SQL, and `PRAGMA user_version = 1`.

PR 1C does not:

- Add or alter SQLite tables, columns, constraints, indexes, or migrations.
- Enable SQLite foreign keys globally.
- Rewrite existing rows or regenerate an existing file.
- Change SQLite null/zero sentinel mapping.

Compound behavior is implemented with transactions and expected-value
predicates over existing columns. Historical nullable values map through
existing model defaults or produce safe initialization failure; they are not
silently rewritten outside reconciliation.

JSON and SQLite profiles open the leaderboard file only when leaderboards are
enabled. PostgreSQL never accesses that path.

## 13. Failure Classification and Logging

Provider-neutral operations never expose SQLite, Dapper, Npgsql, connection,
transaction, or raw exception types.

- Normal absence is `NotFound`.
- Expected pointer/state mismatch is `StaleState`.
- Duplicate IDs or conflicting idempotent payloads are `Conflict`.
- Invalid topology, state, null required rule-state bytes, or ownership is
  `InvalidData`; persistence otherwise treats rule-state bytes as opaque.
- Timeout, network failure before commit, and unexpected safe rollback are
  `Failed`.
- Commit-stage connection loss is `OutcomeUncertain`.

PostgreSQL error objects contain only stable operation, safe IDs, and SQLSTATE.
Connection strings, SQL parameters, schedules, score values, reward payloads,
and rule-state bytes are not included in crash context or provider logs.

Startup logs provider type, PostgreSQL version, migration range/schema version,
pool limits, and durations. It never logs host, database, role, or credentials.

## 14. Configuration and Documentation

Canonical `[Persistence] Provider=PostgreSQL` becomes supported. Empty provider
and deprecated JSON selection retain current behavior.

`[Leaderboards] DatabaseFile` remains applicable only to JSON/SQLite profiles.
`ScheduleFile`, enabled state, autosave, and archive-retention settings remain
provider-neutral.

Replace documentation that says PostgreSQL is unavailable. Document:

- Fresh-install-only support and no JSON/SQLite importer.
- Database creation, runtime role, ownership, and fixed `mhserveremu` schema.
- Connection string exclusively in `ConfigOverride.ini`.
- `SSL Mode=VerifyFull` and trusted root certificate configuration.
- Unix `chmod 600` and Windows ACL guidance.
- Startup migration/checksum/no-downgrade policy.
- Single-writer rejection and restart-only provider selection.
- Consistent `pg_dump` backup command.
- Restore into a clean database with `pg_restore`.
- Role/schema ownership and migration-history verification after restore.
- Upgrade sequence: stop service, back up, deploy, migrate on startup, verify.

No production Dockerfile, Compose file, backup scheduler, or public health route
is added.

## 15. Verification

### 15.1 Provider-Neutral Conformance

Run the same SQLite/PostgreSQL scenarios for:

- Fresh reconciliation and no-op second reconciliation.
- Schedule additions, disable/enable, date/reset changes, and absent-definition
  disabling.
- Missing-file generation, malformed/duplicate schedule rejection, exact
  initial/next instance IDs, collision, and lower-counter overflow.
- Active rotation, stale duplicate rotation, expiration with final scores, and
  restart reconstruction.
- Score insert/update, empty and large batches, unsigned high-bit ordering, and
  deterministic presentation ties with shared competition reward rank.
- Schedule reload draining queued/dirty scores before disabling an instance.
- Meta mapping creation/load and invalid ownership rollback.
- Visibility limits and reward-based retention.
- Bounded startup snapshot, exact archived lookup, visible pagination, and LRU
  eviction while finalized-reward instances remain visible in persistence.
- Idempotent reward generation, pending retrieval, first/duplicate
  finalization, protocol-key mismatch, and restart persistence.
- Reward-generation duplicate-input rejection and exact bidirectional replay
  set comparison.
- Safe finalization retry backoff, late confirmation, send failure, shutdown
  persistence, and the documented grant-before-confirmation crash window.

SQLite golden tests assert schema `user_version = 1` and unchanged table/index
definitions.

### 15.2 PostgreSQL 16/17

Integration coverage includes:

- Migration `0003` ordering, checksum, idempotency, and every named constraint,
  FK, cascade, and index.
- Set-based score/reward batches and pool recovery after safe rollback.
- Connection loss before commit and during commit.
- Uncertain leaderboard write invoking fatal callback once.
- Fresh full-provider bootstrap and no-op restart.
- Second-runtime writer rejection and lock-loss fencing.
- Graceful shutdown disposing the provider only after service drain.

The existing CI matrix runs all tagged tests on PostgreSQL 16 and 17 and fails
if the administrative connection variable is absent or any test skips.

### 15.3 Runtime and Filesystem

Tests prove:

- JSON and SQLite profiles open the configured SQLite leaderboard file when
  enabled.
- PostgreSQL profiles never create, open, or probe the file.
- Leaderboard-disabled PostgreSQL still initializes and migrates PostgreSQL.
- Score dirty flags and runtime lifecycle state change only after commit.
- Reward cache removal follows confirmed conditional finalization.
- Shutdown drains queued score batches.
- Startup service faults terminate without a wait loop hang.
- Fatal callbacks during startup/running/stopping are handled exactly once.
- Account uncertain-state reconciliation does not replay the original mutation;
  player uncertain saves disconnect and require reload; uncertain guild writes
  request fatal shutdown.
- Source guards reject both removed leaderboard persistence singletons.
- Commands use the injected administration facade and marshal through the
  service mailbox with bounded timeout.

Full startup/restart/shutdown tests use synthetic schedules and database models;
they do not require proprietary game assets.

## 16. Acceptance Criteria

- PostgreSQL leaderboard lifecycle and reward conformance passes on versions 16
  and 17 with zero skips.
- PostgreSQL mode uses no SQLite persistence.
- JSON and SQLite leaderboard files remain format-compatible.
- Schedule, lifecycle, score, visibility, and reward operations are atomic at
  their documented boundaries.
- Runtime state is never published before required persistence commits.
- Pending and finalized reward rows retain associated archived instances.
- Full `ulong` score ordering is identical across providers and restart.
- Fresh startup, no-op restart, graceful shutdown, fencing, and second-runtime
  rejection pass.
- Normal `[Persistence] Provider=PostgreSQL` startup is enabled.
- Setup, TLS, backup, restore, and upgrade documentation is complete.
- The forward-port rehearsal remains explicitly tracked as the next separate
  Phase 1 completion activity.
