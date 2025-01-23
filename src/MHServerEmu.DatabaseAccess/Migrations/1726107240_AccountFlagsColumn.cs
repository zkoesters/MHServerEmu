using FluentMigrator;

namespace MHServerEmu.DatabaseAccess.Migrations;

[Migration(1726107240)]
public class AccountFlagsColumn : Migration {
    public override void Up()
    {
        Rename.Column("IsBanned").OnTable("Account").To("Flags");
        Delete.Column("IsArchived").FromTable("Account");
        Delete.Column("IsPasswordExpired").FromTable("Account");
    }

    public override void Down()
    {
        Create.Column("IsArchived").OnTable("Account").AsInt32().NotNullable();
        Create.Column("IsPasswordExpired").OnTable("Account").AsInt32().NotNullable();
        Rename.Column("Flags").OnTable("Account").To("IsBanned");
    }
}