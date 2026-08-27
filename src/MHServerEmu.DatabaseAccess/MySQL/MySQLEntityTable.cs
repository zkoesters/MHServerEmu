using Dapper;
using MHServerEmu.DatabaseAccess.Models;
using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.MySQL
{
    internal sealed class MySQLEntityTable
    {
        private readonly string _selectAllQuery;
        private readonly string _selectIdsQuery;
        private readonly string _deleteQuery;
        private readonly string _deleteForContainersQuery;
        private readonly string _upsertQuery;

        private MySQLEntityTable(DBEntityCategory category)
        {
            string tableName = category switch
            {
                DBEntityCategory.Avatar => "avatar",
                DBEntityCategory.TeamUp => "team_up",
                DBEntityCategory.Item => "item",
                DBEntityCategory.ControlledEntity => "controlled_entity",
                _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
            };

            _selectAllQuery = $@"SELECT db_guid AS DbGuid, container_db_guid AS ContainerDbGuid,
                inventory_proto_guid AS InventoryProtoGuid, slot AS Slot, entity_proto_guid AS EntityProtoGuid,
                archive_data AS ArchiveData FROM {tableName} WHERE container_db_guid = @ContainerDbGuid";
            _selectIdsQuery = $"SELECT db_guid FROM {tableName} WHERE container_db_guid = @ContainerDbGuid";
            _deleteQuery = $"DELETE FROM {tableName} WHERE db_guid IN @Ids";
            _deleteForContainersQuery = $"DELETE FROM {tableName} WHERE container_db_guid IN @ContainerIds";
            _upsertQuery = $@"INSERT INTO {tableName} (db_guid, container_db_guid, inventory_proto_guid, slot, entity_proto_guid, archive_data)
                VALUES (@DbGuid, @ContainerDbGuid, @InventoryProtoGuid, @Slot, @EntityProtoGuid, @ArchiveData)
                ON DUPLICATE KEY UPDATE container_db_guid = VALUES(container_db_guid),
                inventory_proto_guid = VALUES(inventory_proto_guid), slot = VALUES(slot),
                entity_proto_guid = VALUES(entity_proto_guid), archive_data = VALUES(archive_data)";
        }

        public static MySQLEntityTable GetTable(DBEntityCategory category) => new(category);

        public void LoadEntities(MySqlConnection connection, long containerDbGuid, DBEntityCollection entities, MySqlTransaction transaction)
        {
            IEnumerable<MySQLEntityRow> rows = connection.Query<MySQLEntityRow>(_selectAllQuery, new { ContainerDbGuid = containerDbGuid }, transaction);
            entities.AddRange(rows.Select(row => row.ToDBEntity()));
        }

        public void UpdateEntities(MySqlConnection connection, MySqlTransaction transaction, long containerDbGuid, DBEntityCollection entities)
        {
            DeleteEntities(connection, transaction, GetEntitiesToDelete(connection, transaction, containerDbGuid, entities));

            IReadOnlyList<DBEntity> entries = entities.GetEntriesForContainer(containerDbGuid);
            if (entries.Count > 0)
                connection.Execute(_upsertQuery, entries.Select(MySQLEntityRow.FromDBEntity), transaction);
        }

        public long[] GetEntitiesToDelete(MySqlConnection connection, MySqlTransaction transaction, long containerDbGuid, DBEntityCollection entities)
        {
            IEnumerable<long> storedIds = connection.Query<long>(_selectIdsQuery, new { ContainerDbGuid = containerDbGuid }, transaction);
            return storedIds.Where(id => entities.Contains(id) == false).ToArray();
        }

        public void DeleteEntities(MySqlConnection connection, MySqlTransaction transaction, long[] ids)
        {
            if (ids.Length > 0)
                connection.Execute(_deleteQuery, new { Ids = ids }, transaction);
        }

        public void DeleteEntitiesForContainers(MySqlConnection connection, MySqlTransaction transaction, long[] containerIds)
        {
            if (containerIds.Length > 0)
                connection.Execute(_deleteForContainersQuery, new { ContainerIds = containerIds }, transaction);
        }

        private sealed class MySQLEntityRow
        {
            public long DbGuid { get; set; }
            public long ContainerDbGuid { get; set; }
            public long InventoryProtoGuid { get; set; }
            public long Slot { get; set; }
            public long EntityProtoGuid { get; set; }
            public byte[] ArchiveData { get; set; }

            public DBEntity ToDBEntity() => new()
            {
                DbGuid = DbGuid,
                ContainerDbGuid = ContainerDbGuid,
                InventoryProtoGuid = InventoryProtoGuid,
                Slot = checked((uint)Slot),
                EntityProtoGuid = EntityProtoGuid,
                ArchiveData = ArchiveData
            };

            public static MySQLEntityRow FromDBEntity(DBEntity entity) => new()
            {
                DbGuid = entity.DbGuid,
                ContainerDbGuid = entity.ContainerDbGuid,
                InventoryProtoGuid = entity.InventoryProtoGuid,
                Slot = checked((long)entity.Slot),
                EntityProtoGuid = entity.EntityProtoGuid,
                ArchiveData = entity.ArchiveData
            };
        }
    }
}
