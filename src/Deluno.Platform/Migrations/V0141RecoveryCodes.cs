using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1392: recovery codes, the way back into an account whose username or password is forgotten. Only a salted hash of
/// each code is kept; a used code keeps its row (with its time) so the screen can say how many are left and an older
/// account can be told apart from one that has made its codes. An install that has none simply has no rows.
/// </summary>
public sealed class V0141RecoveryCodes : SqliteSqlMigration
{
    public override int Version => 141;

    public override string Name => "recovery_codes";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS recovery_codes (
            id TEXT PRIMARY KEY,
            user_id TEXT NOT NULL,
            code_hash TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            used_utc TEXT NULL,
            FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_recovery_codes_user
            ON recovery_codes (user_id, used_utc);
        """;
}
