using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Converted a companion tool login saved under Weir's former name to the
/// <c>weir</c> tool id. A fresh install has nothing to convert, so this does
/// nothing; the number stays so the numbering stays intact.
///
/// <para>An install that already ran the conversion holds the checksum of the old
/// SQL, so that checksum is listed as superseded and the row is adopted.</para>
/// </summary>
public sealed class V0065CompanionToolWeir : SqliteSqlMigration
{
    public override int Version => 65;

    public override string Name => "companion_tool_weir";

    protected override string Sql => "SELECT 1;";

    protected override IEnumerable<string> SupersededChecksumValues =>
        ["715f8f482a35a1c1068207cd1e5395af42c5eb583d13fc9028b37e02180ac118"];
}
