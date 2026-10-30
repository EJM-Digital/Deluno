using System.Security.Cryptography;
using System.Text;

namespace Deluno.Tray.Tests;

/// <summary>
/// The tray, the taskbar and the installer all draw installer/deluno.ico. Windows picks the frame that fits the screen's
/// scale (16 px at 100 percent, 32 at 200, 256 for the large views), so a missing size is a blurred or wrong icon. The real
/// file is read: its header, one frame per size, each frame the size its entry says, and the stamp that says which logo it was built from.
/// </summary>
public sealed class TheIconFileCarriesEveryWindowsSizeTests
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Deluno.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [Fact]
    public void The_icon_file_carries_one_frame_for_each_Windows_size_and_each_frame_is_that_size()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot(), "installer", "deluno.ico"));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        var count = BitConverter.ToUInt16(bytes, 4);

        var found = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            var length = (int)BitConverter.ToUInt32(bytes, entry + 8);
            var offset = (int)BitConverter.ToUInt32(bytes, entry + 12);
            Assert.Equal(width, height);
            Assert.True(offset + length <= bytes.Length, $"The {width} px frame runs past the end of the file.");
            found.Add(width);

            // A frame is a PNG (its own width and height are in its header) or a bitmap (its width is in its header, its height doubled for the mask).
            if (bytes[offset] == 0x89 && bytes[offset + 1] == (byte)'P')
            {
                Assert.Equal(width, (int)ReadBigEndian(bytes, offset + 16));
                Assert.Equal(height, (int)ReadBigEndian(bytes, offset + 20));
            }
            else
            {
                Assert.Equal(width, BitConverter.ToInt32(bytes, offset + 4));
                Assert.Equal(height * 2, BitConverter.ToInt32(bytes, offset + 8));
            }
        }

        Assert.Equal(Sizes, found.Order().ToArray());
    }

    [Fact]
    public void The_icon_was_built_from_the_current_brand_logo()
    {
        var root = RepoRoot();
        var svg = File.ReadAllText(Path.Combine(root, "apps", "web", "public", "favicon.svg")).Replace("\r\n", "\n");
        var logoHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(svg))).ToLowerInvariant();
        var stamp = File.ReadAllText(Path.Combine(root, "installer", "deluno.ico.source-sha256")).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Equal(logoHash, stamp);
    }

    private static uint ReadBigEndian(byte[] bytes, int at) =>
        (uint)(bytes[at] << 24 | bytes[at + 1] << 16 | bytes[at + 2] << 8 | bytes[at + 3]);
}
