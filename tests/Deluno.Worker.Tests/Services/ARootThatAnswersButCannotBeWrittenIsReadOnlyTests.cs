using System.Security.AccessControl;
using System.Security.Principal;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// A library root that answers but cannot be written to (a read-only mount, a share without write permission) is called ReadOnly, the
/// library is paused, Activity says so once and the problem names the folder (coverage register, engines). The folder is real and made
/// unwritable the way the operating system does it: a deny rule on Windows, a read-only mode elsewhere. Refs #1296.
/// </summary>
public sealed class ARootThatAnswersButCannotBeWrittenIsReadOnlyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deluno-readonly-root-").FullName;
    private readonly List<string> _activity = [];

    public void Dispose()
    {
        MakeWritable(_root);
        Directory.Delete(_root, recursive: true);
    }

    private static LibraryItem Library(string id, string rootPath)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            id, $"Library {id}", "movies", "main", rootPath, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private static void MakeReadOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(path);
            var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles | FileSystemRights.WriteData, AccessControlType.Deny));
            directory.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private static void MakeWritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(path);
            var security = directory.GetAccessControl();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Deny)
                {
                    security.RemoveAccessRuleSpecific(rule);
                }
            }

            directory.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A superuser writes anywhere, so a folder cannot be made unwritable for it and the question cannot be asked.</summary>
    private static bool CannotBeMadeUnwritable() => !OperatingSystem.IsWindows() && Environment.UserName == "root";

    [Fact]
    public async Task A_folder_that_exists_but_refuses_a_new_file_is_ReadOnly_pauses_its_library_and_is_said_once_and_a_writable_one_beside_it_is_left_alone()
    {
        if (CannotBeMadeUnwritable())
        {
            return; // reason: running as the superuser, where no folder refuses a write; the same service is proven on a Windows or ordinary account
        }

        var feed = new Mock<IActivityFeedRepository>();
        feed.Setup(item => item.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((category, message, _, _, _, _, _) => _activity.Add($"{category}|{message}"))
            .ReturnsAsync((ActivityEventItem)null!);
        var fine = Directory.CreateDirectory(Path.Combine(_root, "fine")).FullName;
        var locked = Directory.CreateDirectory(Path.Combine(_root, "locked")).FullName;
        MakeReadOnly(locked);
        var service = new LibraryAvailabilityService(feed.Object, TimeProvider.System, NullLogger<LibraryAvailabilityService>.Instance);
        var libraries = new[] { Library("locked-one", locked), Library("fine-one", fine) };

        var availability = await service.ReadAsync(libraries, CancellationToken.None);
        await service.ReadAsync(libraries, CancellationToken.None);

        Assert.False(availability.IsUsable("locked-one"));
        Assert.True(availability.IsUsable("fine-one"));
        var status = availability.Statuses["locked-one"];
        Assert.True(status.Reachable);
        Assert.Equal(false, status.Writable);
        Assert.Equal(LibraryRootProblem.ReadOnly, status.Problem);
        Assert.Contains("answers, but Deluno cannot write to it", status.Message);
        Assert.Contains(locked, status.Message);
        var said = Assert.Single(_activity);
        Assert.StartsWith("library.paused|", said);
        Assert.Contains("Deluno has paused it", said);
        Assert.Empty(Directory.GetFiles(locked));
        Assert.Empty(Directory.GetFiles(fine));
    }
}
