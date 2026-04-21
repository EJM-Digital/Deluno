using Deluno.Contracts.Manifest;

namespace Deluno.Infrastructure.Storage;

public static class DelunoStorageLayout
{
    public static IReadOnlyList<DatabaseDescriptor> Databases => DelunoSystemManifest.Databases;
}

