using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0002UserSecurityStamp : SqliteSqlMigration
{
    public override int Version => 2;

    public override string Name => "user_security_stamp";

    protected override string Sql =>
        """
        ALTER TABLE users ADD COLUMN security_stamp TEXT NULL;

        UPDATE users
        SET security_stamp = lower(hex(randomblob(16)))
        WHERE security_stamp IS NULL OR length(trim(security_stamp)) = 0;
        """;
}
