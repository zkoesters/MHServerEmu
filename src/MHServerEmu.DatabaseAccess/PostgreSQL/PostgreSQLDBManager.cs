using System.Data;
using Dapper;
using FluentMigrator.Runner;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Extensions;
using MHServerEmu.DatabaseAccess.Migrations;
using MHServerEmu.DatabaseAccess.Models;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL;

public class PostgreSQLDBManager : IDBManager
{
    private const int NumPlayerDataWriteAttempts = 3; // Number of write attempts to do when saving player data
    private const int NumTestAccounts = 5;              // Number of test accounts to create for new database files

    private static readonly Logger _logger = LogManager.CreateLogger();

    private string _connectionString;

    public static PostgreSQLDBManager Instance { get; } = new();

    private PostgreSQLDBManager()
    {
    }

    public bool Initialize()
    {
        var config = ConfigManager.Instance.GetConfig<PostgreSQLDBManagerConfig>();

        _connectionString =
            $"Host={config.Host};Port={config.Port};Username={config.Username};Password={config.Password};Database={config.Database}";

        try
        {
            InitializeDatabase();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryQueryAccountByEmail(string email, out DBAccount account)
    {
        using var connection = GetConnection();
        var accounts = connection.Query<DBAccount>(
            "SELECT Id, Email, PlayerName, PasswordHash, Salt, UserLevel, Flags FROM Account WHERE Email = @Email",
            new { Email = email });
        account = accounts.FirstOrDefault();
        return account != null;
    }

    public bool QueryIsPlayerNameTaken(string playerName)
    {
        using var connection = GetConnection();
        var results = connection.Query<string>(
            "SELECT PlayerName FROM Account WHERE LOWER(PlayerName) = LOWER(@PlayerName)",
            new { PlayerName = playerName });
        return results.Any();
    }

    public bool InsertAccount(DBAccount account)
    {
        using var connection = GetConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            connection.Execute(
                "INSERT INTO Account (Id, Email, PlayerName, PasswordHash, Salt, UserLevel, Flags) VALUES (@Id, @Email, @PlayerName, @PasswordHash, @Salt, @UserLevel, @Flags)",
                account, transaction);
            transaction.Commit();
            return true;
        }
        catch (Exception e)
        {
            _logger.ErrorException(e, nameof(InsertAccount));
            transaction.Rollback();
            return false;
        }
    }

    public bool UpdateAccount(DBAccount account)
    {
        using var connection = GetConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            connection.Execute(
                @"UPDATE Account
                        SET Email = @Email,
                            PlayerName = @PlayerName,
                            PasswordHash = @PasswordHash,
                            Salt = @Salt,
                            UserLevel = @UserLevel,
                            Flags = @Flags
                        WHERE Id = @Id",
                account, transaction);
            transaction.Commit();
            return true;
        }
        catch (Exception e)
        {
            _logger.ErrorException(e, nameof(UpdateAccount));
            transaction.Rollback();
            return false;
        }
    }

    public bool LoadPlayerData(DBAccount account)
    {
        // Clear existing data
        account.Player = null;
        account.ClearEntities();

        using var connection = GetConnection();
        var players =
            connection.Query<DBPlayer>("SELECT * FROM Player WHERE DbGuid = @DbGuid", new { DbGuid = account.Id });
        account.Player = players.FirstOrDefault();

        if (account.Player == null)
        {
            account.Player = new DBPlayer(account.Id);
            _logger.Info($"Initialized player data for account 0x{account.Id:X}");
        }

        // Load inventory entities
        account.Avatars.AddRange(LoadEntitiesFromTable(connection, "Avatar", account.Id));
        account.TeamUps.AddRange(LoadEntitiesFromTable(connection, "TeamUp", account.Id));
        account.Items.AddRange(LoadEntitiesFromTable(connection, "Item", account.Id));

        foreach (var dbGuid in account.Avatars.Select(x => x.DbGuid))
        {
            account.Items.AddRange(LoadEntitiesFromTable(connection, "Item", dbGuid));
            account.ControlledEntities.AddRange(LoadEntitiesFromTable(connection, "ControlledEntity", dbGuid));
        }

        foreach (var teamUp in account.TeamUps)
        {
            account.Items.AddRange(LoadEntitiesFromTable(connection, "Item", teamUp.DbGuid));
        }

        return true;
    }

    public bool SavePlayerData(DBAccount account)
    {
        for (int i = 0; i < NumPlayerDataWriteAttempts; i++)
        {
            if (DoSavePlayerData(account))
                return _logger.InfoReturn(true, $"Successfully written player data for account [{account}]");

            // Maybe we should add a delay here
        }

        return _logger.WarnReturn(false, $"SavePlayerData(): Failed to write player data for account [{account}]");
    }
    
