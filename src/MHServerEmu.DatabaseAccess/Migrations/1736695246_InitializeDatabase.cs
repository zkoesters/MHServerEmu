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
            .WithColumn("ArchiveData").AsBinary()
            .WithColumn("StartTarget").AsInt64()
            .WithColumn("StartTargetRegionOverride").AsInt64()
            .WithColumn("AOIVolume").AsInt32();
        
        Create.ForeignKey()
            .FromTable("Player").ForeignColumn("DbGuid")
            .ToTable("Account").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Create.Table("Avatar")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64()
            .WithColumn("InventoryProtoGuid").AsInt64()
            .WithColumn("Slot").AsInt32()
            .WithColumn("EntityProtoGuid").AsInt64()
            .WithColumn("ArchiveData").AsBinary();
        
        Create.ForeignKey()
            .FromTable("Avatar").ForeignColumn("ContainerDbGuid")
            .ToTable("Player").PrimaryColumn("DbGuid")
            .OnDelete(Rule.Cascade);
        
        Create.Table("TeamUp")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64()
            .WithColumn("InventoryProtoGuid").AsInt64()
            .WithColumn("Slot").AsInt32()
            .WithColumn("EntityProtoGuid").AsInt64()
            .WithColumn("ArchiveData").AsBinary();
        
        Create.ForeignKey()
            .FromTable("TeamUp").ForeignColumn("ContainerDbGuid")
            .ToTable("Player").PrimaryColumn("DbGuid")
            .OnDelete(Rule.Cascade);
        
        Create.Table("Item")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64()
            .WithColumn("InventoryProtoGuid").AsInt64()
            .WithColumn("Slot").AsInt32()
            .WithColumn("EntityProtoGuid").AsInt64()
            .WithColumn("ArchiveData").AsBinary();
        
        Create.ForeignKey()
            .FromTable("Item").ForeignColumn("ContainerDbGuid")
            .ToTable("Player").PrimaryColumn("DbGuid")
            .OnDelete(Rule.Cascade);
        
        Create.ForeignKey()
            .FromTable("Item").ForeignColumn("ContainerDbGuid")
            .ToTable("Avatar").PrimaryColumn("DbGuid")
            .OnDelete(Rule.Cascade);
        
        Create.ForeignKey()
            .FromTable("Item").ForeignColumn("ContainerDbGuid")
            .ToTable("TeamUp").PrimaryColumn("DbGuid")
            .OnDelete(Rule.Cascade);
        
        Create.Table("ControlledEntity")
            .WithColumn("DbGuid").AsInt64().Unique().NotNullable().PrimaryKey()
            .WithColumn("ContainerDbGuid").AsInt64()
            .WithColumn("InventoryProtoGuid").AsInt64()
            .WithColumn("Slot").AsInt32()
            .WithColumn("EntityProtoGuid").AsInt64()
            .WithColumn("ArchiveData").AsBinary();
        
        Create.ForeignKey()
            .FromTable("ControlledEntity").ForeignColumn("ContainerDbGuid")
            .ToTable("Avatar").PrimaryColumn("DbGuid")
            .OnDelete(Rule.Cascade);

        Create.Index()
            .OnTable("Avatar").OnColumn("ContainerDbGuid");
        
        Create.Index()
            .OnTable("TeamUp").OnColumn("ContainerDbGuid");
        
        Create.Index()
            .OnTable("Item").OnColumn("ContainerDbGuid");
        
        Create.Index()
            .OnTable("ControlledEntity").OnColumn("ContainerDbGuid");
    }

    public override void Down()
    {
        Delete.ForeignKey()
            .FromTable("ControlledEntity").ForeignColumn("ContainerDbGuid")
            .ToTable("Avatar").PrimaryColumn("DbGuid");
        
        Delete.ForeignKey()
            .FromTable("Item").ForeignColumn("ContainerDbGuid")
            .ToTable("Player").PrimaryColumn("DbGuid");
        
        Delete.ForeignKey()
            .FromTable("Item").ForeignColumn("ContainerDbGuid")
            .ToTable("Avatar").PrimaryColumn("DbGuid");
        
        Delete.ForeignKey()
            .FromTable("Item").ForeignColumn("ContainerDbGuid")
            .ToTable("TeamUp").PrimaryColumn("DbGuid");
        
        Delete.ForeignKey()
            .FromTable("TeamUp").ForeignColumn("ContainerDbGuid")
            .ToTable("Player").PrimaryColumn("DbGuid");
        
        Delete.ForeignKey()
            .FromTable("Avatar").ForeignColumn("ContainerDbGuid")
            .ToTable("Player").PrimaryColumn("DbGuid");
        
        Delete.ForeignKey()
            .FromTable("Player").ForeignColumn("DbGuid")
            .ToTable("Account").PrimaryColumn("Id");
        
        Delete.Table("ControlledEntity");
        Delete.Table("Item");
        Delete.Table("TeamUp");
        Delete.Table("Avatar");
        Delete.Table("Player");
        Delete.Table("Account");
    }
}