using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Validation;
using Npgsql;
using NpgsqlTypes;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal sealed class PostgreSQLPlayerStore : IPlayerStore
    {
        private const string AccountTable = "mhserveremu.account";
        private const string ProfileTable = "mhserveremu.player_profile";
        private const string EntityTable = "mhserveremu.player_entity";

        private readonly NpgsqlDataSource _dataSource;
        private readonly PostgreSQLStoreExecutor _executor;

        internal PostgreSQLPlayerStore(NpgsqlDataSource dataSource, PostgreSQLStoreExecutor executor)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut)
        {
            playerDbId = 0;
            playerNameOut = null;
            string normalizedName;
            try
            {
                normalizedName = IdentityNormalizer.NormalizePlayerName(playerName);
            }
            catch (ArgumentException)
            {
                return false;
            }

            try
            {
                using NpgsqlConnection connection = _dataSource.OpenConnection();
                using NpgsqlCommand command = new($"SELECT id, player_name FROM {AccountTable} WHERE normalized_player_name = @playerName", connection);
                command.Parameters.AddWithValue("playerName", NpgsqlDbType.Text, normalizedName);
                using NpgsqlDataReader reader = command.ExecuteReader();
                if (reader.Read() == false)
                    return false;

                playerDbId = unchecked((ulong)reader.GetInt64(0));
                playerNameOut = reader.GetString(1);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetPlayerName(ulong playerDbId, out string playerName)
        {
            playerName = null;
            try
            {
                using NpgsqlConnection connection = _dataSource.OpenConnection();
                using NpgsqlCommand command = new($"SELECT player_name FROM {AccountTable} WHERE id = @id", connection);
                command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, unchecked((long)playerDbId));
                playerName = command.ExecuteScalar() as string;
                return string.IsNullOrWhiteSpace(playerName) == false;
            }
            catch
            {
                playerName = null;
                return false;
            }
        }

        public bool GetPlayerNames(Dictionary<ulong, string> playerNames)
        {
            ArgumentNullException.ThrowIfNull(playerNames);
            try
            {
                using NpgsqlConnection connection = _dataSource.OpenConnection();
                using NpgsqlCommand command = new($"SELECT id, player_name FROM {AccountTable}", connection);
                using NpgsqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    playerNames[unchecked((ulong)reader.GetInt64(0))] = reader.GetString(1);
                return playerNames.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime)
        {
            lastLogoutTime = 0;
            try
            {
                using NpgsqlConnection connection = _dataSource.OpenConnection();
                using NpgsqlCommand command = new($"SELECT last_logout_time FROM {ProfileTable} WHERE account_id = @id", connection);
                command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, unchecked((long)playerDbId));
                object value = command.ExecuteScalar();
                if (value == null)
                    return false;
                lastLogoutTime = (long)value;
                return lastLogoutTime > 0;
            }
            catch
            {
                return false;
            }
        }

        public PlayerStoreResult LoadPlayerData(DBAccount account)
        {
            if (account == null)
                return PlayerStoreResult.Failed;

            try
            {
                using NpgsqlConnection connection = _dataSource.OpenConnection();
                if (AccountExists(connection, null, account.Id) == false)
                    return PlayerStoreResult.AccountNotFound;

                DBAccount loaded = new() { Id = account.Id };
                using NpgsqlCommand command = new($"SELECT archive_data, archive_version, game_build_number, start_target, aoi_volume, gazillionite_balance, last_logout_time, revision, created_at_utc, updated_at_utc FROM {ProfileTable} WHERE account_id = @id; SELECT id, kind, parent_entity_id, inventory_proto_id, slot, entity_proto_id, archive_data FROM {EntityTable} WHERE owner_account_id = @id", connection);
                command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, account.Id);
                using NpgsqlDataReader reader = command.ExecuteReader();
                if (reader.Read())
                    loaded.Player = ReadPlayer(reader, account.Id);
                else
                    loaded.Player = new DBPlayer(account.Id);

                if (reader.NextResult() == false)
                    return PlayerStoreResult.Failed;
                while (reader.Read())
                {
                    DBEntityCategory category = (DBEntityCategory)reader.GetInt32(1);
                    DBEntity entity = new()
                    {
                        DbGuid = reader.GetInt64(0),
                        ContainerDbGuid = reader.IsDBNull(2) ? account.Id : reader.GetInt64(2),
                        InventoryProtoGuid = reader.GetInt64(3),
                        Slot = checked((uint)reader.GetInt64(4)),
                        EntityProtoGuid = reader.GetInt64(5),
                        ArchiveData = reader.GetFieldValue<byte[]>(6),
                    };
                    if (AddEntity(loaded, category, entity) == false)
                        return PlayerStoreResult.Failed;
                }

                if (PlayerAggregateValidator.TryValidate(loaded, out _) == false)
                    return PlayerStoreResult.Failed;

                account.Player = loaded.Player;
                account.ClearEntities();
                account.Avatars.AddRange(loaded.Avatars.Entries);
                account.TeamUps.AddRange(loaded.TeamUps.Entries);
                account.Items.AddRange(loaded.Items.Entries);
                account.ControlledEntities.AddRange(loaded.ControlledEntities.Entries);
                return PlayerStoreResult.Success;
            }
            catch
            {
                return PlayerStoreResult.Failed;
            }
        }

        public PlayerStoreResult SavePlayerData(DBAccount account)
        {
            if (account == null || PlayerAggregateValidator.TryValidate(account, out _) == false)
                return PlayerStoreResult.InvalidAggregate;
            if (account.PersistenceState == PersistenceState.OutcomeUncertain || account.Player?.PersistenceState == PersistenceState.OutcomeUncertain)
                return PlayerStoreResult.OutcomeUncertain;

            DBPlayer player = account.Player ?? new DBPlayer(account.Id);
            if (player.DbGuid != account.Id)
                return PlayerStoreResult.InvalidAggregate;

            ProfileMetadata? metadata = null;
            PlayerStoreResult result = PlayerStoreResult.Success;
            PostgreSQLWriteResult write = _executor.ExecuteWriteAsync("PlayerAggregateSave", account.Id, async (connection, transaction, cancellationToken) =>
            {
                await AcquirePlayerLockAsync(connection, transaction, account.Id, cancellationToken);
                if (await AccountExistsAsync(connection, transaction, account.Id, cancellationToken) == false)
                {
                    result = PlayerStoreResult.AccountNotFound;
                    return;
                }

                bool profileExists = (await GetProfileRevisionAsync(connection, transaction, account.Id, cancellationToken)).HasValue;
                metadata = await WriteProfileAsync(connection, transaction, account.Id, player, profileExists, cancellationToken);
                if (metadata.HasValue == false)
                {
                    result = await AccountExistsAsync(connection, transaction, account.Id, cancellationToken)
                        ? PlayerStoreResult.StaleRevision
                        : PlayerStoreResult.AccountNotFound;
                    throw new PlayerStoreWriteAbortedException();
                }

                List<(DBEntity Entity, DBEntityCategory Category)> entities = GetEntities(account);
                await UpsertEntitiesAsync(connection, transaction, account.Id, entities.Where(entry => entry.Entity.ContainerDbGuid == account.Id), cancellationToken);
                await UpsertEntitiesAsync(connection, transaction, account.Id, entities.Where(entry => entry.Entity.ContainerDbGuid != account.Id), cancellationToken);
                await DeleteStaleEntitiesAsync(connection, transaction, account.Id, entities.Select(entry => entry.Entity.DbGuid).ToArray(), cancellationToken);
            }).GetAwaiter().GetResult();

            if (write.Outcome == PostgreSQLWriteOutcome.OutcomeUncertain)
            {
                player.PersistenceState = PersistenceState.OutcomeUncertain;
                return PlayerStoreResult.OutcomeUncertain;
            }
            if (write.Outcome == PostgreSQLWriteOutcome.Failed)
                return result == PlayerStoreResult.Success ? PlayerStoreResult.Failed : result;
            if (result != PlayerStoreResult.Success || metadata.HasValue == false)
                return result == PlayerStoreResult.Success ? PlayerStoreResult.Failed : result;

            account.Player = player;
            player.PersistenceRevision = metadata.Value.Revision;
            player.CreatedAtUtc = metadata.Value.CreatedAtUtc;
            player.UpdatedAtUtc = metadata.Value.UpdatedAtUtc;
            player.PersistenceState = PersistenceState.Clean;
            return PlayerStoreResult.Success;
        }

        private static bool AccountExists(NpgsqlConnection connection, NpgsqlTransaction transaction, long accountId)
        {
            using NpgsqlCommand command = new($"SELECT 1 FROM {AccountTable} WHERE id = @id", connection, transaction);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, accountId);
            return command.ExecuteScalar() != null;
        }

        private static async Task<bool> AccountExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long accountId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT 1 FROM {AccountTable} WHERE id = @id", connection, transaction);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, accountId);
            return await command.ExecuteScalarAsync(cancellationToken) != null;
        }

        private static async Task AcquirePlayerLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long accountId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new("SELECT pg_advisory_xact_lock(@accountId)", connection, transaction);
            command.Parameters.AddWithValue("accountId", NpgsqlDbType.Bigint, accountId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<long?> GetProfileRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long accountId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT revision FROM {ProfileTable} WHERE account_id = @id", connection, transaction);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, accountId);
            object value = await command.ExecuteScalarAsync(cancellationToken);
            return value == null ? null : (long)value;
        }

        private static async Task<ProfileMetadata?> WriteProfileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long accountId, DBPlayer player, bool exists, CancellationToken cancellationToken)
        {
            string sql = exists
                ? $"UPDATE {ProfileTable} SET archive_data = @archiveData, archive_version = @archiveVersion, game_build_number = @gameBuildNumber, start_target = @startTarget, aoi_volume = @aoiVolume, gazillionite_balance = @gazillioniteBalance, last_logout_time = @lastLogoutTime, revision = revision + 1, updated_at_utc = CURRENT_TIMESTAMP WHERE account_id = @accountId AND revision = @revision RETURNING revision, created_at_utc, updated_at_utc"
                : $"INSERT INTO {ProfileTable} (account_id, archive_data, archive_version, game_build_number, start_target, aoi_volume, gazillionite_balance, last_logout_time) VALUES (@accountId, @archiveData, @archiveVersion, @gameBuildNumber, @startTarget, @aoiVolume, @gazillioniteBalance, @lastLogoutTime) RETURNING revision, created_at_utc, updated_at_utc";
            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("accountId", NpgsqlDbType.Bigint, accountId);
            command.Parameters.AddWithValue("archiveData", NpgsqlDbType.Bytea, player.ArchiveData ?? Array.Empty<byte>());
            command.Parameters.AddWithValue("archiveVersion", NpgsqlDbType.Integer, player.ArchiveVersion ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("gameBuildNumber", NpgsqlDbType.Integer, player.GameBuildNumber ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("startTarget", NpgsqlDbType.Bigint, player.StartTarget);
            command.Parameters.AddWithValue("aoiVolume", NpgsqlDbType.Integer, player.AOIVolume);
            command.Parameters.AddWithValue("gazillioniteBalance", NpgsqlDbType.Bigint, player.GazillioniteBalance);
            command.Parameters.AddWithValue("lastLogoutTime", NpgsqlDbType.Bigint, player.LastLogoutTime);
            if (exists)
                command.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, player.PersistenceRevision);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new ProfileMetadata(reader.GetInt64(0), reader.GetDateTime(1), reader.GetDateTime(2))
                : null;
        }

        private static async Task UpsertEntitiesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long accountId, IEnumerable<(DBEntity Entity, DBEntityCategory Category)> entries, CancellationToken cancellationToken)
        {
            (DBEntity Entity, DBEntityCategory Category)[] entities = entries.ToArray();
            if (entities.Length == 0)
                return;

            const string sql = "INSERT INTO mhserveremu.player_entity (id, owner_account_id, kind, parent_entity_id, inventory_proto_id, slot, entity_proto_id, archive_data) SELECT id, owner_account_id, kind, parent_entity_id, inventory_proto_id, slot, entity_proto_id, archive_data FROM UNNEST(@ids, @owners, @kinds, @parents, @inventoryPrototypeIds, @slots, @entityPrototypeIds, @archives) AS source(id, owner_account_id, kind, parent_entity_id, inventory_proto_id, slot, entity_proto_id, archive_data) ON CONFLICT (id) DO UPDATE SET parent_entity_id = EXCLUDED.parent_entity_id, inventory_proto_id = EXCLUDED.inventory_proto_id, slot = EXCLUDED.slot, entity_proto_id = EXCLUDED.entity_proto_id, archive_data = EXCLUDED.archive_data WHERE player_entity.owner_account_id = EXCLUDED.owner_account_id AND player_entity.kind = EXCLUDED.kind RETURNING id";
            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, entities.Select(entry => entry.Entity.DbGuid).ToArray());
            command.Parameters.AddWithValue("owners", NpgsqlDbType.Array | NpgsqlDbType.Bigint, Enumerable.Repeat(accountId, entities.Length).ToArray());
            command.Parameters.AddWithValue("kinds", NpgsqlDbType.Array | NpgsqlDbType.Integer, entities.Select(entry => (int)entry.Category).ToArray());
            command.Parameters.AddWithValue("parents", NpgsqlDbType.Array | NpgsqlDbType.Bigint, entities.Select(entry => entry.Entity.ContainerDbGuid == accountId ? (long?)null : entry.Entity.ContainerDbGuid).ToArray());
            command.Parameters.AddWithValue("inventoryPrototypeIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, entities.Select(entry => entry.Entity.InventoryProtoGuid).ToArray());
            command.Parameters.AddWithValue("slots", NpgsqlDbType.Array | NpgsqlDbType.Bigint, entities.Select(entry => (long)entry.Entity.Slot).ToArray());
            command.Parameters.AddWithValue("entityPrototypeIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, entities.Select(entry => entry.Entity.EntityProtoGuid).ToArray());
            command.Parameters.AddWithValue("archives", NpgsqlDbType.Array | NpgsqlDbType.Bytea, entities.Select(entry => entry.Entity.ArchiveData).ToArray());
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            int written = 0;
            while (await reader.ReadAsync(cancellationToken))
                written++;
            if (written != entities.Length)
                throw new InvalidOperationException("Player entity owner or kind conflict.");
        }

        private static async Task DeleteStaleEntitiesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long accountId, long[] entityIds, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"DELETE FROM {EntityTable} WHERE owner_account_id = @accountId AND NOT (id = ANY(@entityIds))", connection, transaction);
            command.Parameters.AddWithValue("accountId", NpgsqlDbType.Bigint, accountId);
            command.Parameters.AddWithValue("entityIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, entityIds);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static DBPlayer ReadPlayer(NpgsqlDataReader reader, long accountId)
        {
            return new DBPlayer
            {
                DbGuid = accountId,
                ArchiveData = reader.GetFieldValue<byte[]>(0),
                ArchiveVersion = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                GameBuildNumber = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                StartTarget = reader.GetInt64(3),
                AOIVolume = reader.GetInt32(4),
                GazillioniteBalance = reader.GetInt64(5),
                LastLogoutTime = reader.GetInt64(6),
                PersistenceRevision = reader.GetInt64(7),
                CreatedAtUtc = reader.GetDateTime(8),
                UpdatedAtUtc = reader.GetDateTime(9),
                PersistenceState = PersistenceState.Clean,
            };
        }

        private static List<(DBEntity Entity, DBEntityCategory Category)> GetEntities(DBAccount account)
        {
            return account.Avatars.Entries.Select(entity => (entity, DBEntityCategory.Avatar))
                .Concat(account.TeamUps.Entries.Select(entity => (entity, DBEntityCategory.TeamUp)))
                .Concat(account.Items.Entries.Select(entity => (entity, DBEntityCategory.Item)))
                .Concat(account.ControlledEntities.Entries.Select(entity => (entity, DBEntityCategory.ControlledEntity)))
                .ToList();
        }

        private static bool AddEntity(DBAccount account, DBEntityCategory category, DBEntity entity)
        {
            return category switch
            {
                DBEntityCategory.Avatar => account.Avatars.Add(entity),
                DBEntityCategory.TeamUp => account.TeamUps.Add(entity),
                DBEntityCategory.Item => account.Items.Add(entity),
                DBEntityCategory.ControlledEntity => account.ControlledEntities.Add(entity),
                _ => false,
            };
        }

        private readonly record struct ProfileMetadata(long Revision, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

        private sealed class PlayerStoreWriteAbortedException : Exception
        {
        }
    }
}
