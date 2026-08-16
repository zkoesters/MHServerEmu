using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using MHServerEmu.DatabaseAccess.PostgreSQL.Migrations;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations
{
    public class PostgreSQLMigrationCatalogTests
    {
        [Fact]
        public void Create_ValidResources_OrdersMigrationsAndUsesExactBytesForChecksum()
        {
            byte[] initialize = "CREATE SCHEMA mhserveremu;\n"u8.ToArray();
            byte[] seed = "INSERT INTO mhserveremu.application_metadata VALUES (1);\n"u8.ToArray();

            PostgreSQLMigrationCatalog catalog = PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource("Migrations.0002_Seed.sql", seed),
                new PostgreSQLMigrationResource("Migrations.0001_Initialize.sql", initialize),
            });

            Assert.Collection(catalog.Migrations,
                migration =>
                {
                    Assert.Equal(1, migration.Version);
                    Assert.Equal("Initialize", migration.Name);
                    Assert.Equal(Convert.ToHexString(SHA256.HashData(initialize)), migration.Checksum);
                    Assert.Equal("CREATE SCHEMA mhserveremu;\n", migration.Sql);
                },
                migration => Assert.Equal(2, migration.Version));
        }

        [Fact]
        public void LoadEmbedded_UsesTheInitializePersistenceResourceBytes()
        {
            PostgreSQLMigrationCatalog catalog = PostgreSQLMigrationCatalog.LoadEmbedded();
            Assembly assembly = typeof(PostgreSQLMigrationCatalog).Assembly;
            using Stream stream = assembly.GetManifestResourceStream("Migrations.0001_InitializePersistence.sql");
            using MemoryStream bytes = new();
            stream.CopyTo(bytes);

            PostgreSQLMigration migration = Assert.Single(catalog.Migrations);
            Assert.Equal(1, migration.Version);
            Assert.Equal("InitializePersistence", migration.Name);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes.ToArray())), migration.Checksum);
        }

        [Theory]
        [InlineData("Migrations.1_Initialize.sql")]
        [InlineData("Migrations.0001_initialize.sql")]
        [InlineData("Migrations.0001_Initialize.SQL")]
        [InlineData("Migrations.0001_Initialize.sql.bak")]
        [InlineData("Other.0001_Initialize.sql")]
        public void Create_MalformedResourceName_Throws(string resourceName)
        {
            Assert.Throws<InvalidOperationException>(() => PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource(resourceName, "SELECT 1;"u8.ToArray()),
            }));
        }

        [Fact]
        public void Create_DuplicateVersion_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource("Migrations.0001_Initialize.sql", "SELECT 1;"u8.ToArray()),
                new PostgreSQLMigrationResource("Migrations.0001_Reinitialize.sql", "SELECT 2;"u8.ToArray()),
            }));
        }

        [Fact]
        public void Create_InvalidUtf8_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource("Migrations.0001_Initialize.sql", new byte[] { 0xc3, 0x28 }),
            }));
        }

        [Theory]
        [InlineData("BEGIN;")]
        [InlineData("COMMIT;")]
        [InlineData("ROLLBACK;")]
        public void Create_TransactionControl_Throws(string sql)
        {
            Assert.Throws<InvalidOperationException>(() => PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource("Migrations.0001_Initialize.sql", Encoding.UTF8.GetBytes(sql)),
            }));
        }

        [Theory]
        [InlineData("-- BEGIN\nSELECT 1;")]
        [InlineData("SELECT 'COMMIT';")]
        public void Create_TransactionControlTextOutsideAStatement_IsAllowed(string sql)
        {
            PostgreSQLMigrationCatalog catalog = PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource("Migrations.0001_Initialize.sql", Encoding.UTF8.GetBytes(sql)),
            });

            Assert.Single(catalog.Migrations);
        }
    }
}
