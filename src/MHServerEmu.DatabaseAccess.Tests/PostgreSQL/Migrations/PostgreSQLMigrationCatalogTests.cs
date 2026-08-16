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
        [InlineData("start /* preserve separation */ transaction;")]
        [InlineData("CoMmIt;")]
        [InlineData("END;")]
        [InlineData("ROLLBACK TO SAVEPOINT migration;")]
        [InlineData("abort;")]
        [InlineData("SAVEPOINT migration;")]
        [InlineData("RELEASE SAVEPOINT migration;")]
        [InlineData("PREPARE TRANSACTION 'migration';")]
        [InlineData("COMMIT PREPARED 'migration';")]
        [InlineData("ROLLBACK PREPARED 'migration';")]
        [InlineData("-- leading comment\nBEGIN;")]
        [InlineData("CREATE TABLE mhserveremu.safe_before (id integer); /* comment */ ROLLBACK;")]
        [InlineData("/* outer /* nested */ */ END;")]
        [InlineData("/* outer /* nested */ */ PREPARE TRANSACTION 'migration';")]
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
        [InlineData("CREATE TABLE mhserveremu.safe_ddl (id integer NOT NULL);")]
        [InlineData("ALTER TABLE mhserveremu.safe_ddl ADD COLUMN name text;")]
        [InlineData("CREATE INDEX safe_ddl_name_idx ON mhserveremu.safe_ddl (name);")]
        [InlineData("CREATE TABLE mhserveremu.first_safe_ddl (id integer);\nCREATE TABLE mhserveremu.second_safe_ddl (id integer);")]
        [InlineData("/* outer /* nested */ END; */ CREATE TABLE mhserveremu.nested_comment_safe_ddl (id integer);")]
        public void Create_NonTransactionControlSql_IsAllowed(string sql)
        {
            PostgreSQLMigrationCatalog catalog = PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource("Migrations.0001_Initialize.sql", Encoding.UTF8.GetBytes(sql)),
            });

            Assert.Single(catalog.Migrations);
        }

        [Theory]
        [InlineData("CREATE FUNCTION f() RETURNS void AS $$ BEGIN PERFORM 1; END; $$ LANGUAGE plpgsql;")]
        [InlineData("CREATE PROCEDURE p() LANGUAGE plpgsql AS $$ BEGIN PERFORM 1; END; $$;")]
        public void Create_DollarQuotedFunctionOrProcedureBody_IsAllowed(string sql)
        {
            PostgreSQLMigrationCatalog catalog = PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource("Migrations.0001_Initialize.sql", Encoding.UTF8.GetBytes(sql)),
            });

            Assert.Single(catalog.Migrations);
        }

        [Fact]
        public void Create_NamedDollarQuotedBody_IsAllowed()
        {
            PostgreSQLMigrationCatalog catalog = PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource("Migrations.0001_Initialize.sql", "CREATE FUNCTION f() RETURNS void AS $body$ BEGIN PERFORM 1; END; $body$ LANGUAGE plpgsql;"u8.ToArray()),
            });

            Assert.Single(catalog.Migrations);
        }

        [Fact]
        public void Create_EndOutsideDollarQuotedBody_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource("Migrations.0001_Initialize.sql", "CREATE FUNCTION f() RETURNS void AS $$ BEGIN PERFORM 1; END; $$ LANGUAGE plpgsql; END;"u8.ToArray()),
            }));
        }

        [Fact]
        public void Create_UnterminatedDollarQuotedBody_Throws()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource("Migrations.0001_Initialize.sql", "CREATE FUNCTION f() RETURNS void AS $body$ BEGIN PERFORM 1;"u8.ToArray()),
            }));

            Assert.Contains("unterminated dollar quote", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Create_UnterminatedNestedBlockComment_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PostgreSQLMigrationCatalog.Create(new[]
            {
                new PostgreSQLMigrationResource("Migrations.0001_Initialize.sql", "/* outer /* nested */ CREATE TABLE mhserveremu.unfinished_comment (id integer);"u8.ToArray()),
            }));
        }
    }
}
