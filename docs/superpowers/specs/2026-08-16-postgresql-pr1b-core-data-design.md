# PostgreSQL PR 1B Core Data Design

Status: Approved

Date: 2026-08-16

Implementation baseline: `release/1.0.1` at `abac9fa255fa487edc47bdbcfee76a1668984dc3`

## 1. Summary

PR 1B adds PostgreSQL persistence for accounts, player profiles, player entities,
guilds, and guild members. It evolves the provider-neutral capability contracts
where atomicity or optimistic concurrency cannot be represented by the PR 1A
CRUD-shaped methods. JSON and SQLite keep their existing file formats and map
their current behavior into the stronger contracts.

PostgreSQL remains unavailable through normal server composition in this pull
request. Tests construct the PostgreSQL provider and stores directly. PR 1C
adds leaderboard persistence and then activates the complete PostgreSQL runtime
profile.

## 2. Goals

- Add fresh-install PostgreSQL tables for all non-leaderboard game data.
- Implement focused account, player, and guild stores over the PR 1A provider.
- Make PostgreSQL account, player aggregate, and guild writes atomic and
  revision checked.
- Enforce identity normalization, entity ownership, and parent integrity.
- Classify stale, conflicting, invalid, failed, and uncertain writes explicitly.
- Preserve JSON and SQLite persistent formats and established behavior unless
  this design explicitly strengthens a caller transaction boundary.
- Pass shared conformance tests and PostgreSQL-specific tests on versions 16
  and 17.

## 3. Non-Goals

- PostgreSQL leaderboard persistence or normal runtime activation.
- JSON or SQLite import into PostgreSQL.
- Changes to the SQLite schema version or JSON serialization format.
- Multi-process active writers, asynchronous public persistence contracts, or
  a generic cross-database repository.
- Portal projections, account operation leases, domain command records, or
  runtime session handling for uncertain PostgreSQL outcomes.
- Production container or deployment artifacts.

## 4. Architecture

The PostgreSQL project adds three focused implementations:

- `PostgreSQLAccountStore`
- `PostgreSQLPlayerStore`
- `PostgreSQLGuildStore`

They share the process-wide `NpgsqlDataSource`, writer owner, configuration, and
one internal `PostgreSQLStoreExecutor`. The executor owns absolute operation
deadlines, short-lived connections, transaction creation, writer-fence
validation, command and lock timeouts, commit-stage tracking, and sanitized
failure classification. Domain stores own mappings, validation, SQL, and
domain result selection. No store retains a normal pooled connection.

Reads open one pooled connection and do not validate the writer fence. Writes
run through the executor in a `ReadCommitted` transaction, acquire the shared
writer-fence transaction lock, and verify owner and generation before domain
SQL executes.

The shared database-access project contains contracts, normalization, result
types, model metadata, and provider-neutral aggregate validation. It contains
no PostgreSQL types or SQL. SQLite and JSON adapters implement the same public
contracts without a relational base class.

## 5. Capability Contracts

Read operations retain the existing `bool` and `out` style where absence is
the only expected negative result. Writes return domain result enums so callers
can distinguish conflict, stale state, invalid data, ordinary failure, and an
uncertain commit without seeing provider exceptions.

The result enums are provider-neutral and use these exact states:

- `AccountStoreResult`: `Success`, `AccountNotFound`, `EmailConflict`,
  `PlayerNameConflict`, `StaleRevision`, `InvalidData`, `Failed`, and
  `OutcomeUncertain`.
- `PlayerStoreResult`: `Success`, `AccountNotFound`, `StaleRevision`,
  `InvalidAggregate`, `Failed`, and `OutcomeUncertain`.
- `GuildStoreResult`: `Success`, `GuildNotFound`, `NameConflict`,
  `MembershipConflict`, `StaleRevision`, `InvalidData`, `Failed`, and
  `OutcomeUncertain`.

### 5.1 Account Store

`IAccountStore` retains `TryQueryAccountByEmail` and replaces generic unchecked
updates with `InsertAccount`, `ChangePlayerName`, `ChangePassword`,
`ChangeUserLevel`, and `ChangeFlags` write intents. The insert returns
`AccountStoreResult` instead of `bool`; each mutation receives the loaded
`DBAccount` followed by its intended new value and returns the same result type.

- Insert an account.
- Change a player name.
- Change a password and clear `IsPasswordExpired` atomically.
- Change user level.
- Replace account flags.

