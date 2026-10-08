namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// Text that was saved as UTF-8, read back as Windows-1252 and saved again turns "…" into "â€¦" and "—" into "â€”".
/// It reached the screen: the installer's picker showed "Browseâ€¦", and a Movies message read "right now â€” …".
/// No source file carries those sequences.
/// </summary>
public sealed class NoGarbledTextTests
{
    private static readonly string[] Garbled = ["â€", "Ã©", "Ã¨", "Â·", "Â "];

    [Fact]
    public void No_source_file_carries_text_garbled_by_a_wrong_encoding()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var folder in new[] { "src", "apps", "installer", "scripts", "docs" })
        {
            var path = Path.Combine(root, folder);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                         .Where(file => Path.GetExtension(file) is ".cs" or ".ts" or ".tsx" or ".json" or ".md" or ".ps1" or ".mjs")
                         .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")
                                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                        && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                        && !file.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}")))
            {
                var text = File.ReadAllText(file);
                if (Garbled.Any(sequence => text.Contains(sequence, StringComparison.Ordinal)))
                {
                    offenders.Add(Path.GetRelativePath(root, file));
                }
            }
        }

        Assert.True(offenders.Count == 0, "Garbled text (UTF-8 read as Windows-1252) in: " + string.Join(", ", offenders));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Deluno.Infrastructure")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
