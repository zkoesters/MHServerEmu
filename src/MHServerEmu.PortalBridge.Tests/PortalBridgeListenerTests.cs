using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Core.Network;
using MHServerEmu.PortalBridge.Handlers;

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
        [InlineData(PortalAuthenticationWebHandler.ChangePasswordPath, "{\"identifier\":\"player@example.test\",\"currentPassword\":\"correct horse battery staple\",\"newPassword\":\"new correct horse battery staple\"}")]
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
        public async Task ChangePassword_EmailOrPlayerNameWithCorrectCurrentPassword_UpdatesCredentials(string identifier)
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent($"{{\"identifier\":\"{identifier}\",\"currentPassword\":\"correct horse battery staple\",\"newPassword\":\"new correct horse battery staple\"}}"));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            Assert.False(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount(identifier,
                "correct horse battery staple", out _));
            Assert.True(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount(identifier,
                "new correct horse battery staple", out _));
        }

        [Fact]
        public async Task ChangePassword_WrongCurrentPassword_ReturnsGenericProblemWithoutUpdatingCredentials()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent("{\"identifier\":\"StarLord\",\"currentPassword\":\"wrong password\",\"newPassword\":\"new correct horse battery staple\"}"));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("invalid_credentials", json.RootElement.GetProperty("code").GetString());
            Assert.True(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount("StarLord",
                "correct horse battery staple", out _));
        }

        [Fact]
        public async Task ChangePassword_InvalidNewPassword_ReturnsGenericProblemWithoutUpdatingCredentials()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();
            await client.PostAsync(PortalAuthenticationWebHandler.RegisterPath,
                JsonContent("{\"email\":\"player@example.test\",\"playerName\":\"StarLord\",\"password\":\"correct horse battery staple\"}"));

            using HttpResponseMessage response = await client.PostAsync(PortalAuthenticationWebHandler.ChangePasswordPath,
                JsonContent("{\"identifier\":\"StarLord\",\"currentPassword\":\"correct horse battery staple\",\"newPassword\":\"no\"}"));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("invalid_credentials", json.RootElement.GetProperty("code").GetString());
            Assert.True(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount("StarLord",
                "correct horse battery staple", out _));
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
                JsonContent("{\"identifier\":\"StarLord\",\"currentPassword\":\"correct horse battery staple\",\"newPassword\":\"new correct horse battery staple\"}"));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("account_service_unavailable", json.RootElement.GetProperty("code").GetString());
            Assert.True(MHServerEmu.PlayerManagement.Players.AccountManager.TryVerifyAccount("StarLord",
                "correct horse battery staple", out _));
        }

        [Fact]
        public async Task ChangePassword_BodyMutatedAfterSigning_ReturnsUnauthorized()
        {
            using AccountDatabaseScope database = new();
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(PortalAuthenticationWebHandler.ChangePasswordPath,
                HttpMethod.Post, content: JsonContent("{\"identifier\":\"StarLord\",\"currentPassword\":\"correct horse battery staple\",\"newPassword\":\"new correct horse battery staple\"}"));
            request.Content = JsonContent("{\"identifier\":\"StarLord\",\"currentPassword\":\"correct horse battery staple\",\"newPassword\":\"another correct horse battery staple\"}");

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

        private sealed class InMemoryAccountDatabase : IDBManager
        {
            private readonly Dictionary<string, DBAccount> _accountsByEmail = new(StringComparer.OrdinalIgnoreCase);
            private readonly bool _verifyAccounts;
            private readonly bool _updateAccounts;

            public DBAccount Account { get => Assert.Single(_accountsByEmail.Values); }
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
