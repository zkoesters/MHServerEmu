using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MHServerEmu.DatabaseAccess.PostgreSQL.Migrations
{
    internal sealed class PostgreSQLMigrationCatalog
    {
        private static readonly Regex ResourceNamePattern = new("^Migrations\\.(?<version>[0-9]{4})_(?<name>[A-Z][A-Za-z0-9]*)\\.sql$", RegexOptions.CultureInvariant);
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private PostgreSQLMigrationCatalog(IReadOnlyList<PostgreSQLMigration> migrations)
        {
            Migrations = migrations;
        }

        internal IReadOnlyList<PostgreSQLMigration> Migrations { get; }

        internal static PostgreSQLMigrationCatalog Create(IEnumerable<PostgreSQLMigrationResource> resources)
        {
            ArgumentNullException.ThrowIfNull(resources);
            List<PostgreSQLMigration> migrations = new();
            HashSet<int> versions = new();

            foreach (PostgreSQLMigrationResource resource in resources)
            {
                if (resource == null)
                    throw new InvalidOperationException("Migration resources cannot contain null values.");

                Match match = ResourceNamePattern.Match(resource.Name);
                if (match.Success == false || int.TryParse(match.Groups["version"].Value, out int version) == false || version == 0)
                    throw new InvalidOperationException("Migration resource name is invalid.");

                if (versions.Add(version) == false)
                    throw new InvalidOperationException("Migration versions must be unique.");

                string sql;
                try
                {
                    sql = StrictUtf8.GetString(resource.Bytes);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidOperationException("Migration SQL must be valid UTF-8.", exception);
                }

                if (ContainsTransactionControl(sql))
                    throw new InvalidOperationException("Migration SQL cannot control transactions.");

                migrations.Add(new PostgreSQLMigration(version, match.Groups["name"].Value, Convert.ToHexString(SHA256.HashData(resource.Bytes)), sql));
            }

            migrations.Sort((left, right) => left.Version.CompareTo(right.Version));
            return new PostgreSQLMigrationCatalog(new ReadOnlyCollection<PostgreSQLMigration>(migrations));
        }

        internal static PostgreSQLMigrationCatalog LoadEmbedded()
        {
            Assembly assembly = typeof(PostgreSQLMigrationCatalog).Assembly;
            List<PostgreSQLMigrationResource> resources = new();
            foreach (string resourceName in assembly.GetManifestResourceNames().Where(name => name.StartsWith("Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal)))
            {
                using Stream stream = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Embedded migration resource is unavailable.");
                using MemoryStream bytes = new();
                stream.CopyTo(bytes);
                resources.Add(new PostgreSQLMigrationResource(resourceName, bytes.ToArray()));
            }

            return Create(resources);
        }

        private static bool ContainsTransactionControl(string sql)
        {
            StringBuilder statement = new();
            bool inLineComment = false;
            bool inBlockComment = false;
            bool inSingleQuote = false;
            bool inDoubleQuote = false;

            for (int index = 0; index < sql.Length; index++)
            {
                char current = sql[index];
                char next = index + 1 < sql.Length ? sql[index + 1] : '\0';

                if (inLineComment)
                {
                    if (current == '\n')
                        inLineComment = false;
                    continue;
                }

                if (inBlockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        inBlockComment = false;
                        index++;
                    }
                    continue;
                }

                if (inSingleQuote)
                {
                    if (current == '\'' && next == '\'')
                        index++;
                    else if (current == '\'')
                        inSingleQuote = false;
                    continue;
                }

                if (inDoubleQuote)
                {
                    if (current == '"')
                        inDoubleQuote = false;
                    continue;
                }

                if (current == '-' && next == '-')
                {
                    inLineComment = true;
                    index++;
                    continue;
                }

                if (current == '/' && next == '*')
                {
                    inBlockComment = true;
                    index++;
                    continue;
                }

                if (current == '\'')
                {
                    inSingleQuote = true;
                    continue;
                }

                if (current == '"')
                {
                    inDoubleQuote = true;
                    continue;
                }

                if (current == ';')
                {
                    if (IsTransactionControl(statement.ToString()))
                        return true;
                    statement.Clear();
                    continue;
                }

                statement.Append(current);
            }

            return IsTransactionControl(statement.ToString());
        }

        private static bool IsTransactionControl(string statement)
        {
            string command = statement.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.Equals(command, "BEGIN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "COMMIT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "ROLLBACK", StringComparison.OrdinalIgnoreCase);
        }
    }
}
