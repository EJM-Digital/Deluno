using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A generated per-connection secret Overseerr/Jellyseerr must send back as
/// its webhook's Authorization header value (#851) — without it, the inbound
/// webhook route has no session and nothing else scopes the call. Encrypted
/// at rest through the same secret-protection mechanism as
/// <c>seerr_servers.api_key</c>. Null until a connection's card generates
/// one, which is also what every existing connection starts as: its webhook
/// stays refused until the secret is set up, never quietly accepting calls
/// it never asked to protect.
/// </summary>
public sealed class V0110SeerrWebhookSecret : SqliteSqlMigration
{
    public override int Version => 110;

    public override string Name => "seerr_webhook_secret";

    protected override string Sql =>
        "ALTER TABLE seerr_servers ADD COLUMN webhook_secret TEXT NULL;";
}
