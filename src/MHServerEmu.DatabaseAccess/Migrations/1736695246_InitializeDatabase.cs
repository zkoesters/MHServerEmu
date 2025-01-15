using System.Data;
using FluentMigrator;

namespace MHServerEmu.DatabaseAccess.Migrations;

[Migration(1736695246)]
public class InitializeDatabase : Migration {
    public override void Up()
    {
        Create.Table("Account")
            .WithColumn("Id").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("Email").AsString().Unique().NotNullable()
            .WithColumn("PlayerName").AsString().Unique().NotNullable()
            .WithColumn("PasswordHash").AsBinary().NotNullable()
            .WithColumn("Salt").AsBinary().NotNullable()
            .WithColumn("UserLevel").AsInt32().NotNullable()
            .WithColumn("Flags").AsInt32().NotNullable();

        Create.Table("Player")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
                .ForeignKey("Account", "Id").OnDelete(Rule.Cascade)
            .WithColumn("ArchiveData").AsBinary()
            .WithColumn("StartTarget").AsInt64()
            .WithColumn("StartTargetRegionOverride").AsInt64()
            .WithColumn("AOIVolume").AsInt32();

        Create.Table("Avatar")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64().Indexed()
                .ForeignKey("Player", "DbGuid").OnDelete(Rule.Cascade)
            .WithColumn("InventoryProtoGuid").AsInt64()
            .WithColumn("Slot").AsInt32()
            .WithColumn("EntityProtoGuid").AsInt64()
            .WithColumn("ArchiveData").AsBinary();
        
        Create.Table("TeamUp")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64().Indexed()
                .ForeignKey("Player", "DbGuid").OnDelete(Rule.Cascade)
            .WithColumn("InventoryProtoGuid").AsInt64()
            .WithColumn("Slot").AsInt32()
            .WithColumn("EntityProtoGuid").AsInt64()
            .WithColumn("ArchiveData").AsBinary();
        
        Create.Table("Item")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64().Indexed()
                .ForeignKey("Player", "DbGuid").OnDelete(Rule.Cascade)
                .ForeignKey("Avatar", "DbGuid").OnDelete(Rule.Cascade)
                .ForeignKey("TeamUp", "DbGuid").OnDelete(Rule.Cascade)
            .WithColumn("InventoryProtoGuid").AsInt64()
            .WithColumn("Slot").AsInt32()
            .WithColumn("EntityProtoGuid").AsInt64()
            .WithColumn("ArchiveData").AsBinary();
        
        Create.Table("ControlledEntity")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64().Indexed()
                .ForeignKey("Avatar", "DbGuid").OnDelete(Rule.Cascade)
            .WithColumn("InventoryProtoGuid").AsInt64()
            .WithColumn("Slot").AsInt32()
            .WithColumn("EntityProtoGuid").AsInt64()
            .WithColumn("ArchiveData").AsBinary();
    }

    public override void Down()
    {
        Delete.Table("ControlledEntity");
        Delete.Table("Item");
        Delete.Table("TeamUp");
        Delete.Table("Avatar");
        Delete.Table("Player");
        Delete.Table("Account");
    }
}