The write result distinguishes success, missing account, email conflict,
player-name conflict, stale revision, invalid data, ordinary failure, and
uncertain outcome. Insert conflicts are classified by the named normalized
email and player-name constraints. Mutation methods receive the loaded account
and intended new values; they do not require callers to mutate the model before
the write.

On success, the store applies the committed values and returned metadata to
the in-memory account. On any other result, the in-memory values and versions
remain unchanged except that an uncertain result marks the account as
uncertain. Account security notifications remain after committed success.

JSON maps unsupported account writes to ordinary failure. SQLite performs its
existing SQL and maps affected rows and uniqueness errors into the new results.

### 5.2 Player Store

Player-name lookup, all-name lookup, and last-logout lookup retain their
current signatures. `LoadPlayerData` and `SavePlayerData` return
`PlayerStoreResult` instead of `bool`.
A missing `player_profile` is a successful new-profile load and produces the
same defaults as `DBPlayer.Reset()`.

`GetPlayerNames` is defined to append rows to the supplied dictionary and to
return true when at least one stored name exists. Callers that need replacement
semantics clear the dictionary first.

Save writes the complete player profile and entity snapshot. Its result
distinguishes success, stale revision, invalid aggregate, ordinary failure, and
uncertain outcome. A player marked uncertain rejects another save until a fresh
aggregate is loaded.

If the supplied account row no longer exists, load and save return
`AccountNotFound`. A missing profile under an existing account remains the
successful new-profile case.

### 5.3 Guild Store

`IGuildStore` provides:

- Startup load of all guilds and members.
- Atomic initial guild and leader-member creation.
- Checked guild-name change.
- Checked MOTD change.
- Atomic membership transitions containing every insert, rank change, and
  removal in one domain action.
- Checked guild deletion.

Guild results distinguish success, missing data, normalized-name conflict,
membership conflict, stale revision, invalid data, ordinary failure, and
uncertain outcome. A member already belonging to another guild is a conflict;
an upsert never silently moves it.

`LoadGuilds` retains its current `bool` result. `CreateGuild`,
`ChangeGuildName`, `ChangeGuildMotd`, `ApplyMembershipTransition`, and
`DeleteGuild` return `GuildStoreResult`.

`GuildMemberTransition` contains one or two `GuildMemberChange` values. Each
change contains a player ID, nullable expected membership, and nullable new
membership. A null expected value requires that the player is not currently a
member; a null new value deletes the expected member. The enclosing loaded
`DBGuild` supplies the expected guild and revision. Changes cannot repeat a
player, use `None` as a persisted rank, or leave a surviving guild without
exactly one leader.

Joining, leaving, ordinary promotion, and ordinary demotion use one change.
Leadership transfer and leader departure with a successor use two changes.
Leader departure without a successor uses checked `DeleteGuild`, whose member
deletion cascades. The PostgreSQL store locks and checks the guild revision,
checks every expected member state, applies demotions/deletions before
promotions/inserts, and increments the guild revision once for the complete
transition. Any mismatch returns `StaleRevision`; a player found in another
guild returns `MembershipConflict`.

Guild callers persist before changing in-memory state or broadcasting. Failed
and stale writes leave runtime state unchanged and return the existing closest
game protocol error. Initial guild creation does not publish the guild until
the guild and leader rows commit together.

Caller mappings are fixed as follows:

| Store result | Name change | MOTD change | Membership change |
| --- | --- | --- | --- |
| `Success` | `eGCNRCSuccess` | `eGCMotdRCSuccess` | Existing success or dissolved-success result selected by the validated transition |
| `NameConflict` | `eGCNRCDuplicateName` | Not applicable | Not applicable |
| `GuildNotFound` | `eGCNRCInvalidGuild` | `eGCMotdRCInvalidGuild` | `eGCMRCGuildInErrorState` |
| `InvalidData` | `eGCNRCInvalidGuild` | `eGCMotdRCInvalidGuild` | `eGCMRCInternalError` |
| `MembershipConflict` | Not applicable | Not applicable | `eGCMRCGuildInErrorState` |
| `StaleRevision`, `Failed`, or `OutcomeUncertain` | `eGCNRCGuildInErrorState` | `eGCMotdRCGuildInErrorState` | `eGCMRCGuildInErrorState` |

