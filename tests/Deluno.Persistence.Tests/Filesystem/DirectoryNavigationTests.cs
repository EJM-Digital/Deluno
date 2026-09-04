using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// Going up in the folder picker.
///
/// <para>Found on #81 by using the picker on an installed build: browsing to
/// <c>C:\InstallerTest\Films\</c> reported its parent as
/// <c>C:\InstallerTest\Films</c> — the same folder without its trailing
/// separator. Pressing up landed you where you already were, so the picker
/// could descend and never climb, and typing a path was the only way out.</para>
///
/// <para>Two reasonable decisions met: the browse endpoint appends a trailing
/// separator so a drive root renders as <c>C:\</c> rather than <c>C:</c>, and
/// <c>Directory.GetParent</c> reads a trailing separator as "this path is the
/// directory" and answers with the path itself.</para>
/// </summary>
public sealed class DirectoryNavigationTests
{
    [Fact]
    public void Going_up_moves_to_a_different_directory()
    {
        var root = Directory.CreateTempSubdirectory("deluno-nav").FullName;
        try
        {
            var nested = Path.Combine(root, "media", "films");
            Directory.CreateDirectory(nested);

            // The trailing separator is what the browse endpoint hands over,
            // and what the original implementation choked on.
            var parent = DirectoryNavigation.ParentOf(nested + Path.DirectorySeparatorChar);

            Assert.Equal(Path.Combine(root, "media"), parent);
            Assert.NotEqual(nested, parent);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_answer_is_the_same_with_or_without_a_trailing_separator()
    {
        var root = Directory.CreateTempSubdirectory("deluno-nav").FullName;
        try
        {
            var nested = Path.Combine(root, "media");
            Directory.CreateDirectory(nested);

            Assert.Equal(
                DirectoryNavigation.ParentOf(nested),
                DirectoryNavigation.ParentOf(nested + Path.DirectorySeparatorChar));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Walking_up_reaches_the_top_rather_than_looping()
    {
        var root = Directory.CreateTempSubdirectory("deluno-nav").FullName;
        try
        {
            var path = Path.Combine(root, "a", "b", "c");
            Directory.CreateDirectory(path);

            var visited = new List<string>();
            for (var step = 0; step < 64; step++)
            {
                var parent = DirectoryNavigation.ParentOf(path);
                if (parent is null)
                {
                    // A drive root, where the caller shows the drive list.
                    return;
                }

                // The defect made this loop for ever on one directory.
                Assert.DoesNotContain(parent, visited);
                visited.Add(parent);
                path = parent;
            }

            Assert.Fail($"Walking up never reached the top; it visited {visited.Count} directories.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_drive_root_has_nowhere_above_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Null rather than "C:", because the caller answers this by showing the
        // drive list, not by browsing a path that does not mean anything.
        Assert.Null(DirectoryNavigation.ParentOf(@"C:\"));
        Assert.Null(DirectoryNavigation.ParentOf(@"C:"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_in_gives_nothing_back(string? path)
    {
        Assert.Null(DirectoryNavigation.ParentOf(path));
    }
}
