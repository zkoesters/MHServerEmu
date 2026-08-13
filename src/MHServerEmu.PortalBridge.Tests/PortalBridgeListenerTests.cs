using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dapper;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.SQLite;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Network;
using MHServerEmu.PlayerManagement.Players;
using MHServerEmu.PortalBridge.Handlers;
using System.Data.SQLite;

namespace MHServerEmu.PortalBridge.Tests
{
    [Collection("PortalBridge logging")]
    public class PortalBridgeListenerTests
    {
        [Fact]
        public async Task Register_ValidCredentials_ReturnsOnlyOpaqueAccountId()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new[] { "emulatorAccountId" }, json.RootElement.EnumerateObject().Select(property => property.Name));
            string externalAccountId = json.RootElement.GetProperty("emulatorAccountId").GetString();
            Assert.StartsWith("acct_", externalAccountId, StringComparison.Ordinal);
            Assert.DoesNotContain(database.Database.Account.Id.ToString(), externalAccountId, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(PortalAuthenticationWebHandler.RegisterPath, "{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}")]
        [InlineData(PortalAuthenticationWebHandler.VerifyPath, "{\"identifier\":\"player@example.test\",\"password\":\"correct horse battery staple\"}")]
        [InlineData(PortalAuthenticationWebHandler.ChangePasswordPath, "{\"identifier\":\"player@example.test\",\"currentPassword\":\"correct horse battery staple\",\"newPassword\":\"new correct horse battery staple\",\"operationId\":\"3dc599bd-130b-4f65-8f1e-c22c94a0d7bc\"}")]
        [InlineData(PortalAuthenticationWebHandler.GetPasswordChangeStatusPath, "{\"identifier\":\"player@example.test\",\"operationId\":\"3dc599bd-130b-4f65-8f1e-c22c94a0d7bc\"}")]
        public async Task Authentication_CredentialsUnsupportedBackend_ReturnsUnavailable(string path, string requestBody)
        {
            using AccountDatabaseScope database = new(verifyAccounts: false);
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();

            using HttpResponseMessage response = await client.PostAsync(path, JsonContent(requestBody));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("account_service_unavailable", json.RootElement.GetProperty("code").GetString());
        }

