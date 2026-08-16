using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess.Validation
{
    public static class PlayerAggregateValidator
    {
        public static bool TryValidate(DBAccount account, out string error)
        {
            if (account == null)
                return Fail("Account cannot be null.", out error);

            List<(DBEntity Entity, DBEntityCategory Category)> entities = new();
            if (AddEntities(account.Avatars, DBEntityCategory.Avatar, entities, out error) == false
                || AddEntities(account.TeamUps, DBEntityCategory.TeamUp, entities, out error) == false
                || AddEntities(account.Items, DBEntityCategory.Item, entities, out error) == false
                || AddEntities(account.ControlledEntities, DBEntityCategory.ControlledEntity, entities, out error) == false)
            {
                return false;
            }

            Dictionary<long, DBEntityCategory> categories = new();
            Dictionary<long, DBEntity> entitiesById = new();
            HashSet<(long ContainerDbGuid, long InventoryProtoGuid, uint Slot)> inventoryLocations = new();

            foreach ((DBEntity entity, DBEntityCategory category) in entities)
            {
                if (entity == null)
                    return Fail("Entity cannot be null.", out error);
                if (entity.ArchiveData == null)
                    return Fail($"Entity 0x{entity.DbGuid:X} archive data cannot be null.", out error);
                if (entitiesById.TryAdd(entity.DbGuid, entity) == false)
                    return Fail($"Duplicate entity DB ID 0x{entity.DbGuid:X}.", out error);

                categories.Add(entity.DbGuid, category);

                if (entity.InventoryProtoGuid != 0
                    && inventoryLocations.Add((entity.ContainerDbGuid, entity.InventoryProtoGuid, entity.Slot)) == false)
                {
                    return Fail($"Duplicate inventory location for entity 0x{entity.DbGuid:X}.", out error);
                }
            }

            foreach ((DBEntity entity, DBEntityCategory _) in entities)
            {
                if (TryValidateGraph(entity, account.Id, entitiesById, out error) == false)
                    return false;
            }

            foreach ((DBEntity entity, DBEntityCategory category) in entities)
            {
                if (entity.ContainerDbGuid == account.Id)
                {
                    if (category == DBEntityCategory.ControlledEntity)
                        return Fail("Controlled entities cannot be aggregate roots.", out error);

                    continue;
                }

                DBEntityCategory parentCategory = categories[entity.ContainerDbGuid];
                if (IsValidParent(category, parentCategory) == false)
                    return Fail($"Invalid parent category for entity 0x{entity.DbGuid:X}.", out error);
            }

            error = null;
            return true;
        }

        private static bool AddEntities(DBEntityCollection collection, DBEntityCategory category, List<(DBEntity Entity, DBEntityCategory Category)> entities, out string error)
        {
            if (collection == null)
                return Fail($"{category} collection cannot be null.", out error);

            foreach (DBEntity entity in collection.Entries)
                entities.Add((entity, category));

            error = null;
            return true;
        }

        private static bool TryValidateGraph(DBEntity entity, long accountId, Dictionary<long, DBEntity> entitiesById, out string error)
        {
            HashSet<long> visited = new() { entity.DbGuid };
            DBEntity current = entity;
            int depth = 0;

            while (current.ContainerDbGuid != accountId)
            {
                if (entitiesById.TryGetValue(current.ContainerDbGuid, out DBEntity parent) == false)
                    return Fail($"Entity 0x{entity.DbGuid:X} has a missing parent.", out error);
                if (visited.Add(parent.DbGuid) == false)
                    return Fail($"Entity 0x{entity.DbGuid:X} is part of a parent cycle.", out error);

                depth++;
                if (depth > 1)
                    return Fail($"Entity 0x{entity.DbGuid:X} exceeds the maximum parent depth.", out error);

                current = parent;
            }

            error = null;
            return true;
        }

        private static bool IsValidParent(DBEntityCategory category, DBEntityCategory parentCategory)
        {
            return category switch
            {
                DBEntityCategory.Item => parentCategory is DBEntityCategory.Avatar or DBEntityCategory.TeamUp,
                DBEntityCategory.ControlledEntity => parentCategory == DBEntityCategory.Avatar,
                _ => false,
            };
        }

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
