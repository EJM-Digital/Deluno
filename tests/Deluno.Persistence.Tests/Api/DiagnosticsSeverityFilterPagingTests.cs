using Deluno.Api.Monitoring;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>#1401: the severity filter is applied before the page is cut, so a page of errors is full and every match is read once.</summary>
public sealed class DiagnosticsSeverityFilterPagingTests : IDisposable
{
    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
    private readonly TestStorage _storage = TestStorage.Create();

    public void Dispose() => _storage.Dispose();

    private async Task InsertAsync(int n, string category)
    {
        await using var connection = await _storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO activity_events (id, category, message, created_utc) VALUES (@id, @c, @m, @t);";
        void P(string name, object value) { var p = insert.CreateParameter(); p.ParameterName = name; p.Value = value; insert.Parameters.Add(p); }
        P("@id", $"id{n:D4}");
        P("@c", category);
        P("@m", $"row {n}");
        P("@t", DateTimeOffset.Parse("2026-09-30T12:00:00Z").AddSeconds(n).ToString("O"));
        await insert.ExecuteNonQueryAsync();
    }

    private MonitoringService Service() => new(
        null!, null!, null!, null!, null!, null!, _storage.Factory,
        Microsoft.Extensions.Options.Options.Create(new StoragePathOptions { DataRoot = _storage.DataRoot }),
        null!, null!, Clock, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<MonitoringService>.Instance);

    [Fact]
    public async Task Asking_for_errors_reads_full_pages_and_every_error_exactly_once()
    {
        await MigratedDatabaseTemplate.CopyIntoAsync(_storage.DataRoot);
        // Newest first: 2 infos, an error, 3 infos, an error, 2 infos, an error, an error, then older infos.
        var errorRows = new HashSet<int> { 3, 7, 10, 11 };
        for (var n = 1; n <= 14; n++)
            await InsertAsync(n, errorRows.Contains(n) ? "job.failed" : "job.info");

        var service = Service();
        string? token = null;
        var pageSizes = new List<int>();
        var seen = new List<string>();
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await service.SearchDiagnosticsAsync(
                new MonitoringDiagnosticsQuery(null, null, "error", null, new PageRequest(3, token)), CancellationToken.None);
            pageSizes.Add(page.Items.Count);
            seen.AddRange(page.Items.Select(i => i.Message));
            if (page.NextPageToken is null) break;
            token = page.NextPageToken;
        }

        Assert.Equal(["row 11", "row 10", "row 7", "row 3"], seen);
        Assert.Equal([3, 1], pageSizes);
    }

    [Fact]
    public async Task The_last_page_of_errors_has_no_next_page_when_nothing_else_matches()
    {
        await MigratedDatabaseTemplate.CopyIntoAsync(_storage.DataRoot);
        for (var n = 1; n <= 6; n++)
            await InsertAsync(n, n == 2 ? "job.failed" : "job.info");

        var page = await Service().SearchDiagnosticsAsync(
            new MonitoringDiagnosticsQuery(null, null, "error", null, new PageRequest(1, null)), CancellationToken.None);

        Assert.Equal(["row 2"], page.Items.Select(i => i.Message).ToArray());
        Assert.Null(page.NextPageToken);
    }
}
