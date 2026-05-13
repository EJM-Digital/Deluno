using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class CustomFormatPersistenceTests
{
    [Fact]
    public async Task CreateCustomFormatAsync_persists_media_type_and_trash_id_for_movies_and_tv()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-13T05:00:00Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(
            storage.Factory,
            timeProvider,
            TestSecretProtection.Create(storage));

        var movieFormat = await repository.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(
                Name: "Movie HDR",
                MediaType: "movies",
                Score: 150,
                TrashId: "trash-hdr",
                Conditions: """[{"type":"hdr","value":"hdr10"}]""",
                UpgradeAllowed: true),
            CancellationToken.None);

        var tvFormat = await repository.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(
                Name: "TV HDR",
                MediaType: "tv",
                Score: 120,
                TrashId: "trash-hdr",
                Conditions: """[{"type":"hdr","value":"hdr10"}]""",
                UpgradeAllowed: true),
            CancellationToken.None);

        var formats = await repository.ListCustomFormatsAsync(CancellationToken.None);

        var storedMovie = Assert.Single(formats, item => item.Id == movieFormat.Id);
        var storedTv = Assert.Single(formats, item => item.Id == tvFormat.Id);

        Assert.Equal("movies", storedMovie.MediaType);
        Assert.Equal("trash-hdr", storedMovie.TrashId);
        Assert.Equal("tv", storedTv.MediaType);
        Assert.Equal("trash-hdr", storedTv.TrashId);
    }
}