A failed initial guild creation uses its existing internal-error path. A join
`MembershipConflict` maps to `eGRIRAlreadyInOtherGuild`; other failed joins map
to `eGRIRCInternalError`. No failure path broadcasts or mutates guild state.

## 6. Model Metadata

Provider metadata is marked `[JsonIgnore]`, so existing JSON files do not
change. SQLite synthesizes legacy values in memory and ignores optimistic
revisions during writes.

All three aggregate models use `PersistenceState.Clean` or
`PersistenceState.OutcomeUncertain`. Their optimistic revision property is a
nonnegative `long` named `PersistenceRevision`.

`DBAccount` gains:

- `PasswordAlgorithm` with persisted value `Pbkdf2HmacSha512 = 1` and password
  format version `1`.
- PBKDF2 iteration count `210000` and key size `64` bytes for newly created
  credentials.
- Credential version and game-security version, initially one.
- Nullable email-verification time.
- Persistence revision and database audit timestamps.
- An uncertain-persistence marker.

`DBPlayer` gains persistence revision, nullable integer archive-version and
game-build diagnostic metadata, database audit timestamps, and an uncertain
marker. A new profile keeps diagnostics null until the game serializes an
archive. Successful `1.0.1` serialization records `ArchiveVersion.Current`
(`10`) and `GameBuildNumber.Current` (`479899`) before saving. PostgreSQL loads
stored values without deriving or trusting metadata from opaque archive bytes.

`DBGuild` gains persistence revision, database audit timestamps, and an
uncertain marker. Guild-member rows do not need independent optimistic
revisions because member operations are constrained by player, expected guild,
and current rank.

## 7. Migration 0002

PR 1B adds immutable embedded migration `0002_CorePersistence.sql`. Migration
`0001` is not modified. Every table and index is schema-qualified under
`mhserveremu`.

### 7.1 Account

`mhserveremu.account` contains:

- Signed `bigint` account ID primary key.
- Display and normalized email.
- Display and normalized player name.
- Non-null password hash and salt as `bytea`.
- Password algorithm `1`, format version `1`, PBKDF2 iteration count `210000`,
  and key size `64`; the fresh-install migration constrains these exact current
  values and requires 64-byte hashes and salts.
- Credential and game-security versions constrained to at least one.
- Checked user level and persisted flags.
- Nullable email-verification `timestamptz`.
- Nonnegative optimistic revision.
- Non-null created and updated UTC timestamps.

Named unique indexes enforce normalized email and normalized player name. User
level uses persisted values `User = 0`, `Moderator = 1`, and `Admin = 2`.
Account flags remain an unconstrained non-null signed integer because persisted
flag values are forward-compatible and historical bit `3` may exist even
though current code does not set it. Text length, required binary values,
algorithm, level, and version constraints are named so store failure
classification does not depend on localized messages. Account IDs remain
arbitrary signed bit patterns and are not constrained to be positive.

### 7.2 Player Profile

`mhserveremu.player_profile` contains the account ID as primary key and an
`ON DELETE CASCADE` foreign key to account. It stores non-null opaque archive
bytes, nullable positive `integer` archive-version and game-build diagnostics,
start target, AOI volume, Gazillionite balance, last logout time, nonnegative
revision, and UTC audit timestamps.

Game identifier and timestamp fields remain signed `bigint` values to preserve
existing C# bit patterns and clock units. The migration does not reserve later
upstream fields such as `Player.Flags`.

### 7.3 Player Entity

`mhserveremu.player_entity` stores all four current entity kinds:

- Entity ID primary key.
- Owner account ID.
- Checked entity-kind discriminator.
- Nullable parent entity ID.
- Inventory prototype ID.
- Slot as `bigint` constrained to `0..4294967295`.
- Entity prototype ID.
- Non-null opaque archive bytes.

Owner account references `player_profile(account_id) ON DELETE CASCADE`. A
unique `(owner_account_id, entity_id)` key supports a composite self-reference
from `(owner_account_id, parent_entity_id)` with cascade deletion. A child
cannot reference another account. Root rows use a null parent; mapping restores
the current root `ContainerDbGuid` as the owner account ID.

A partial `UNIQUE NULLS NOT DISTINCT` index on owner, parent, inventory
prototype, and slot enforces inventory-slot uniqueness when the inventory
prototype is nonzero. Indexes cover owner aggregate load and owner/kind/parent
access.