    private NpgsqlConnection GetConnection()
    {
        NpgsqlConnection connection = new(_connectionString);
        connection.Open();
        return connection;
    }
    
    private void InitializeDatabase()
    {
        var serviceProvider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .ConfigureGlobalProcessorOptions(options =>
                {
                    options.ProviderSwitches = "Force Quote=false";
                })
                .WithGlobalConnectionString(_connectionString)
                .ScanIn(typeof(IMigrationsMarker).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddFluentMigratorConsole())
            .BuildServiceProvider(false);

        // Run the migrations
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        
        var isFirstRun = !runner.HasMigrationsApplied();
        
        runner.MigrateUp();

        if (isFirstRun)
        {
            CreateTestAccounts(NumTestAccounts);
        }
    }
    
    private void CreateTestAccounts(int numAccounts)
    {
        for (int i = 0; i < numAccounts; i++)
        {
            string email = $"test{i + 1}@test.com";
            string playerName = $"Player{i + 1}";
            string password = "123";

            DBAccount account = new(email, playerName, password);
            InsertAccount(account);
            _logger.Info($"Created test account {account}");
        }
    }

    private bool DoSavePlayerData(DBAccount account)
    {
        using var connection = GetConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            // Update player entity
            if (account.Player != null)
            {
                connection.Execute("INSERT INTO Player (DbGuid) VALUES (@DbGuid) ON CONFLICT DO NOTHING",
                    account.Player,
                    transaction);
                connection.Execute(@"UPDATE Player
                                           SET ArchiveData = @ArchiveData,
                                               StartTarget = @StartTarget,
                                               StartTargetRegionOverride = @StartTargetRegionOverride,
                                               AOIVolume = @AOIVolume
                                           WHERE DbGuid = @DbGuid",
                    account.Player, transaction);
            }
            else
            {
                _logger.Warn($"DoSavePlayerData(): Attempted to save null player entity data for account {account}");
            }

            // Update inventory entities
            UpdateEntityTable(connection, transaction, "Avatar", account.Id, account.Avatars);
            UpdateEntityTable(connection, transaction, "TeamUp", account.Id, account.TeamUps);
            UpdateEntityTable(connection, transaction, "Item", account.Id, account.Items);

            foreach (var dbGuid in account.Avatars.Select(x => x.DbGuid))
            {
                UpdateEntityTable(connection, transaction, "Item", dbGuid, account.Items);
                UpdateEntityTable(connection, transaction, "ControlledEntity", dbGuid,
                    account.ControlledEntities);
            }

            foreach (var teamUp in account.TeamUps)
            {
                UpdateEntityTable(connection, transaction, "Item", teamUp.DbGuid, account.Items);
            }

            transaction.Commit();

            return true;
        }
        catch (Exception e)
        {
            _logger.Warn($"DoSavePlayerData(): PostgreSQL error for account [{account}]: {e.Message}");
            transaction.Rollback();
            return false;
        }
    }

    private IEnumerable<DBEntity> LoadEntitiesFromTable(NpgsqlConnection connection, string tableName, long containerDbGuid)
    {
        return connection.Query<DBEntity>($"SELECT * FROM {tableName} WHERE ContainerDbGuid = @ContainerDbGuid",
            new { ContainerDbGuid = containerDbGuid });
    }

    private void UpdateEntityTable(NpgsqlConnection connection, IDbTransaction transaction, string tableName,
        long containerDbGuid, DBEntityCollection dbEntityCollection)
    {
        var @params = new { ContainerDbGuid = containerDbGuid };

        // Delete items that no longer belong to this account
        var storedEntities = connection.Query<long>(
            $"SELECT DbGuid FROM {tableName} WHERE ContainerDbGuid = @ContainerDbGuid", @params, transaction);
        var entitiesToDelete = storedEntities.Except(dbEntityCollection.Guids);

        var toDelete = entitiesToDelete as long[] ?? entitiesToDelete.ToArray();
        if (toDelete.Length != 0)
        {
            connection.Execute($"DELETE FROM {tableName} WHERE DbGuid IN @DbGuids", new { DbGuids = toDelete },
                transaction);
        }

        // Insert and update
        var entries = dbEntityCollection.GetEntriesForContainer(containerDbGuid);

        if (!entries.Any())
            return;
        
        connection.Execute($"INSERT INTO {tableName} (DbGuid) VALUES (@DbGuid) ON CONFLICT DO NOTHING",
            entries, transaction);
        connection.Execute(@$"UPDATE {tableName}
                                     SET ContainerDbGuid = @ContainerDbGuid,
                                         InventoryProtoGuid = @InventoryProtoGuid,
                                         Slot = @Slot,
                                         EntityProtoGuid = @EntityProtoGuid,
                                         ArchiveData = @ArchiveData
                                     WHERE DbGuid = @DbGuid",
            entries, transaction);
    }
}