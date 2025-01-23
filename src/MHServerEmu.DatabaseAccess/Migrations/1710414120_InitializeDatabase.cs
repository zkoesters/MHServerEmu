using System.Data;
using FluentMigrator;

namespace MHServerEmu.DatabaseAccess.Migrations;

[Migration(1710414120)]
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
            .WithColumn("IsBanned").AsInt32().NotNullable()
            .WithColumn("IsArchived").AsInt32().NotNullable()
            .WithColumn("IsPasswordExpired").AsInt32().NotNullable();
        
        Create.Table("Avatar")
            .WithColumn("AccountId").AsInt64().NotNullable().PrimaryKey()
            .ForeignKey("Account", "Id").OnDelete(Rule.Cascade)
            .WithColumn("RawPrototype").AsInt64().NotNullable().PrimaryKey()
            .WithColumn("RawCostume").AsInt64().Nullable()
            .WithColumn("RawAbilityKeyMapping").AsBinary().Nullable();

        Create.Table("Player")
            .WithColumn("AccountId").AsInt64().Unique().NotNullable().PrimaryKey()
            .ForeignKey("Account", "Id").OnDelete(Rule.Cascade)
            .WithColumn("RawRegion").AsInt64().Nullable()
            .WithColumn("RawAvatar").AsInt64().Nullable()
            .WithColumn("RawWaypoint").AsInt64().Nullable()
            .WithColumn("AOIVolume").AsInt32().Nullable();
    }

    public override void Down()
    {
        Delete.Table("Player");
        Delete.Table("Avatar");
        Delete.Table("Account");
    }
}