Database checks enforce known kinds, slot range, and a parent for controlled
entities. Kind values freeze the existing enum ordinals: `Avatar = 0`,
`TeamUp = 1`, `Item = 2`, and `ControlledEntity = 3`.

The exact graph is at most two row levels: root depth `0` and child depth `1`.
Avatars and team-ups must be roots. Items may be roots or children of an avatar
or team-up. Controlled entities must be children of an avatar. No entity may
parent an avatar or team-up; items and controlled entities cannot be parents.
Application validation enforces this matrix, cycle rejection, duplicate entity
IDs, duplicate inventory slots, and complete parent presence before SQL.

### 7.4 Guilds

`mhserveremu.guild` stores application ID, display and normalized name, MOTD,
creator account ID, game creation time, nonnegative revision, and UTC audit
timestamps. The normalized name has a named unique index.

`mhserveremu.guild_member` stores player account ID as its primary key, guild
ID, checked membership rank, and UTC audit timestamps. Persisted ranks are
`Member = 1`, `Officer = 2`, and `Leader = 3`; `None = 0` is represented by row
absence. Player and guild foreign keys cascade on deletion. The player primary
key enforces membership in at most one guild, and a partial unique index on
guild ID where membership is `3` prevents two leaders.

## 8. Identity Normalization Version 1

Normalization is application-defined and shared by future providers. It does
not use PostgreSQL collations or `citext`. Fixed test vectors lock the behavior
to `application_metadata.identity_normalization_version = 1`.

- Email is trimmed, normalized to Unicode NFC, converted with invariant
  lowercase, limited to 320 characters, and rejected when it contains control
  characters. The canonical display value remains lowercase to preserve
  current account behavior.
- Player names remain 1-16 ASCII alphanumeric characters. The display value is
  preserved and the normalized key is invariant uppercase.
- Guild names are trimmed, normalized to Unicode NFC, rejected when they
  contain controls, and converted to invariant uppercase for the unique key.
  The accepted display casing is preserved.

PostgreSQL stores both display and normalized values. SQLite keeps its current
schema and `NOCASE` behavior; conformance tests do not claim Unicode uniqueness
guarantees that SQLite cannot provide without a file migration.

## 9. Write Semantics

### 9.1 Deadlines and Fencing

Each public PostgreSQL call receives one monotonic deadline using the configured
store-operation timeout. It covers pool acquisition, advisory and row locks,
all statements, and commit or rollback. Transaction-local `statement_timeout`
and `lock_timeout` and every Npgsql command use the remaining time.

The shared writer transaction lock is acquired with bounded polling or an
equivalent transaction-local lock timeout; no runtime write can wait forever
inside `PostgreSQLWriterOwner.ValidateTransactionAsync`. Fenced writes fail
before domain SQL. Public contracts remain synchronous; internal async Npgsql
operations use `ConfigureAwait(false)` before synchronous bridging.

### 9.2 Account Mutations

PostgreSQL account updates include `WHERE id = @id AND revision = @revision`
and return the committed revision and metadata. Password changes increment
credential version, game-security version, revision, and updated time. User
level and security-sensitive flag changes increment game-security version,
revision, and updated time. Player-name changes update display and normalized
values, revision, and updated time.

Affected-row count zero is stale or missing and is resolved without an
unchecked overwrite. A conflict or rollback changes no in-memory values or
versions.

### 9.3 Player Aggregate Save and Load

Load reads the profile and all owner entities, builds a temporary aggregate,
validates and partitions the graph, and replaces account state only after the
complete load succeeds.

Save validates the graph before SQL, then:

1. Begins one fenced transaction and takes a transaction advisory lock keyed
   by signed account ID within the operation deadline.
2. Inserts a new profile or compare-and-swap updates the loaded revision.
3. Bulk-upserts parent entities before children using set-based Npgsql arrays.
4. Verifies every returned entity ID retains its immutable owner and kind.
5. Deletes persisted owner rows absent from the supplied aggregate.
6. Commits and then applies the new revision to the in-memory player.

An ID owned by another account or stored under another kind cannot be
overwritten. A retained child whose parent is absent fails validation before
SQL. Empty aggregates and aggregates containing thousands of entities use the
same transaction and set-based path.

### 9.4 Guild Writes

Initial guild and leader membership insert in one fenced transaction. Name and
MOTD changes compare the loaded guild revision and increment it on commit.
Membership transitions lock the guild row, compare its revision and every
expected member rank, apply all one-or-two changes atomically, and increment
the guild revision once. They never move a player implicitly. Guild deletion
checks the expected revision and relies on foreign-key cascade for members.