        [Fact]
        public async Task Register_SignedNonEmptyBody_ReturnsAccount()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();

            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Register_BodyMutatedAfterSigning_ReturnsUnauthorized()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(PortalAuthenticationWebHandler.RegisterPath, HttpMethod.Post,
                content: JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));
            request.Content = JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"Nova\",\"password\":\"correct horse battery staple\"}");

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Theory]
        [InlineData("{\"email\":\"player@example.test\",\"playerName\":\"Nova\",\"password\":\"correct horse battery staple\"}")]
        [InlineData("{\"email\":\"other@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}")]
        public async Task Register_DuplicateEmailOrPlayerName_ReturnsConflict(string duplicateRequest)
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath, JsonContent(duplicateRequest));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("account_conflict", json.RootElement.GetProperty("code").GetString());
        }

        [Theory]
        [InlineData("player@example.test")]
        [InlineData("StarLord")]
        public async Task Verify_EmailOrPlayerNameWithCorrectPassword_ReturnsOpaqueAccountId(string identifier)
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            using HttpResponseMessage registration = await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));
            using JsonDocument registrationJson = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
            string externalAccountId = registrationJson.RootElement.GetProperty("emulatorAccountId").GetString();

            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.VerifyPath,
                JsonContent($"{{\"identifier\":\"{identifier}\",\"password\":\"correct horse battery staple\"}}"));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new[] { "emulatorAccountId" }, json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(externalAccountId, json.RootElement.GetProperty("emulatorAccountId").GetString());
        }

        [Theory]
        [InlineData("player@example.test", "wrong password")]
        [InlineData("missing@example.test", "correct horse battery staple")]
        public async Task Verify_InvalidCredentials_ReturnsGenericProblem(string identifier, string password)
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.VerifyPath,
                JsonContent($"{{\"identifier\":\"{identifier}\",\"password\":\"{password}\"}}"));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("invalid_credentials", json.RootElement.GetProperty("code").GetString());
            Assert.Equal(new[] { "code", "correlationId" }, json.RootElement.EnumerateObject().Select(property => property.Name));
        }

        [Theory]
        [InlineData("player@example.test")]
        [InlineData("StarLord")]
        public async Task ChangePassword_SucceededOperation_ReplaysWithoutRecheckingCredentials(string identifier)
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            Guid operationId = Guid.NewGuid();
            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent(ChangePasswordJson(identifier, "correct horse battery staple", "new correct horse battery staple", operationId)));
            using HttpResponseMessage replay = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent(ChangePasswordJson(identifier, "wrong password", "another correct horse battery staple", operationId)));

            await AssertOutcomeAsync(response, "succeeded");
            await AssertOutcomeAsync(replay, "succeeded");
            Assert.False(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount(identifier,
                "correct horse battery staple", out _));
            Assert.True(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount(identifier,
                "new correct horse battery staple", out _));
            Assert.False(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount(identifier,
                "another correct horse battery staple", out _));
        }

        [Fact]
        public async Task ChangePassword_RejectedOperation_ReplaysWithoutUpdatingCredentials()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            Guid operationId = Guid.NewGuid();
            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent(ChangePasswordJson("StarLord", "wrong password", "new correct horse battery staple", operationId)));
            using HttpResponseMessage replay = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent(ChangePasswordJson("StarLord", "correct horse battery staple", "new correct horse battery staple", operationId)));

            await AssertOutcomeAsync(response, "rejected");
            await AssertOutcomeAsync(replay, "rejected");
            Assert.True(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount("StarLord",
                "correct horse battery staple", out _));
        }

        [Fact]
        public async Task ChangePassword_InvalidNewPassword_ReturnsRejectedWithoutUpdatingCredentials()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent(ChangePasswordJson("StarLord", "correct horse battery staple", "no", Guid.NewGuid())));

            await AssertOutcomeAsync(response, "rejected");
            Assert.True(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount("StarLord",
                "correct horse battery staple", out _));
        }

        [Fact]
        public async Task ChangePassword_MalformedJson_ReturnsUnavailable()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();

            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent("{\"identifier\":\"StarLord\""));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("account_service_unavailable", json.RootElement.GetProperty("code").GetString());
        }

        [Fact]
        public async Task ChangePassword_UnknownField_ReturnsUnavailableWithoutMutationOrOperation()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            Guid operationId = Guid.NewGuid();
            string requestBody = ChangePasswordJson("StarLord", "correct horse battery staple",
                "new correct horse battery staple", operationId);
            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent($"{requestBody[..^1]},\"extra\":true}}"));

            await AssertUnavailableAsync(response);
            Assert.Equal(0, database.Database.PasswordChangeOperationCount);
            Assert.True(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount("StarLord",
                "correct horse battery staple", out _));
        }

        [Fact]
        public async Task GetPasswordChangeStatus_CredentialField_ReturnsUnavailableWithoutOperation()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            Guid operationId = Guid.NewGuid();
            string requestBody = StatusJson("StarLord", operationId);
            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.GetPasswordChangeStatusPath,
                JsonContent($"{requestBody[..^1]},\"currentPassword\":\"correct horse battery staple\"}}"));

            await AssertUnavailableAsync(response);
            Assert.Equal(0, database.Database.PasswordChangeOperationCount);
        }

        [Fact]
        public async Task ChangePassword_PersistenceFailure_ReturnsUnavailableWithoutUpdatingCredentials()
        {
            using AccountDatabaseScope database = new(updateAccounts: false);
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent(ChangePasswordJson("StarLord", "correct horse battery staple", "new correct horse battery staple", Guid.NewGuid())));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("account_service_unavailable", json.RootElement.GetProperty("code").GetString());
            Assert.True(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount("StarLord",
                "correct horse battery staple", out _));
        }

        [Fact]
        public async Task GetPasswordChangeStatus_UnseenOperation_CancelsDelayedMutation()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            Guid operationId = Guid.NewGuid();
            using HttpResponseMessage status = await client.PostAsync(PortalAuthenticationWebHandler.GetPasswordChangeStatusPath,
                JsonContent(StatusJson("StarLord", operationId)));
            using HttpResponseMessage mutation = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent(ChangePasswordJson("StarLord", "correct horse battery staple", "new correct horse battery staple", operationId)));

            await AssertOutcomeAsync(status, "cancelled");
            await AssertOutcomeAsync(mutation, "cancelled");
            Assert.True(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount("StarLord",
                "correct horse battery staple", out _));
        }

        [Fact]
        public async Task ChangePassword_BodyMutatedAfterSigning_ReturnsUnauthorized()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(PortalAuthenticationWebHandler.ChangePasswordPath,
                HttpMethod.Post, content: JsonContent(ChangePasswordJson("StarLord", "correct horse battery staple", "new correct horse battery staple", Guid.NewGuid())));
            request.Content = JsonContent(ChangePasswordJson("StarLord", "correct horse battery staple", "another correct horse battery staple", Guid.NewGuid()));

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public void SQLitePasswordOperations_MigrateFromVersion6AndPersistTerminalOutcomesAcrossManagerInstances()
        {
            using SQLiteAccountDatabaseScope database = new();
            SQLiteDBManager firstManager = database.CreateManager();
            Assert.True(firstManager.Initialize());

            DBAccount account = new("player@example.test", "StarLord", "correct horse battery staple");
            Assert.True(firstManager.InsertAccount(account));
            Guid succeededId = Guid.NewGuid();
            Guid rejectedId = Guid.NewGuid();
            Guid cancelledId = Guid.NewGuid();
            Assert.Equal(PortalPasswordChangeOperationOutcome.Succeeded, firstManager.ResolvePortalPasswordChange(account,
                succeededId, "correct horse battery staple", "new correct horse battery staple", true));
            Assert.Equal(PortalPasswordChangeOperationOutcome.Rejected, firstManager.ResolvePortalPasswordChange(account,
                rejectedId, "wrong password", "another correct horse battery staple", true));
            Assert.Equal(PortalPasswordChangeOperationOutcome.Cancelled,
                firstManager.GetPortalPasswordChangeStatus(account, cancelledId));

            SQLiteDBManager reloadedManager = database.CreateManager();
            Assert.True(reloadedManager.Initialize());
            IDBManager.Instance = reloadedManager;
            Assert.True(reloadedManager.TryQueryAccountByPlayerName("StarLord", out DBAccount reloadedAccount));
            Assert.Equal(PortalPasswordChangeOperationOutcome.Succeeded, reloadedManager.ResolvePortalPasswordChange(reloadedAccount,
                succeededId, "wrong password", "another correct horse battery staple", true));
            Assert.Equal(PortalPasswordChangeOperationOutcome.Rejected, reloadedManager.ResolvePortalPasswordChange(reloadedAccount,
                rejectedId, "new correct horse battery staple", "another correct horse battery staple", true));
            Assert.Equal(PortalPasswordChangeOperationOutcome.Cancelled, reloadedManager.ResolvePortalPasswordChange(reloadedAccount,
                cancelledId, "new correct horse battery staple", "another correct horse battery staple", true));
            Assert.True(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount("StarLord",
                "new correct horse battery staple", out _));

            using SQLiteConnection connection = new($"Data Source={database.DatabasePath}");
            connection.Open();
            Assert.Equal(7, connection.QuerySingle<int>("PRAGMA user_version"));
            Assert.Equal(new[] { "AccountId", "OperationId", "Outcome", "CreatedAt" }, connection.Query<string>(
                "SELECT name FROM pragma_table_info('PortalPasswordChangeOperation') ORDER BY cid"));
            Assert.Equal(new[] { "cancelled", "rejected", "succeeded" }, connection.Query<string>(
                "SELECT Outcome FROM PortalPasswordChangeOperation ORDER BY Outcome"));
        }

        [Fact]
        public async Task SQLitePasswordOperations_ConcurrentStaleSnapshots_OnlyOneChangesPassword()
        {
            using SQLiteAccountDatabaseScope database = new();
            SQLiteDBManager firstManager = database.CreateManager();
            SQLiteDBManager secondManager = database.CreateManager();
            Assert.True(firstManager.Initialize());
            Assert.True(secondManager.Initialize());
            DBAccount createdAccount = new("player@example.test", "StarLord", "correct horse battery staple");
            Assert.True(firstManager.InsertAccount(createdAccount));
            Assert.True(firstManager.TryQueryAccountByPlayerName("StarLord", out DBAccount firstSnapshot));
            Assert.True(secondManager.TryQueryAccountByPlayerName("StarLord", out DBAccount secondSnapshot));

            Guid firstOperationId = Guid.NewGuid();
            Guid secondOperationId = Guid.NewGuid();
            Task<PortalPasswordChangeOperationOutcome> first = Task.Run(() => firstManager.ResolvePortalPasswordChange(
                firstSnapshot, firstOperationId, "correct horse battery staple", "first new password", true));
            Task<PortalPasswordChangeOperationOutcome> second = Task.Run(() => secondManager.ResolvePortalPasswordChange(
                secondSnapshot, secondOperationId, "correct horse battery staple", "second new password", true));
            PortalPasswordChangeOperationOutcome[] outcomes = await Task.WhenAll(first, second);

            Assert.Equal(1, outcomes.Count(outcome => outcome == PortalPasswordChangeOperationOutcome.Succeeded));
            Assert.Equal(1, outcomes.Count(outcome => outcome == PortalPasswordChangeOperationOutcome.Rejected));
            SQLiteDBManager reloadedManager = database.CreateManager();
            Assert.True(reloadedManager.Initialize());
            Assert.True(reloadedManager.TryQueryAccountByPlayerName("StarLord", out DBAccount reloadedAccount));
            Assert.Equal(outcomes[0], reloadedManager.ResolvePortalPasswordChange(reloadedAccount, firstOperationId,
                "wrong password", "ignored", true));
            Assert.Equal(outcomes[1], reloadedManager.ResolvePortalPasswordChange(reloadedAccount, secondOperationId,
                "wrong password", "ignored", true));
            Assert.True(MHServerEmu.Core.Helpers.CryptographyHelper.VerifyPassword("first new password", reloadedAccount.PasswordHash,
                reloadedAccount.Salt) || MHServerEmu.Core.Helpers.CryptographyHelper.VerifyPassword("second new password",
                    reloadedAccount.PasswordHash, reloadedAccount.Salt));
            Assert.False(MHServerEmu.Core.Helpers.CryptographyHelper.VerifyPassword("first new password", reloadedAccount.PasswordHash,
                reloadedAccount.Salt) && MHServerEmu.Core.Helpers.CryptographyHelper.VerifyPassword("second new password",
                    reloadedAccount.PasswordHash, reloadedAccount.Salt));
            using SQLiteConnection connection = new($"Data Source={database.DatabasePath}");
            connection.Open();
            Assert.Equal(new[] { "rejected", "succeeded" }, connection.Query<string>(
                "SELECT Outcome FROM PortalPasswordChangeOperation ORDER BY Outcome"));
        }

        [Fact]
        public async Task SQLitePasswordOperations_PortalAndLegacyMutationsShareAccountPasswordLock()
        {
            using SQLiteAccountDatabaseScope database = new();
            SQLiteDBManager manager = database.CreateManager();
            Assert.True(manager.Initialize());
            IDBManager.Instance = manager;
            Assert.Equal(AccountOperationResult.Success, AccountManager.CreateAccount("player@example.test", "StarLord",
                "correct horse battery staple"));

            object passwordLock = typeof(AccountManager).GetField("AccountPasswordLock",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Guid operationId = Guid.NewGuid();
            Monitor.Enter(passwordLock);
            bool lockHeld = true;
            Task<PortalPasswordChangeOperationOutcome> portal;
            Task<AccountOperationResult> legacy;
            try
            {
                portal = Task.Run(() => AccountManager.ChangePortalPassword("StarLord",
                    operationId, "correct horse battery staple", "portal new password"));
                legacy = Task.Run(() => AccountManager.ChangeAccountPassword("StarLord",
                    "correct horse battery staple", "legacy new password"));
                Thread.Sleep(100);
                Assert.False(portal.IsCompleted);
                Assert.False(legacy.IsCompleted);

                Monitor.Exit(passwordLock);
                lockHeld = false;
            }
            finally
            {
                if (lockHeld)
                    Monitor.Exit(passwordLock);
            }

            PortalPasswordChangeOperationOutcome portalOutcome = await portal;
            AccountOperationResult legacyOutcome = await legacy;
            Assert.True((portalOutcome == PortalPasswordChangeOperationOutcome.Succeeded &&
                legacyOutcome == AccountOperationResult.EmailNotFound) ||
                (portalOutcome == PortalPasswordChangeOperationOutcome.Rejected &&
                legacyOutcome == AccountOperationResult.Success));
            Assert.True(AccountManager.TryVerifyAccount("StarLord", portalOutcome == PortalPasswordChangeOperationOutcome.Succeeded
                ? "portal new password"
                : "legacy new password", out _));
            Assert.True(manager.TryQueryAccountByPlayerName("StarLord", out DBAccount account));
            Assert.Equal(portalOutcome, manager.ResolvePortalPasswordChange(account, operationId,
                "wrong password", "ignored", true));
        }

        [Fact]
        public void SQLiteMigration_WalDatabasePreservesVersion6Data()
        {
            using SQLiteAccountDatabaseScope database = new(enableWal: true);
            using SQLiteConnection writer = new($"Data Source={database.DatabasePath}");
            writer.Open();
            writer.Execute("PRAGMA wal_autocheckpoint = 0");
            writer.Execute(@"INSERT INTO Account (Id, Email, PlayerName, PasswordHash, Salt, UserLevel, Flags)
                VALUES (42, 'player@example.test', 'StarLord', X'0102', X'0304', 0, 0)");
            using SQLiteConnection reader = new($"Data Source={database.DatabasePath}");
            reader.Open();
            reader.Execute("BEGIN");
            Assert.Equal("StarLord", reader.QuerySingle<string>("SELECT PlayerName FROM Account WHERE Id = 42"));
            writer.Execute(@"INSERT INTO Account (Id, Email, PlayerName, PasswordHash, Salt, UserLevel, Flags)
                VALUES (43, 'other@example.test', 'Nova', X'0506', X'0708', 0, 0)");
            Assert.True(new FileInfo($"{database.DatabasePath}-wal").Length > 0);

            SQLiteDBManager manager = database.CreateManager();

            Assert.True(manager.Initialize());
            Assert.Equal("StarLord", reader.QuerySingle<string>("SELECT PlayerName FROM Account WHERE Id = 42"));
            reader.Execute("ROLLBACK");
            using SQLiteConnection migratedConnection = new($"Data Source={database.DatabasePath}");
            migratedConnection.Open();
            Assert.Equal(7, migratedConnection.QuerySingle<int>("PRAGMA user_version"));
            Assert.Equal("StarLord", migratedConnection.QuerySingle<string>("SELECT PlayerName FROM Account WHERE Id = 42"));
            Assert.Equal("Nova", migratedConnection.QuerySingle<string>("SELECT PlayerName FROM Account WHERE Id = 43"));
            Assert.True(migratedConnection.QuerySingle<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'PortalPasswordChangeOperation'") == 1);
        }

        [Fact]
        public void SQLiteMigration_ConflictingOperationTable_FailsWithoutSchemaOrVersionChange()
        {
            using SQLiteAccountDatabaseScope database = new();
            using (SQLiteConnection connection = new($"Data Source={database.DatabasePath}"))
            {
                connection.Open();
                connection.Execute("CREATE TABLE PortalPasswordChangeOperation (ExistingColumn TEXT NOT NULL)");
            }

            SQLiteDBManager manager = database.CreateManager();

            Assert.False(manager.Initialize());
            using SQLiteConnection failedConnection = new($"Data Source={database.DatabasePath}");
            failedConnection.Open();
            Assert.Equal(6, failedConnection.QuerySingle<int>("PRAGMA user_version"));
            Assert.Equal(new[] { "ExistingColumn" }, failedConnection.Query<string>(
                "SELECT name FROM pragma_table_info('PortalPasswordChangeOperation') ORDER BY cid"));
        }

        [Fact]
        public async Task GetCapabilities_ValidSignature_ReturnsExactPayload()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();

            using HttpResponseMessage response = await client.GetAsync(CapabilitiesWebHandler.Path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[]
                {
                    "contractVersion",
                    "emulatorVersion",
                    "upstreamCommit",
                    "gameBuild",
                    "snapshotSchemaVersion",
                    "serverInstanceId",
                    "capabilities",
                }, json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("1.0", json.RootElement.GetProperty("contractVersion").GetString());
            Assert.Equal("1.0.2", json.RootElement.GetProperty("emulatorVersion").GetString());
            Assert.Equal(PortalBridgeBuildMetadata.UpstreamCommit, json.RootElement.GetProperty("upstreamCommit").GetString());
            Assert.Equal("1.52.0.1700", json.RootElement.GetProperty("gameBuild").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("snapshotSchemaVersion").GetInt32());
            Assert.Equal(Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a"),
                json.RootElement.GetProperty("serverInstanceId").GetGuid());
            Assert.Equal(new[] { "bridge.health" }, json.RootElement.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
        }

        [Fact]
        public async Task GetCapabilities_ReplayedSignature_ReturnsUnauthorized()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage first = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path);
            using HttpRequestMessage replay = bridge.CloneRequest(first);

            using HttpResponseMessage accepted = await bridge.Client.SendAsync(first);
            using HttpResponseMessage rejected = await bridge.Client.SendAsync(replay);

            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        [Fact]
        public async Task SignedQuery_DoesNotMatchContractRoute()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path + "?extra=true");

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetHealth_ValidSignature_ReturnsHealthyServices()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(HealthWebHandler.Path);

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "status", "checkedAtUtc", "services" },
                json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("healthy", json.RootElement.GetProperty("status").GetString());
            Assert.True(DateTimeOffset.TryParse(json.RootElement.GetProperty("checkedAtUtc").GetString(), out _));
            Assert.Equal(new[] { "bridge", "playerManager" },
                json.RootElement.GetProperty("services").EnumerateObject().Select(property => property.Name));
            Assert.Equal("healthy", json.RootElement.GetProperty("services").GetProperty("bridge").GetString());
            Assert.Equal("healthy", json.RootElement.GetProperty("services").GetProperty("playerManager").GetString());
        }

        [Fact]
        public async Task SignedUnknownPath_ReturnsNotFound()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest("/portal-bridge/v1/unknown");

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task SignedPost_ReturnsMethodNotAllowed()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path, HttpMethod.Post);

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Fact]
        public async Task TamperedPost_ReturnsUnauthorizedBeforeMethodDispatch()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path, HttpMethod.Post);
            request.Headers.Remove("X-Portal-Signature");
            request.Headers.TryAddWithoutValidation("X-Portal-Signature", new string('0', 64));

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public void Start_BindFailure_RetriesOnNewLoopbackPort()
        {
            using TcpListener occupied = new(IPAddress.Loopback, 0);
            occupied.Start();
            int occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            int attempts = 0;

            using RunningBridge bridge = RunningBridge.Start(GameServiceState.Running, () =>
            {
                attempts++;
                return attempts == 1 ? occupiedPort : GetFreePort();
            });

            Assert.True(attempts >= 2);
        }

        [Fact]
        public async Task GetCapabilities_IPv6Loopback_ReturnsExactPayloadWhenAvailable()
        {
            if (CanStartIPv6LoopbackHttpListener() == false)
                return;

            using RunningBridge bridge = RunningBridge.Start("::1");
            using HttpRequestMessage request = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path);

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[]
                {
                    "contractVersion",
                    "emulatorVersion",
                    "upstreamCommit",
                    "gameBuild",
                    "snapshotSchemaVersion",
                    "serverInstanceId",
                    "capabilities",
                }, json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("1.0", json.RootElement.GetProperty("contractVersion").GetString());
            Assert.Equal("1.0.2", json.RootElement.GetProperty("emulatorVersion").GetString());
            Assert.Equal(PortalBridgeBuildMetadata.UpstreamCommit, json.RootElement.GetProperty("upstreamCommit").GetString());
            Assert.Equal("1.52.0.1700", json.RootElement.GetProperty("gameBuild").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("snapshotSchemaVersion").GetInt32());
            Assert.Equal(Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a"),
                json.RootElement.GetProperty("serverInstanceId").GetGuid());
            Assert.Equal(new[] { "bridge.health" }, json.RootElement.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MissingOrTamperedSignature_ReturnsExactAuthenticationProblem(bool missingSignature)
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path);
            request.Headers.Remove("X-Portal-Signature");
            if (missingSignature == false)
                request.Headers.TryAddWithoutValidation("X-Portal-Signature", new string('0', 64));

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType.MediaType);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "code", "correlationId" }, json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("bridge_authentication_failed", json.RootElement.GetProperty("code").GetString());
            Assert.NotEqual(Guid.Empty, json.RootElement.GetProperty("correlationId").GetGuid());
        }

        private static int GetFreePort()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

        private static string ChangePasswordJson(string identifier, string currentPassword, string newPassword, Guid operationId)
        {
            return $"{{\"identifier\":\"{identifier}\",\"currentPassword\":\"{currentPassword}\",\"newPassword\":\"{newPassword}\",\"operationId\":\"{operationId:D}\"}}";
        }

        private static string StatusJson(string identifier, Guid operationId)
        {
            return $"{{\"identifier\":\"{identifier}\",\"operationId\":\"{operationId:D}\"}}";
        }

        private static async Task AssertOutcomeAsync(HttpResponseMessage response, string outcome)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "outcome" }, json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(outcome, json.RootElement.GetProperty("outcome").GetString());
        }

        private static async Task AssertUnavailableAsync(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("account_service_unavailable", json.RootElement.GetProperty("code").GetString());
        }

        private sealed class AccountDatabaseScope : IDisposable
        {
            private readonly IDBManager _previous = IDBManager.Instance;
            public InMemoryAccountDatabase Database { get; }

            public AccountDatabaseScope(bool verifyAccounts = true, bool updateAccounts = true)
            {
                Database = new InMemoryAccountDatabase(verifyAccounts, updateAccounts);
                IDBManager.Instance = Database;
            }

            public void Dispose()
            {
                IDBManager.Instance = _previous;
            }
        }

        private sealed class SQLiteAccountDatabaseScope : IDisposable
        {
            private readonly IDBManager _previous = IDBManager.Instance;
            private readonly string _backupPath;

            public string DatabasePath { get; }

            public SQLiteAccountDatabaseScope(bool enableWal = false)
            {
                Directory.CreateDirectory(FileHelper.DataDirectory);
                DatabasePath = Path.Combine(FileHelper.DataDirectory, "Account.db");
                _backupPath = File.Exists(DatabasePath) ? Path.GetTempFileName() : null;
                if (_backupPath != null)
                    File.Move(DatabasePath, _backupPath, true);

                File.Delete($"{DatabasePath}-wal");
                File.Delete($"{DatabasePath}-shm");
                File.Delete($"{DatabasePath}.v6");

                SQLiteConnection.CreateFile(DatabasePath);
                using SQLiteConnection connection = new($"Data Source={DatabasePath}");
                connection.Open();
                if (enableWal)
                    connection.Execute("PRAGMA journal_mode=WAL");
                connection.Execute(@"PRAGMA user_version = 6;
                    CREATE TABLE Account (
                        Id INTEGER NOT NULL UNIQUE,
                        Email TEXT NOT NULL UNIQUE,
                        PlayerName TEXT NOT NULL UNIQUE,
                        PasswordHash BLOB NOT NULL,
                        Salt BLOB NOT NULL,
                        UserLevel INTEGER NOT NULL,
                        Flags INTEGER NOT NULL,
                        PRIMARY KEY (Id)
                    );");
            }

            public SQLiteDBManager CreateManager()
            {
                return (SQLiteDBManager)Activator.CreateInstance(typeof(SQLiteDBManager), nonPublic: true);
            }

            public void Dispose()
            {
                IDBManager.Instance = _previous;
                DeleteDatabaseFiles();
                if (_backupPath != null)
                    File.Move(_backupPath, DatabasePath, true);
            }

            private void DeleteDatabaseFiles()
            {
                File.Delete(DatabasePath);
                File.Delete($"{DatabasePath}-wal");
                File.Delete($"{DatabasePath}-shm");
                File.Delete($"{DatabasePath}.v6");
            }
        }

        private sealed class InMemoryAccountDatabase : IDBManager
        {
            private readonly Dictionary<string, DBAccount> _accountsByEmail = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<Guid, PortalPasswordChangeOperationOutcome> _passwordChangeOperations = new();
            private readonly bool _verifyAccounts;
            private readonly bool _updateAccounts;

            public DBAccount Account { get => Assert.Single(_accountsByEmail.Values); }
            public int PasswordChangeOperationCount { get => _passwordChangeOperations.Count; }
            public bool VerifyAccounts { get => _verifyAccounts; }

            public InMemoryAccountDatabase(bool verifyAccounts, bool updateAccounts)
            {
                _verifyAccounts = verifyAccounts;
                _updateAccounts = updateAccounts;
            }

            public bool TryQueryAccountByEmail(string email, out DBAccount account) => _accountsByEmail.TryGetValue(email, out account);

            public bool TryQueryAccountByPlayerName(string playerName, out DBAccount account)
            {
                account = _accountsByEmail.Values.SingleOrDefault(value => string.Equals(value.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
                return account != null;
            }

            public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut)
            {
                bool found = TryQueryAccountByPlayerName(playerName, out DBAccount account);
                playerDbId = found ? (ulong)account.Id : 0;
                playerNameOut = found ? account.PlayerName : null;
                return found;
            }

            public bool InsertAccount(DBAccount account)
            {
                if (_accountsByEmail.ContainsKey(account.Email) || TryQueryAccountByPlayerName(account.PlayerName, out _))
                    return false;

                _accountsByEmail.Add(account.Email, account);
                return true;
            }

            public bool Initialize() => true;
            public bool TryGetPlayerName(ulong playerDbId, out string playerName) { playerName = null; return false; }
            public bool GetPlayerNames(Dictionary<ulong, string> playerNames) => false;
            public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime) { lastLogoutTime = 0; return false; }
            public bool UpdateAccount(DBAccount account) => _updateAccounts;
            public PortalPasswordChangeOperationOutcome ResolvePortalPasswordChange(DBAccount account, Guid operationId,
                string currentPassword, string newPassword, bool newPasswordIsValid)
            {
                if (_passwordChangeOperations.TryGetValue(operationId, out PortalPasswordChangeOperationOutcome outcome))
                    return outcome;

                if (newPasswordIsValid == false || MHServerEmu.Core.Helpers.CryptographyHelper.VerifyPassword(currentPassword,
                    account.PasswordHash, account.Salt) == false)
                    return _passwordChangeOperations[operationId] = PortalPasswordChangeOperationOutcome.Rejected;

                if (_updateAccounts == false)
                    return PortalPasswordChangeOperationOutcome.Unavailable;

                account.PasswordHash = MHServerEmu.Core.Helpers.CryptographyHelper.HashPassword(newPassword, out byte[] salt);
                account.Salt = salt;
                account.Flags &= ~AccountFlags.IsPasswordExpired;
                return _passwordChangeOperations[operationId] = PortalPasswordChangeOperationOutcome.Succeeded;
            }
            public PortalPasswordChangeOperationOutcome GetPortalPasswordChangeStatus(DBAccount account, Guid operationId)
            {
                if (_passwordChangeOperations.TryGetValue(operationId, out PortalPasswordChangeOperationOutcome outcome))
                    return outcome;

                return _passwordChangeOperations[operationId] = PortalPasswordChangeOperationOutcome.Cancelled;
            }
            public bool LoadPlayerData(DBAccount account) => false;
            public bool SavePlayerData(DBAccount account) => false;
            public bool LoadGuilds(List<DBGuild> guilds) => false;
            public bool SaveGuild(DBGuild guild) => false;
            public bool DeleteGuild(DBGuild guild) => false;
            public bool SaveGuildMember(DBGuildMember guildMember) => false;
            public bool DeleteGuildMember(DBGuildMember guildMember) => false;
        }

        private static bool CanStartIPv6LoopbackHttpListener()
        {
            if (Socket.OSSupportsIPv6 == false)
                return false;

            try
            {
                using TcpListener reservation = new(IPAddress.IPv6Loopback, 0);
                reservation.Start();
                int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();

                using HttpListener listener = new();
                listener.Prefixes.Add($"http://[::1]:{port}/");
                listener.Start();
                return true;
            }
            catch (HttpListenerException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
        }
    }
}
