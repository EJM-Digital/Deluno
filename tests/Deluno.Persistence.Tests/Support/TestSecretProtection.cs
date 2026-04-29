using Deluno.Platform.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Deluno.Persistence.Tests.Support;

internal static class TestSecretProtection
{
    public static ISecretProtector Create(TestStorage storage)
        => new DataProtectionSecretProtector(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(storage.DataRoot, "protection-keys"))));
}