Callers publish successful state only after commit. Existing JSON no-op guild
behavior remains unchanged; SQLite uses transactions where the stronger
compound contract now requires atomicity without changing its schema.

## 10. Failure and Uncertain Outcomes

Normal absence returns an expected negative result. Named unique constraints
map to stable identity conflicts. Revision misses map to stale results. Graph
validation maps to invalid aggregate. Deadline, fence, network, and unexpected
provider errors before commit invocation roll back and map to ordinary failure.
Writes are not retried generically.

Once commit invocation begins, a connection or cancellation failure that makes
the server outcome unknowable maps to `OutcomeUncertain`. The affected account,
player, or guild receives a hidden uncertain marker and rejects another write
using its old revision. A newly loaded model can reconcile database state; the
uncertain instance is never silently reused.

Unit tests inject a transaction committer that enters the commit stage and then
throws, proving stage-based classification without pretending to prove a real
server outcome. A PostgreSQL integration test installs a test-only deferrable,
initially deferred constraint trigger on the affected ephemeral table. The
trigger terminates its own backend while PostgreSQL processes `CommitAsync`,
then the test verifies the real Npgsql path returns `OutcomeUncertain`. The
trigger and function are not migration resources or production SQL.

PR 1B proves classification and lockout at the store boundary. PR 1C wires an
uncertain player save to disconnect/reload, an uncertain account mutation to
fail-closed authentication and reconciliation, and an uncertain global guild
transition to controlled shutdown.

PostgreSQL catches provider exceptions before they leave the provider project.
Diagnostics contain only stable codes, operation names, SQLSTATE, and safe
entity identifiers. Connection strings, credentials, SQL parameters, and
archive bytes are never logged or included in failure objects.

## 11. Runtime Integration Boundary

PR 1B changes shared contracts and existing SQLite/JSON callers, but it does
not add the PostgreSQL project reference to normal server composition. The
existing `Provider=PostgreSQL` rejection remains and is covered by composition
tests. Integration tests start `PostgreSQLProvider` directly and construct the
stores over it.

PR 1C will compose these stores with PostgreSQL leaderboards, own provider
lifetime, and connect fatal writer-lock and uncertain-outcome signals to
controlled runtime behavior.

## 12. Verification

Shared conformance tests run against SQLite and PostgreSQL where capabilities
overlap. PostgreSQL-specific integration tests run against versions 16 and 17
through the existing tagged CI matrix.

PR 1B coverage includes:

- Fixed normalization-v1 vectors and normalized uniqueness.
- Account round trips, duplicate races, binary credentials, checked security
  mutations, metadata/version increments, stale revisions, and high-bit signed
  ID round trips.
- Fixed deterministic `1.0.1` player/archive payloads, missing/new profiles,
  every entity kind, root reconstruction, nested containers, opaque archives,
  stale deletion, empty aggregates, and thousands-of-entities aggregates.
- Invalid kinds, slots, duplicate IDs and inventory slots, missing or invalid
  parents, cross-account parents, cycles, and transaction rollback.
- Competing player saves and per-account lock deadlines.
- Atomic guild/leader creation, normalized name conflict, member changes,
  membership conflict, stale guild revisions, and cascade deletion.
- Deterministic connection termination at commit invocation, uncertain result
  classification, and rejection of another write from the uncertain object.
- Migration bootstrap, `0002` history/checksum validation, and idempotent
  restart.
- Continued JSON, SQLite, server, redaction, and PostgreSQL activation-gate
  coverage.

The integration fixture creates a unique database, applies the full migration
catalog, disposes all stores/provider connections, and drops the database. CI
must fail rather than skip when its administrative connection variable is
missing.

## 13. Acceptance Criteria

- Account, player/entity, and guild conformance passes on PostgreSQL 16 and 17.
- Aggregate writes are atomic and stale revisions cannot overwrite committed
  state.
- Entity ownership, parent integrity, cycles, kinds, slots, and uniqueness are
  rejected as specified.
- Security metadata and version increments match account mutation intent.
- Commit-stage ambiguity marks the aggregate uncertain and prevents unsafe
  reuse.
- Guild state is persisted before runtime publication.
- Existing JSON and SQLite files remain compatible and all tests pass.
- Normal PostgreSQL server startup remains gated for PR 1C.
