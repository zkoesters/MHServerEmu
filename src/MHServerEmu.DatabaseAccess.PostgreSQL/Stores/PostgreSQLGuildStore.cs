using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Validation;
using Npgsql;
using NpgsqlTypes;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal sealed class PostgreSQLGuildStore : IGuildStore
    {
        private const string GuildTable = "mhserveremu.guild";
        private const string GuildMemberTable = "mhserveremu.guild_member";
        private const string GuildNameConstraint = "guild_normalized_name_unique";
        private const string GuildMemberPrimaryKeyConstraint = "guild_member_pkey";
        private const string GuildLeaderConstraint = "guild_member_one_leader_unique";

        private readonly NpgsqlDataSource _dataSource;
        private readonly PostgreSQLStoreExecutor _executor;

        internal PostgreSQLGuildStore(NpgsqlDataSource dataSource, PostgreSQLStoreExecutor executor)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public bool LoadGuilds(List<DBGuild> guilds)
        {
            ArgumentNullException.ThrowIfNull(guilds);
            try
            {
                PostgreSQLReadResult<bool> read = _executor.ExecuteReadAsync("GuildLoad", async (connection, deadline, cancellationToken) =>
                {
                    Dictionary<long, DBGuild> loaded = new();
                    await using (NpgsqlCommand command = new($"SELECT id, name, motd, creator_account_id, creation_time, revision, created_at_utc, updated_at_utc FROM {GuildTable}", connection)
                    {
                        CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                    })
                    await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            DBGuild guild = new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4))
                            {
                                PersistenceRevision = reader.GetInt64(5),
                                CreatedAtUtc = reader.GetDateTime(6),
                                UpdatedAtUtc = reader.GetDateTime(7),
                                PersistenceState = PersistenceState.Clean,
                            };
                            guilds.Add(guild);
                            loaded.Add(guild.Id, guild);
                        }
                    }

                    await using NpgsqlCommand memberCommand = new($"SELECT member.player_account_id, member.guild_id, member.membership FROM {GuildMemberTable} member INNER JOIN {GuildTable} guild ON guild.id = member.guild_id", connection)
                    {
                        CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                    };
                    await using NpgsqlDataReader memberReader = await memberCommand.ExecuteReaderAsync(cancellationToken);
                    while (await memberReader.ReadAsync(cancellationToken))
                    {
                        long guildId = memberReader.GetInt64(1);
                        if (loaded.TryGetValue(guildId, out DBGuild guild))
                            guild.Members.Add(new DBGuildMember(memberReader.GetInt64(0), guildId, memberReader.GetInt32(2)));
                    }
                    return true;
                }).GetAwaiter().GetResult();
                if (read.Succeeded)
                    return read.Value;
            }
            catch
            {
            }

            guilds.Clear();
            return false;
        }

        public GuildStoreResult CreateGuild(DBGuild guild, DBGuildMember leader)
        {
            if (CanWrite(guild, out GuildStoreResult result) == false)
                return result;
            if (leader == null || leader.GuildId != guild.Id || leader.Membership != 3 || guild.Motd == null)
                return GuildStoreResult.InvalidData;

            string normalizedName;
            try
            {
                normalizedName = IdentityNormalizer.NormalizeGuildName(guild.Name);
            }
            catch (ArgumentException)
            {
                return GuildStoreResult.InvalidData;
            }

            GuildMetadata? metadata = null;
            PostgreSQLWriteResult write = _executor.ExecuteWriteAsync("GuildCreate", guild.Id, async (connection, transaction, cancellationToken) =>
            {
                await using NpgsqlCommand guildCommand = new($"INSERT INTO {GuildTable} (id, name, normalized_name, motd, creator_account_id, creation_time) VALUES (@id, @name, @normalizedName, @motd, @creatorAccountId, @creationTime) RETURNING revision, created_at_utc, updated_at_utc", connection, transaction);
                AddGuildParameters(guildCommand, guild, normalizedName);
                await using (NpgsqlDataReader reader = await guildCommand.ExecuteReaderAsync(cancellationToken))
                {
                    if (await reader.ReadAsync(cancellationToken))
                        metadata = ReadMetadata(reader);
                }

                await using NpgsqlCommand memberCommand = new($"INSERT INTO {GuildMemberTable} (player_account_id, guild_id, membership) VALUES (@playerId, @guildId, @membership)", connection, transaction);
                AddMemberParameters(memberCommand, leader);
                await memberCommand.ExecuteNonQueryAsync(cancellationToken);
            }, notifyFatalOnOutcomeUncertain: true).GetAwaiter().GetResult();

            return CompleteWrite(guild, write, metadata, MapWriteFailure);
        }

        public GuildStoreResult ChangeGuildName(DBGuild guild, string name)
        {
            if (CanWrite(guild, out GuildStoreResult result) == false)
                return result;

            string normalizedName;
            try
            {
                normalizedName = IdentityNormalizer.NormalizeGuildName(name);
            }
            catch (ArgumentException)
            {
                return GuildStoreResult.InvalidData;
            }

            result = UpdateGuild(guild, "GuildChangeName", $"UPDATE {GuildTable} SET name = @name, normalized_name = @normalizedName, revision = revision + 1, updated_at_utc = CURRENT_TIMESTAMP WHERE id = @id AND revision = @revision RETURNING revision, created_at_utc, updated_at_utc", parameters =>
            {
                parameters.AddWithValue("name", NpgsqlDbType.Text, name);
                parameters.AddWithValue("normalizedName", NpgsqlDbType.Text, normalizedName);
            });
            if (result == GuildStoreResult.Success)
                guild.Name = name;
            return result;
        }

        public GuildStoreResult ChangeGuildMotd(DBGuild guild, string motd)
        {
            if (CanWrite(guild, out GuildStoreResult result) == false)
                return result;
            if (motd == null)
                return GuildStoreResult.InvalidData;

            result = UpdateGuild(guild, "GuildChangeMotd", $"UPDATE {GuildTable} SET motd = @motd, revision = revision + 1, updated_at_utc = CURRENT_TIMESTAMP WHERE id = @id AND revision = @revision RETURNING revision, created_at_utc, updated_at_utc", parameters => parameters.AddWithValue("motd", NpgsqlDbType.Text, motd));
            if (result == GuildStoreResult.Success)
                guild.Motd = motd;
            return result;
        }

        public GuildStoreResult ApplyMembershipTransition(DBGuild guild, GuildMemberTransition transition)
        {
            if (CanWrite(guild, out GuildStoreResult result) == false)
                return result;
            if (transition == null || transition.GuildId != guild.Id)
                return GuildStoreResult.InvalidData;

            GuildMetadata? metadata = null;
            result = GuildStoreResult.Success;
            PostgreSQLWriteResult write = _executor.ExecuteWriteAsync("GuildMembershipTransition", guild.Id, async (connection, transaction, cancellationToken) =>
            {
                long? revision = await GetGuildRevisionForUpdateAsync(connection, transaction, guild.Id, cancellationToken);
                if (revision.HasValue == false)
                {
                    result = GuildStoreResult.GuildNotFound;
                    throw new GuildWriteAbortedException();
                }
                if (revision.Value != transition.ExpectedRevision)
                {
                    result = GuildStoreResult.StaleRevision;
                    throw new GuildWriteAbortedException();
                }

                Dictionary<long, DBGuildMember> members = await ReadRelevantMembersForUpdateAsync(connection, transaction, guild.Id, transition.Changes, cancellationToken);
                foreach (GuildMemberChange change in transition.Changes)
                {
                    if (change.ExpectedMembership == null)
                    {
                        if (members.ContainsKey(change.PlayerDbGuid))
                        {
                            result = GuildStoreResult.MembershipConflict;
                            throw new GuildWriteAbortedException();
                        }
                    }
                    else if (members.TryGetValue(change.PlayerDbGuid, out DBGuildMember member) == false || member.GuildId != guild.Id || member.Membership != change.ExpectedMembership.Value)
                    {
                        result = GuildStoreResult.MembershipConflict;
                        throw new GuildWriteAbortedException();
                    }
                }

                Dictionary<long, long> resultingMemberships = members.Values.Where(member => member.GuildId == guild.Id).ToDictionary(member => member.PlayerDbGuid, member => member.Membership);
                foreach (GuildMemberChange change in transition.Changes)
                {
                    if (change.NewMembership == null)
                        resultingMemberships.Remove(change.PlayerDbGuid);
                    else
                        resultingMemberships[change.PlayerDbGuid] = change.NewMembership.Value;
                }
                if (resultingMemberships.Values.Count(membership => membership == 3) != 1)
                {
                    result = GuildStoreResult.InvalidData;
                    throw new GuildWriteAbortedException();
                }

                foreach (GuildMemberChange change in transition.Changes.Where(change => change.NewMembership == null || (change.ExpectedMembership == 3 && change.NewMembership != 3)))
                    await ApplyMembershipChangeAsync(connection, transaction, guild.Id, change, cancellationToken);
                foreach (GuildMemberChange change in transition.Changes.Where(change => change.NewMembership != null && (change.ExpectedMembership != 3 || change.NewMembership == 3)))
                    await ApplyMembershipChangeAsync(connection, transaction, guild.Id, change, cancellationToken);

                metadata = await IncrementRevisionAsync(connection, transaction, guild.Id, transition.ExpectedRevision, cancellationToken);
                if (metadata.HasValue == false)
                {
                    result = GuildStoreResult.StaleRevision;
                    throw new GuildWriteAbortedException();
                }
            }, notifyFatalOnOutcomeUncertain: true).GetAwaiter().GetResult();

            return CompleteWrite(guild, write, metadata, failure => result == GuildStoreResult.Success ? MapWriteFailure(failure) : result);
        }

        public GuildStoreResult DeleteGuild(DBGuild guild)
        {
            if (CanWrite(guild, out GuildStoreResult result) == false)
                return result;

            bool exists = false;
            bool deleted = false;
            PostgreSQLWriteResult write = _executor.ExecuteWriteAsync("GuildDelete", guild.Id, async (connection, transaction, cancellationToken) =>
            {
                await using NpgsqlCommand command = new($"DELETE FROM {GuildTable} WHERE id = @id AND revision = @revision RETURNING id", connection, transaction);
                command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, guild.Id);
                command.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, guild.PersistenceRevision);
                deleted = await command.ExecuteScalarAsync(cancellationToken) != null;
                if (deleted == false)
                    exists = await GuildExistsAsync(connection, transaction, guild.Id, cancellationToken);
            }, notifyFatalOnOutcomeUncertain: true).GetAwaiter().GetResult();

            if (write.Outcome == PostgreSQLWriteOutcome.OutcomeUncertain)
            {
                guild.PersistenceState = PersistenceState.OutcomeUncertain;
                return GuildStoreResult.OutcomeUncertain;
            }
            if (write.Outcome == PostgreSQLWriteOutcome.Failed)
                return MapWriteFailure(write.Failure);
            if (deleted == false)
                return exists ? GuildStoreResult.StaleRevision : GuildStoreResult.GuildNotFound;

            guild.PersistenceState = PersistenceState.Clean;
            return GuildStoreResult.Success;
        }

        private GuildStoreResult UpdateGuild(DBGuild guild, string operation, string sql, Action<NpgsqlParameterCollection> addParameters)
        {
            GuildMetadata? metadata = null;
            bool exists = false;
            PostgreSQLWriteResult write = _executor.ExecuteWriteAsync(operation, guild.Id, async (connection, transaction, cancellationToken) =>
            {
                await using NpgsqlCommand command = new(sql, connection, transaction);
                command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, guild.Id);
                command.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, guild.PersistenceRevision);
                addParameters(command.Parameters);
                await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    if (await reader.ReadAsync(cancellationToken))
                        metadata = ReadMetadata(reader);
                }
                if (metadata.HasValue == false)
                    exists = await GuildExistsAsync(connection, transaction, guild.Id, cancellationToken);
            }, notifyFatalOnOutcomeUncertain: true).GetAwaiter().GetResult();

            return CompleteWrite(guild, write, metadata, failure => MapWriteFailure(failure), exists ? GuildStoreResult.StaleRevision : GuildStoreResult.GuildNotFound);
        }

        private static async Task<long?> GetGuildRevisionForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long guildId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT revision FROM {GuildTable} WHERE id = @id FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, guildId);
            object value = await command.ExecuteScalarAsync(cancellationToken);
            return value == null ? null : (long)value;
        }

        private static async Task<Dictionary<long, DBGuildMember>> ReadRelevantMembersForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long guildId, IReadOnlyList<GuildMemberChange> changes, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT player_account_id, guild_id, membership FROM {GuildMemberTable} WHERE guild_id = @guildId OR player_account_id = ANY(@playerIds) FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("guildId", NpgsqlDbType.Bigint, guildId);
            command.Parameters.AddWithValue("playerIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, changes.Select(change => change.PlayerDbGuid).ToArray());
            Dictionary<long, DBGuildMember> members = new();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                DBGuildMember member = new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2));
                members.Add(member.PlayerDbGuid, member);
            }
            return members;
        }

        private static async Task ApplyMembershipChangeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long guildId, GuildMemberChange change, CancellationToken cancellationToken)
        {
            string sql;
            if (change.NewMembership == null)
                sql = $"DELETE FROM {GuildMemberTable} WHERE player_account_id = @playerId AND guild_id = @guildId AND membership = @expectedMembership";
            else if (change.ExpectedMembership == null)
                sql = $"INSERT INTO {GuildMemberTable} (player_account_id, guild_id, membership) VALUES (@playerId, @guildId, @membership)";
            else
                sql = $"UPDATE {GuildMemberTable} SET membership = @membership, updated_at_utc = CURRENT_TIMESTAMP WHERE player_account_id = @playerId AND guild_id = @guildId AND membership = @expectedMembership";

            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("playerId", NpgsqlDbType.Bigint, change.PlayerDbGuid);
            command.Parameters.AddWithValue("guildId", NpgsqlDbType.Bigint, guildId);
            if (change.ExpectedMembership.HasValue)
                command.Parameters.AddWithValue("expectedMembership", NpgsqlDbType.Integer, checked((int)change.ExpectedMembership.Value));
            if (change.NewMembership.HasValue)
                command.Parameters.AddWithValue("membership", NpgsqlDbType.Integer, checked((int)change.NewMembership.Value));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new GuildWriteAbortedException();
        }

        private static async Task<GuildMetadata?> IncrementRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long guildId, long revision, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"UPDATE {GuildTable} SET revision = revision + 1, updated_at_utc = CURRENT_TIMESTAMP WHERE id = @id AND revision = @revision RETURNING revision, created_at_utc, updated_at_utc", connection, transaction);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, guildId);
            command.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, revision);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadMetadata(reader) : null;
        }

        private static async Task<bool> GuildExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long guildId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT 1 FROM {GuildTable} WHERE id = @id", connection, transaction);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, guildId);
            return await command.ExecuteScalarAsync(cancellationToken) != null;
        }

        private static void AddGuildParameters(NpgsqlCommand command, DBGuild guild, string normalizedName)
        {
            command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, guild.Id);
            command.Parameters.AddWithValue("name", NpgsqlDbType.Text, guild.Name);
            command.Parameters.AddWithValue("normalizedName", NpgsqlDbType.Text, normalizedName);
            command.Parameters.AddWithValue("motd", NpgsqlDbType.Text, guild.Motd);
            command.Parameters.AddWithValue("creatorAccountId", NpgsqlDbType.Bigint, guild.CreatorDbGuid);
            command.Parameters.AddWithValue("creationTime", NpgsqlDbType.Bigint, guild.CreationTime);
        }

        private static void AddMemberParameters(NpgsqlCommand command, DBGuildMember member)
        {
            command.Parameters.AddWithValue("playerId", NpgsqlDbType.Bigint, member.PlayerDbGuid);
            command.Parameters.AddWithValue("guildId", NpgsqlDbType.Bigint, member.GuildId);
            command.Parameters.AddWithValue("membership", NpgsqlDbType.Integer, checked((int)member.Membership));
        }

        private static GuildMetadata ReadMetadata(NpgsqlDataReader reader)
        {
            return new GuildMetadata(reader.GetInt64(0), reader.GetDateTime(1), reader.GetDateTime(2));
        }

        private static bool CanWrite(DBGuild guild, out GuildStoreResult result)
        {
            if (guild == null)
            {
                result = GuildStoreResult.InvalidData;
                return false;
            }
            if (guild.PersistenceState == PersistenceState.OutcomeUncertain)
            {
                result = GuildStoreResult.OutcomeUncertain;
                return false;
            }
            result = GuildStoreResult.Success;
            return true;
        }

        private static GuildStoreResult CompleteWrite(DBGuild guild, PostgreSQLWriteResult write, GuildMetadata? metadata, Func<PostgreSQLPersistenceFailure, GuildStoreResult> mapFailure, GuildStoreResult noRowResult = GuildStoreResult.Failed)
        {
            if (write.Outcome == PostgreSQLWriteOutcome.OutcomeUncertain)
            {
                guild.PersistenceState = PersistenceState.OutcomeUncertain;
                return GuildStoreResult.OutcomeUncertain;
            }
            if (write.Outcome == PostgreSQLWriteOutcome.Failed)
                return mapFailure(write.Failure);
            if (metadata.HasValue == false)
                return noRowResult;

            guild.PersistenceRevision = metadata.Value.Revision;
            guild.CreatedAtUtc = metadata.Value.CreatedAtUtc;
            guild.UpdatedAtUtc = metadata.Value.UpdatedAtUtc;
            guild.PersistenceState = PersistenceState.Clean;
            return GuildStoreResult.Success;
        }

        private static GuildStoreResult MapWriteFailure(PostgreSQLPersistenceFailure failure)
        {
            return failure?.ConstraintName switch
            {
                GuildNameConstraint => GuildStoreResult.NameConflict,
                GuildMemberPrimaryKeyConstraint or GuildLeaderConstraint => GuildStoreResult.MembershipConflict,
                _ => GuildStoreResult.Failed,
            };
        }

        private readonly record struct GuildMetadata(long Revision, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

        private sealed class GuildWriteAbortedException : Exception
        {
        }
    }
}
