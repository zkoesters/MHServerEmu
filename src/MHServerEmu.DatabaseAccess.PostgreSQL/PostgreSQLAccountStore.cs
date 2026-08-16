using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Validation;
using Npgsql;
using NpgsqlTypes;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal sealed class PostgreSQLAccountStore : IAccountStore
    {
        private const string AccountTable = "mhserveremu.account";
        private const string EmailConstraint = "account_normalized_email_unique";
        private const string PlayerNameConstraint = "account_normalized_player_name_unique";
        private const int PasswordAlgorithm = 1;
        private const int InitialCredentialVersion = 1;
        private const int InitialGameSecurityVersion = 1;

        private readonly NpgsqlDataSource _dataSource;
        private readonly PostgreSQLStoreExecutor _executor;

        internal PostgreSQLAccountStore(NpgsqlDataSource dataSource, PostgreSQLStoreExecutor executor)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public bool TryQueryAccountByEmail(string email, out DBAccount account)
        {
            account = null;
            string normalizedEmail;
            try
            {
                normalizedEmail = IdentityNormalizer.NormalizeEmail(email);
            }
            catch (ArgumentException)
            {
                return false;
            }

            try
            {
                PostgreSQLReadResult<DBAccount> read = _executor.ExecuteReadAsync("AccountQueryByEmail", async (connection, deadline, cancellationToken) =>
                {
                    await using NpgsqlCommand command = new($"SELECT id, email, player_name, password_hash, password_salt, password_algorithm, password_format_version, password_iterations, password_key_size, credential_version, game_security_version, user_level, flags, email_verified_at_utc, revision, created_at_utc, updated_at_utc FROM {AccountTable} WHERE normalized_email = @normalizedEmail", connection)
                    {
                        CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                    };
                    command.Parameters.AddWithValue("normalizedEmail", NpgsqlDbType.Text, normalizedEmail);
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                    return await reader.ReadAsync(cancellationToken) ? ReadAccount(reader) : null;
                }).GetAwaiter().GetResult();
                account = read.Value;
                return read.Succeeded && account != null;
            }
            catch
            {
                account = null;
                return false;
            }
        }

        public AccountStoreResult InsertAccount(DBAccount account)
        {
            if (CanWrite(account, out AccountStoreResult result) == false)
                return result;

            string normalizedEmail;
            string normalizedPlayerName;
            try
            {
                normalizedEmail = IdentityNormalizer.NormalizeEmail(account.Email);
                normalizedPlayerName = IdentityNormalizer.NormalizePlayerName(account.PlayerName);
            }
            catch (ArgumentException)
            {
                return AccountStoreResult.InvalidData;
            }

            if (HasSupportedCredentialMetadata(account) == false)
                return AccountStoreResult.InvalidData;

            AccountMetadata? metadata = null;
            PostgreSQLWriteResult write = _executor.ExecuteWriteAsync("AccountInsert", account.Id, async (connection, transaction, cancellationToken) =>
            {
                await using NpgsqlCommand command = new($"INSERT INTO {AccountTable} (id, email, normalized_email, player_name, normalized_player_name, password_hash, password_salt, password_algorithm, password_format_version, password_iterations, password_key_size, credential_version, game_security_version, user_level, flags, email_verified_at_utc) VALUES (@id, @email, @normalizedEmail, @playerName, @normalizedPlayerName, @passwordHash, @salt, @passwordAlgorithm, @passwordFormatVersion, @passwordIterations, @passwordKeySize, @credentialVersion, @gameSecurityVersion, @userLevel, @flags, @emailVerifiedAtUtc) RETURNING revision, created_at_utc, updated_at_utc, credential_version, game_security_version, flags", connection, transaction);
                AddInsertParameters(command, account, normalizedEmail, normalizedPlayerName);
                using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                    metadata = ReadMetadata(reader, includesCreatedAt: true);
            }).GetAwaiter().GetResult();

            if (write.Outcome == PostgreSQLWriteOutcome.OutcomeUncertain)
            {
                account.PersistenceState = PersistenceState.OutcomeUncertain;
                return AccountStoreResult.OutcomeUncertain;
            }

            if (write.Outcome == PostgreSQLWriteOutcome.Failed)
                return MapWriteFailure(write);

            if (metadata.HasValue == false)
                return AccountStoreResult.Failed;

            ApplyMetadata(account, metadata.Value);
            return AccountStoreResult.Success;
        }

        public AccountStoreResult ChangePlayerName(DBAccount account, string playerName)
        {
            if (CanWrite(account, out AccountStoreResult result) == false)
                return result;

            string normalizedPlayerName;
            try
            {
                normalizedPlayerName = IdentityNormalizer.NormalizePlayerName(playerName);
            }
            catch (ArgumentException)
            {
                return AccountStoreResult.InvalidData;
            }

            result = UpdateAccount(account, "AccountChangePlayerName", $"UPDATE {AccountTable} SET player_name = @playerName, normalized_player_name = @normalizedPlayerName, revision = revision + 1, updated_at_utc = CURRENT_TIMESTAMP WHERE id = @id AND revision = @revision RETURNING revision, updated_at_utc, credential_version, game_security_version, flags", parameters =>
            {
                parameters.AddWithValue("playerName", NpgsqlDbType.Text, playerName);
                parameters.AddWithValue("normalizedPlayerName", NpgsqlDbType.Text, normalizedPlayerName);
            });
            if (result == AccountStoreResult.Success)
                account.PlayerName = playerName;
            return result;
        }

        public AccountStoreResult ChangePassword(DBAccount account, byte[] passwordHash, byte[] salt)
        {
            if (CanWrite(account, out AccountStoreResult result) == false)
                return result;
            if (passwordHash == null || salt == null)
                return AccountStoreResult.InvalidData;

            result = UpdateAccount(account, "AccountChangePassword", $"UPDATE {AccountTable} SET password_hash = @passwordHash, password_salt = @salt, flags = flags & ~@passwordExpiredFlag, credential_version = credential_version + 1, game_security_version = game_security_version + 1, revision = revision + 1, updated_at_utc = CURRENT_TIMESTAMP WHERE id = @id AND revision = @revision RETURNING revision, updated_at_utc, credential_version, game_security_version, flags", parameters =>
            {
                parameters.AddWithValue("passwordHash", NpgsqlDbType.Bytea, passwordHash);
                parameters.AddWithValue("salt", NpgsqlDbType.Bytea, salt);
                parameters.AddWithValue("passwordExpiredFlag", NpgsqlDbType.Integer, (int)AccountFlags.IsPasswordExpired);
            });
            if (result == AccountStoreResult.Success)
            {
                account.PasswordHash = passwordHash;
                account.Salt = salt;
            }
            return result;
        }

        public AccountStoreResult ChangeUserLevel(DBAccount account, AccountUserLevel userLevel)
        {
            if (CanWrite(account, out AccountStoreResult result) == false)
                return result;

            result = UpdateAccount(account, "AccountChangeUserLevel", $"UPDATE {AccountTable} SET user_level = @userLevel, game_security_version = game_security_version + 1, revision = revision + 1, updated_at_utc = CURRENT_TIMESTAMP WHERE id = @id AND revision = @revision RETURNING revision, updated_at_utc, credential_version, game_security_version, flags", parameters => parameters.AddWithValue("userLevel", NpgsqlDbType.Integer, (int)userLevel));
            if (result == AccountStoreResult.Success)
                account.UserLevel = userLevel;
            return result;
        }

        public AccountStoreResult ChangeFlags(DBAccount account, AccountFlags flags)
        {
            if (CanWrite(account, out AccountStoreResult result) == false)
                return result;

            result = UpdateAccount(account, "AccountChangeFlags", $"UPDATE {AccountTable} SET flags = @flags, game_security_version = game_security_version + 1, revision = revision + 1, updated_at_utc = CURRENT_TIMESTAMP WHERE id = @id AND revision = @revision RETURNING revision, updated_at_utc, credential_version, game_security_version, flags", parameters => parameters.AddWithValue("flags", NpgsqlDbType.Integer, (int)flags));
            return result;
        }

        private AccountStoreResult UpdateAccount(DBAccount account, string operation, string sql, Action<NpgsqlParameterCollection> addParameters)
        {
            AccountMetadata? metadata = null;
            bool exists = false;
            PostgreSQLWriteResult write = _executor.ExecuteWriteAsync(operation, account.Id, async (connection, transaction, cancellationToken) =>
            {
                await using NpgsqlCommand command = new(sql, connection, transaction);
                command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, account.Id);
                command.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, account.PersistenceRevision);
                addParameters(command.Parameters);
                using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    if (await reader.ReadAsync(cancellationToken))
                        metadata = ReadMetadata(reader, includesCreatedAt: false);
                }

                if (metadata.HasValue == false)
                {
                    await using NpgsqlCommand existsCommand = new($"SELECT 1 FROM {AccountTable} WHERE id = @id LIMIT 1", connection, transaction);
                    existsCommand.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, account.Id);
                    exists = await existsCommand.ExecuteScalarAsync(cancellationToken) != null;
                }
            }).GetAwaiter().GetResult();

            if (write.Outcome == PostgreSQLWriteOutcome.OutcomeUncertain)
            {
                account.PersistenceState = PersistenceState.OutcomeUncertain;
                return AccountStoreResult.OutcomeUncertain;
            }

            if (write.Outcome == PostgreSQLWriteOutcome.Failed)
                return MapWriteFailure(write);
            if (metadata.HasValue == false)
                return exists ? AccountStoreResult.StaleRevision : AccountStoreResult.AccountNotFound;

            ApplyMetadata(account, metadata.Value);
            return AccountStoreResult.Success;
        }

        private static bool CanWrite(DBAccount account, out AccountStoreResult result)
        {
            if (account == null)
            {
                result = AccountStoreResult.InvalidData;
                return false;
            }
            if (account.PersistenceState == PersistenceState.OutcomeUncertain)
            {
                result = AccountStoreResult.OutcomeUncertain;
                return false;
            }

            result = AccountStoreResult.Success;
            return true;
        }

        private static bool HasSupportedCredentialMetadata(DBAccount account)
        {
            return account.PasswordHash != null
                && account.Salt != null
                && account.PasswordAlgorithm == Core.Helpers.CryptographyHelper.PasswordAlgorithm
                && account.PasswordFormatVersion == Core.Helpers.CryptographyHelper.PasswordFormatVersion
                && account.PasswordIterations == Core.Helpers.CryptographyHelper.PasswordIterationCount
                && account.PasswordKeySize == Core.Helpers.CryptographyHelper.PasswordKeySize;
        }

        private static void AddInsertParameters(NpgsqlCommand command, DBAccount account, string normalizedEmail, string normalizedPlayerName)
        {
            command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, account.Id);
            command.Parameters.AddWithValue("email", NpgsqlDbType.Text, account.Email);
            command.Parameters.AddWithValue("normalizedEmail", NpgsqlDbType.Text, normalizedEmail);
            command.Parameters.AddWithValue("playerName", NpgsqlDbType.Text, account.PlayerName);
            command.Parameters.AddWithValue("normalizedPlayerName", NpgsqlDbType.Text, normalizedPlayerName);
            command.Parameters.AddWithValue("passwordHash", NpgsqlDbType.Bytea, account.PasswordHash);
            command.Parameters.AddWithValue("salt", NpgsqlDbType.Bytea, account.Salt);
            command.Parameters.AddWithValue("passwordAlgorithm", NpgsqlDbType.Integer, PasswordAlgorithm);
            command.Parameters.AddWithValue("passwordFormatVersion", NpgsqlDbType.Integer, account.PasswordFormatVersion);
            command.Parameters.AddWithValue("passwordIterations", NpgsqlDbType.Integer, account.PasswordIterations);
            command.Parameters.AddWithValue("passwordKeySize", NpgsqlDbType.Integer, account.PasswordKeySize);
            command.Parameters.AddWithValue("credentialVersion", NpgsqlDbType.Integer, InitialCredentialVersion);
            command.Parameters.AddWithValue("gameSecurityVersion", NpgsqlDbType.Integer, InitialGameSecurityVersion);
            command.Parameters.AddWithValue("userLevel", NpgsqlDbType.Integer, (int)account.UserLevel);
            command.Parameters.AddWithValue("flags", NpgsqlDbType.Integer, (int)account.Flags);
            command.Parameters.AddWithValue("emailVerifiedAtUtc", NpgsqlDbType.TimestampTz, account.EmailVerifiedAtUtc ?? (object)DBNull.Value);
        }

        private static DBAccount ReadAccount(NpgsqlDataReader reader)
        {
            return new DBAccount
            {
                Id = reader.GetInt64(0),
                Email = reader.GetString(1),
                PlayerName = reader.GetString(2),
                PasswordHash = reader.GetFieldValue<byte[]>(3),
                Salt = reader.GetFieldValue<byte[]>(4),
                PasswordAlgorithm = reader.GetInt32(5) == PasswordAlgorithm ? Core.Helpers.CryptographyHelper.PasswordAlgorithm : null,
                PasswordFormatVersion = reader.GetInt32(6),
                PasswordIterations = reader.GetInt32(7),
                PasswordKeySize = reader.GetInt32(8),
                CredentialVersion = reader.GetInt32(9),
                GameSecurityVersion = reader.GetInt32(10),
                UserLevel = (AccountUserLevel)reader.GetInt32(11),
                Flags = (AccountFlags)reader.GetInt32(12),
                EmailVerifiedAtUtc = ReadNullableDateTime(reader, 13),
                PersistenceRevision = reader.GetInt64(14),
                CreatedAtUtc = reader.GetDateTime(15),
                UpdatedAtUtc = reader.GetDateTime(16),
                PersistenceState = PersistenceState.Clean,
            };
        }

        private static AccountMetadata ReadMetadata(NpgsqlDataReader reader, bool includesCreatedAt)
        {
            int index = 0;
            long revision = reader.GetInt64(index++);
            DateTime? createdAtUtc = includesCreatedAt ? reader.GetDateTime(index++) : null;
            DateTime updatedAtUtc = reader.GetDateTime(index++);
            int credentialVersion = reader.GetInt32(index++);
            int gameSecurityVersion = reader.GetInt32(index++);
            AccountFlags flags = (AccountFlags)reader.GetInt32(index);
            return new AccountMetadata(revision, createdAtUtc, updatedAtUtc, credentialVersion, gameSecurityVersion, flags);
        }

        private static DateTime? ReadNullableDateTime(NpgsqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
        }

        private static void ApplyMetadata(DBAccount account, AccountMetadata metadata)
        {
            account.PersistenceRevision = metadata.Revision;
            account.CreatedAtUtc = metadata.CreatedAtUtc ?? account.CreatedAtUtc;
            account.UpdatedAtUtc = metadata.UpdatedAtUtc;
            account.CredentialVersion = metadata.CredentialVersion;
            account.GameSecurityVersion = metadata.GameSecurityVersion;
            account.Flags = metadata.Flags;
            account.PersistenceState = PersistenceState.Clean;
        }

        private static AccountStoreResult MapWriteFailure(PostgreSQLWriteResult write)
        {
            return write.Failure?.ConstraintName switch
            {
                EmailConstraint => AccountStoreResult.EmailConflict,
                PlayerNameConstraint => AccountStoreResult.PlayerNameConflict,
                _ => AccountStoreResult.Failed,
            };
        }

        private readonly record struct AccountMetadata(long Revision, DateTime? CreatedAtUtc, DateTime UpdatedAtUtc, int CredentialVersion, int GameSecurityVersion, AccountFlags Flags);
    }
}
