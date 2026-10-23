using Deluno.Api.Updates;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UpdateEnvironmentCollection { public const string Name = "UpdateEnvironment"; }

/// <summary>
/// The update status says whether this is a Docker install and which image it runs from the container's own
/// environment: either of the two "running in a container" flags, and the image reference or digest the compose
/// file passes in. Each reading is checked here, with the variables put back afterwards.
/// </summary>
[Collection(UpdateEnvironmentCollection.Name)]
public sealed class UpdateStatusReadsTheContainerEnvironmentTests
{
    private static readonly string[] Names =
        ["DOTNET_RUNNING_IN_CONTAINER", "ASPNETCORE_RUNNING_IN_CONTAINER", "DELUNO_IMAGE_REF", "DELUNO_IMAGE_DIGEST"];

    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static async Task<UpdateStatusResponse> StatusWith(params (string Name, string? Value)[] environment)
    {
        var saved = Names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var dataRoot = Path.Combine(Path.GetTempPath(), "deluno-update-environment", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            foreach (var name in Names) Environment.SetEnvironmentVariable(name, null);
            foreach (var (name, value) in environment) Environment.SetEnvironmentVariable(name, value);
            var orchestrator = new DefaultUpdateOrchestrator(
                Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
                new FakeNotificationService(),
                new FakeOutboundNotificationService());
            return await orchestrator.GetStatusAsync(CancellationToken.None);
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(dataRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Neither_container_flag_set_is_a_manual_install()
    {
        var status = await StatusWith();
        Assert.Equal(UpdateInstallKinds.Manual, status.InstallKind);
        Assert.Null(status.CurrentImageRef);
        Assert.Null(status.CurrentImageDigest);
    }

    [Fact]
    public async Task The_dotnet_container_flag_makes_it_a_docker_install()
        => Assert.Equal(UpdateInstallKinds.Docker, (await StatusWith(("DOTNET_RUNNING_IN_CONTAINER", "true"))).InstallKind);

    [Fact]
    public async Task The_aspnetcore_container_flag_alone_makes_it_a_docker_install()
        => Assert.Equal(UpdateInstallKinds.Docker, (await StatusWith(("ASPNETCORE_RUNNING_IN_CONTAINER", "true"))).InstallKind);

    [Fact]
    public async Task A_flag_that_is_false_or_not_a_boolean_is_not_a_container()
    {
        var status = await StatusWith(("DOTNET_RUNNING_IN_CONTAINER", "false"), ("ASPNETCORE_RUNNING_IN_CONTAINER", "maybe"));
        Assert.Equal(UpdateInstallKinds.Manual, status.InstallKind);
    }

    [Fact]
    public async Task The_digest_is_read_from_the_image_reference_when_none_is_given()
    {
        var status = await StatusWith(("DELUNO_IMAGE_REF", "ghcr.io/example/deluno@" + Digest));
        Assert.Equal("ghcr.io/example/deluno@" + Digest, status.CurrentImageRef);
        Assert.Equal(Digest, status.CurrentImageDigest);
    }

    [Fact]
    public async Task A_tag_only_reference_has_no_digest()
    {
        var status = await StatusWith(("DELUNO_IMAGE_REF", "ghcr.io/example/deluno:1.2.3"));
        Assert.Equal("ghcr.io/example/deluno:1.2.3", status.CurrentImageRef);
        Assert.Null(status.CurrentImageDigest);
    }

    [Fact]
    public async Task A_digest_given_on_its_own_wins_over_the_reference()
    {
        var status = await StatusWith(("DELUNO_IMAGE_REF", "ghcr.io/example/deluno@" + Digest), ("DELUNO_IMAGE_DIGEST", "sha256:given"));
        Assert.Equal("sha256:given", status.CurrentImageDigest);
    }
}
