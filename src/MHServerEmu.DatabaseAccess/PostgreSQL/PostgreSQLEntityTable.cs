using Dapper;
using MHServerEmu.DatabaseAccess.Models;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    public class PostgreSQLEntityTable
    {
        private const string AvatarSelectQuery = @"SELECT db_guid AS DbGuid, container_db_guid AS ContainerDbGuid,
            inventory_proto_guid AS InventoryProtoGuid, slot AS Slot, entity_proto_guid AS EntityProtoGuid,
            archive_data AS ArchiveData FROM avatar WHERE container_db_guid = @ContainerDbGuid";
        private const string TeamUpSelectQuery = @"SELECT db_guid AS DbGuid, container_db_guid AS ContainerDbGuid,
            inventory_proto_guid AS InventoryProtoGuid, slot AS Slot, entity_proto_guid AS EntityProtoGuid,
            archive_data AS ArchiveData FROM team_up WHERE container_db_guid = @ContainerDbGuid";
        private const string ItemSelectQuery = @"SELECT db_guid AS DbGuid, container_db_guid AS ContainerDbGuid,
            inventory_proto_guid AS InventoryProtoGuid, slot AS Slot, entity_proto_guid AS EntityProtoGuid,
            archive_data AS ArchiveData FROM item WHERE container_db_guid = @ContainerDbGuid";
        private const string ControlledEntitySelectQuery = @"SELECT db_guid AS DbGuid, container_db_guid AS ContainerDbGuid,
            inventory_proto_guid AS InventoryProtoGuid, slot AS Slot, entity_proto_guid AS EntityProtoGuid,
            archive_data AS ArchiveData FROM controlled_entity WHERE container_db_guid = @ContainerDbGuid";

        private readonly string _selectAllQuery;
        private readonly string _selectIdsQuery;
        private readonly string _deleteQuery;
        private readonly string _deleteForContainersQuery;
        private readonly string _upsertQuery;

        public DBEntityCategory Category { get; }

        private PostgreSQLEntityTable(DBEntityCategory category)
        {
            Category = category;
            _selectAllQuery = category switch
            {
                DBEntityCategory.Avatar => AvatarSelectQuery,
                DBEntityCategory.TeamUp => TeamUpSelectQuery,
                DBEntityCategory.Item => ItemSelectQuery,
                DBEntityCategory.ControlledEntity => ControlledEntitySelectQuery,
                _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
            };

            string tableName = category switch
            {
                DBEntityCategory.Avatar => "avatar",
                DBEntityCategory.TeamUp => "team_up",
                DBEntityCategory.Item => "item",
                DBEntityCategory.ControlledEntity => "controlled_entity",
                _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
            };
            _selectIdsQuery = $"SELECT db_guid FROM {tableName} WHERE container_db_guid = @ContainerDbGuid";
            _deleteQuery = $"DELETE FROM {tableName} WHERE db_guid = ANY(@EntitiesToDelete)";
            _deleteForContainersQuery = $"DELETE FROM {tableName} WHERE container_db_guid = ANY(@ContainerDbGuids)";
            _upsertQuery = $@"INSERT INTO {tableName} (db_guid, container_db_guid, inventory_proto_guid, slot, entity_proto_guid, archive_data)
                VALUES (@DbGuid, @ContainerDbGuid, @InventoryProtoGuid, @Slot, @EntityProtoGuid, @ArchiveData)
                ON CONFLICT (db_guid) DO UPDATE SET container_db_guid = EXCLUDED.container_db_guid,
                inventory_proto_guid = EXCLUDED.inventory_proto_guid, slot = EXCLUDED.slot,
                entity_proto_guid = EXCLUDED.entity_proto_guid, archive_data = EXCLUDED.archive_data";
        }

        public static PostgreSQLEntityTable GetTable(DBEntityCategory category)
        {
            return new(category);
        }

        public void LoadEntities(NpgsqlConnection connection, long containerDbGuid, DBEntityCollection dbEntityCollection, NpgsqlTransaction transaction = null)
        {
            IEnumerable<PostgreSQLEntityRow> rows = connection.Query<PostgreSQLEntityRow>(_selectAllQuery,
                new { ContainerDbGuid = containerDbGuid }, transaction);
            dbEntityCollection.AddRange(rows.Select(row => row.ToDBEntity()));
        }

        public void UpdateEntities(NpgsqlConnection connection, NpgsqlTransaction transaction, long containerDbGuid, DBEntityCollection dbEntityCollection)
        {
            DeleteEntities(connection, transaction, GetEntitiesToDelete(connection, transaction, containerDbGuid, dbEntityCollection));

            IReadOnlyList<DBEntity> entries = dbEntityCollection.GetEntriesForContainer(containerDbGuid);
            if (entries.Count > 0)
                connection.Execute(_upsertQuery, entries.Select(PostgreSQLEntityRow.FromDBEntity), transaction);
        }

        public long[] GetEntitiesToDelete(NpgsqlConnection connection, NpgsqlTransaction transaction, long containerDbGuid, DBEntityCollection dbEntityCollection)
        {
            IEnumerable<long> storedDbGuids = connection.Query<long>(_selectIdsQuery, new { ContainerDbGuid = containerDbGuid }, transaction);
            List<long> entitiesToDelete = new();
            foreach (long storedDbGuid in storedDbGuids)
            {
                if (dbEntityCollection.Contains(storedDbGuid) == false)
                    entitiesToDelete.Add(storedDbGuid);
            }

            return entitiesToDelete.ToArray();
        }

        public void DeleteEntities(NpgsqlConnection connection, NpgsqlTransaction transaction, long[] entitiesToDelete)
        {
            if (entitiesToDelete.Length > 0)
                connection.Execute(_deleteQuery, new { EntitiesToDelete = entitiesToDelete }, transaction);
        }

        public void DeleteEntitiesForContainers(NpgsqlConnection connection, NpgsqlTransaction transaction, long[] containerDbGuids)
        {
            if (containerDbGuids.Length > 0)
                connection.Execute(_deleteForContainersQuery, new { ContainerDbGuids = containerDbGuids }, transaction);
        }

        private class PostgreSQLEntityRow
        {
            public long DbGuid { get; set; }
            public long ContainerDbGuid { get; set; }
            public long InventoryProtoGuid { get; set; }
            public long Slot { get; set; }
            public long EntityProtoGuid { get; set; }
            public byte[] ArchiveData { get; set; }

            public DBEntity ToDBEntity()
            {
                return new()
                {
                    DbGuid = DbGuid,
                    ContainerDbGuid = ContainerDbGuid,
                    InventoryProtoGuid = InventoryProtoGuid,
                    Slot = checked((uint)Slot),
                    EntityProtoGuid = EntityProtoGuid,
                    ArchiveData = ArchiveData
                };
            }

            public static PostgreSQLEntityRow FromDBEntity(DBEntity entity)
            {
                return new()
                {
                    DbGuid = entity.DbGuid,
                    ContainerDbGuid = entity.ContainerDbGuid,
                    InventoryProtoGuid = entity.InventoryProtoGuid,
                    Slot = (long)entity.Slot,
                    EntityProtoGuid = entity.EntityProtoGuid,
                    ArchiveData = entity.ArchiveData
                };
            }
        }
    }
}
