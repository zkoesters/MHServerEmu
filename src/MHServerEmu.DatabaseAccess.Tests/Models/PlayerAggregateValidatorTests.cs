using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Validation;

namespace MHServerEmu.DatabaseAccess.Tests.Models
{
    public class PlayerAggregateValidatorTests
    {
        [Fact]
        public void TryValidate_AcceptsRootAndNestedEntities()
        {
            DBAccount account = CreateAccount();
            account.Avatars.Add(Entity(10, account.Id));
            account.TeamUps.Add(Entity(20, account.Id));
            account.Items.Add(Entity(30, account.Id, inventoryProtoGuid: 100, slot: 1));
            account.Items.Add(Entity(31, 10, inventoryProtoGuid: 100, slot: 2));
            account.Items.Add(Entity(32, 20, inventoryProtoGuid: 100, slot: 3));
            account.ControlledEntities.Add(Entity(40, 10));

            bool valid = PlayerAggregateValidator.TryValidate(account, out string error);

            Assert.True(valid);
            Assert.Null(error);
        }

        [Fact]
        public void TryValidate_RejectsNullAccount()
        {
            Assert.False(PlayerAggregateValidator.TryValidate(null, out string error));

            Assert.NotEmpty(error);
        }

        [Fact]
        public void TryValidate_RejectsNullEntityCollection()
        {
            DBAccount account = new() { Id = 1, Avatars = null };

            Assert.False(PlayerAggregateValidator.TryValidate(account, out string error));

            Assert.Contains("collection", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryValidate_RejectsNullEntity()
        {
            DBAccount account = CreateAccount();
            var entities = (Dictionary<long, DBEntity>)typeof(DBEntityCollection)
                .GetField("_allEntities", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(account.Avatars);
            entities.Add(10, null);

            Assert.False(PlayerAggregateValidator.TryValidate(account, out string error));

            Assert.Contains("entity", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryValidate_RejectsNullArchive()
        {
            DBAccount account = CreateAccount();
            DBEntity avatar = Entity(10, account.Id);
            avatar.ArchiveData = null;
            account.Avatars.Add(avatar);

            Assert.False(PlayerAggregateValidator.TryValidate(account, out string error));

            Assert.Contains("archive", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryValidate_RejectsDuplicateEntityIdAcrossCategories()
        {
            DBAccount account = CreateAccount();
            account.Avatars.Add(Entity(10, account.Id));
            account.Items.Add(Entity(10, account.Id));

            Assert.False(PlayerAggregateValidator.TryValidate(account, out string error));

            Assert.Contains("duplicate", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryValidate_RejectsMissingParent()
        {
            DBAccount account = CreateAccount();
            account.Items.Add(Entity(10, 999));

            Assert.False(PlayerAggregateValidator.TryValidate(account, out string error));

            Assert.Contains("parent", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryValidate_RejectsCycle()
        {
            DBAccount account = CreateAccount();
            account.Items.Add(Entity(10, 11));
            account.Items.Add(Entity(11, 10));

            Assert.False(PlayerAggregateValidator.TryValidate(account, out string error));

            Assert.Contains("cycle", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryValidate_RejectsDepthGreaterThanOne()
        {
            DBAccount account = CreateAccount();
            account.Avatars.Add(Entity(10, account.Id));
            account.Items.Add(Entity(11, 10));
            account.Items.Add(Entity(12, 11));

            Assert.False(PlayerAggregateValidator.TryValidate(account, out string error));

            Assert.Contains("depth", error, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(DBEntityCategory.Avatar, DBEntityCategory.Item)]
        [InlineData(DBEntityCategory.TeamUp, DBEntityCategory.Item)]
        [InlineData(DBEntityCategory.Item, DBEntityCategory.Item)]
        [InlineData(DBEntityCategory.Item, DBEntityCategory.ControlledEntity)]
        [InlineData(DBEntityCategory.ControlledEntity, DBEntityCategory.TeamUp)]
        [InlineData(DBEntityCategory.ControlledEntity, DBEntityCategory.Item)]
        [InlineData(DBEntityCategory.ControlledEntity, DBEntityCategory.ControlledEntity)]
        public void TryValidate_RejectsInvalidParentCategory(DBEntityCategory childCategory, DBEntityCategory parentCategory)
        {
            DBAccount account = CreateAccount();
            Add(account, parentCategory, Entity(10, account.Id));
            Add(account, childCategory, Entity(11, 10));

            Assert.False(PlayerAggregateValidator.TryValidate(account, out string error));
        }

        [Theory]
        [InlineData(DBEntityCategory.ControlledEntity)]
        public void TryValidate_RejectsInvalidRootCategory(DBEntityCategory category)
        {
            DBAccount account = CreateAccount();
            Add(account, category, Entity(10, account.Id));

            Assert.False(PlayerAggregateValidator.TryValidate(account, out string error));

            Assert.Contains("root", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryValidate_RejectsDuplicateNonzeroInventoryLocations()
        {
            DBAccount account = CreateAccount();
            account.Items.Add(Entity(10, account.Id, inventoryProtoGuid: 100, slot: 1));
            account.Items.Add(Entity(11, account.Id, inventoryProtoGuid: 100, slot: 1));

            Assert.False(PlayerAggregateValidator.TryValidate(account, out string error));

            Assert.Contains("inventory", error, StringComparison.OrdinalIgnoreCase);
        }

        private static DBAccount CreateAccount()
        {
            return new DBAccount { Id = 1 };
        }

        private static DBEntity Entity(long dbGuid, long containerDbGuid, long inventoryProtoGuid = 0, uint slot = 0, byte[] archiveData = null)
        {
            return new DBEntity
            {
                DbGuid = dbGuid,
                ContainerDbGuid = containerDbGuid,
                InventoryProtoGuid = inventoryProtoGuid,
                Slot = slot,
                ArchiveData = archiveData ?? Array.Empty<byte>(),
            };
        }

        private static void Add(DBAccount account, DBEntityCategory category, DBEntity entity)
        {
            switch (category)
            {
                case DBEntityCategory.Avatar:
                    account.Avatars.Add(entity);
                    break;
                case DBEntityCategory.TeamUp:
                    account.TeamUps.Add(entity);
                    break;
                case DBEntityCategory.Item:
                    account.Items.Add(entity);
                    break;
                case DBEntityCategory.ControlledEntity:
                    account.ControlledEntities.Add(entity);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(category));
            }
        }
    }
}
