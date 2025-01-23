using System.Data;
using FluentMigrator;

namespace MHServerEmu.DatabaseAccess.Migrations;

[Migration(1724920380)]
public class AddEntityTables : Migration {
    public override void Up()
    {
        Delete.Table("Avatar");
        Delete.Table("Player");
        
        Create.Table("Player")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
                .ForeignKey("Account", "Id").OnDelete(Rule.Cascade)
            .WithColumn("ArchiveData").AsBinary().Nullable()
            .WithColumn("StartTarget").AsInt64().Nullable()
            .WithColumn("StartTargetRegionOverride").AsInt64().Nullable()
            .WithColumn("AOIVolume").AsInt32().Nullable();

        Create.Table("Avatar")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64().Nullable()
                .ForeignKey("Player", "DbGuid").OnDelete(Rule.Cascade)
            .WithColumn("InventoryProtoGuid").AsInt64().Nullable()
            .WithColumn("Slot").AsInt32().Nullable()
            .WithColumn("EntityProtoGuid").AsInt64().Nullable()
            .WithColumn("ArchiveData").AsBinary().Nullable();
        
        Create.Table("TeamUp")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64().Nullable()
                .ForeignKey("Player", "DbGuid").OnDelete(Rule.Cascade)
            .WithColumn("InventoryProtoGuid").AsInt64().Nullable()
            .WithColumn("Slot").AsInt32().Nullable()
            .WithColumn("EntityProtoGuid").AsInt64().Nullable()
            .WithColumn("ArchiveData").AsBinary().Nullable();
        
        Create.Table("Item")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64().Nullable()
                .ForeignKey("Player", "DbGuid").OnDelete(Rule.Cascade)
                .ForeignKey("Avatar", "DbGuid").OnDelete(Rule.Cascade)
                .ForeignKey("TeamUp", "DbGuid").OnDelete(Rule.Cascade)
            .WithColumn("InventoryProtoGuid").AsInt64().Nullable()
            .WithColumn("Slot").AsInt32().Nullable()
            .WithColumn("EntityProtoGuid").AsInt64().Nullable()
            .WithColumn("ArchiveData").AsBinary().Nullable();
        
        Create.Table("ControlledEntity")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64().Nullable()
                .ForeignKey("Avatar", "DbGuid").OnDelete(Rule.Cascade)
            .WithColumn("InventoryProtoGuid").AsInt64().Nullable()
            .WithColumn("Slot").AsInt32().Nullable()
            .WithColumn("EntityProtoGuid").AsInt64().Nullable()
            .WithColumn("ArchiveData").AsBinary().Nullable();
        
        Create.Index().OnTable("Avatar").OnColumn("ContainerDbGuid");
        Create.Index().OnTable("TeamUp").OnColumn("ContainerDbGuid");
        Create.Index().OnTable("Item").OnColumn("ContainerDbGuid");
        Create.Index().OnTable("ControlledEntity").OnColumn("ContainerDbGuid");
    }

    public override void Down()
    {
        Delete.Index().OnTable("ControlledEntity").OnColumn("ContainerDbGuid");
        Delete.Index().OnTable("Item").OnColumn("ContainerDbGuid");
        Delete.Index().OnTable("TeamUp").OnColumn("ContainerDbGuid");
        Delete.Index().OnTable("Avatar").OnColumn("ContainerDbGuid");
        
        Delete.Table("ControlledEntity");
        Delete.Table("Item");
        Delete.Table("TeamUp");
        Delete.Table("Avatar");
        Delete.Table("Player");
        
        Create.Table("Player")
            .WithColumn("AccountId").AsInt64().Unique().NotNullable().PrimaryKey()
            .ForeignKey("Account", "Id").OnDelete(Rule.Cascade)
            .WithColumn("RawRegion").AsInt64().Nullable()
            .WithColumn("RawAvatar").AsInt64().Nullable()
            .WithColumn("RawWaypoint").AsInt64().Nullable()
            .WithColumn("AOIVolume").AsInt32().Nullable();
        
        Create.Table("Avatar")
            .WithColumn("AccountId").AsInt64().NotNullable().PrimaryKey()
            .ForeignKey("Account", "Id").OnDelete(Rule.Cascade)
            .WithColumn("RawPrototype").AsInt64().NotNullable().PrimaryKey()
            .WithColumn("RawCostume").AsInt64().Nullable()
            .WithColumn("RawAbilityKeyMapping").AsBinary().Nullable();
    }
}