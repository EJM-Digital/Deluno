using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Deluno.Companions;

/// <summary>
/// One vendor build Deluno may run, and how it proves it is that build.
/// </summary>
/// <param name="Sha256">
/// The SHA-256 the file must have. Pinned in code for a build verified by a
/// vendor-authenticated channel beforehand, or read from the vendor's published
/// checksum file at download time. Never taken from the file itself.
/// </param>
/// <param name="MustBeSigned">Also require a valid Authenticode signature.</param>
/// <param name="MinimumBytes">
/// A floor that catches a challenge or error page saved under the installer's
/// name - which happened on the rig with a scripted SourceForge download.
/// </param>
public sealed record CompanionArtifact(string FileName, Uri Url, string Sha256, bool MustBeSigned, long MinimumBytes);

/// <summary>
/// The builds Deluno installs, pinned.
///
/// <para>SABnzbd and qBittorrent are the versions the end-to-end rig was walked
/// against (<c>scripts/lab/rig-software.json</c>): SABnzbd is Authenticode-signed
/// and hash-pinned; qBittorrent's Windows installer is unsigned, so its hash was
/// pinned after checking the release's detached PGP signature against the
/// project's key. Each tool updates itself afterwards.</para>
///
/// <para>Weir is taken from its latest GitHub release and checked against the
/// <c>SHA256SUMS.txt</c> published with that release.</para>
/// </summary>
public static class CompanionCatalog
{
    public static readonly CompanionArtifact Qbittorrent = new(
        "qbittorrent_5.2.1_x64_setup.exe",
        new Uri("https://github.com/qbittorrent/qBittorrent/releases/download/release-5.2.1/qbittorrent_5.2.1_x64_setup.exe"),
        "02A177C43C08DF4DB30A8F1C2E3D71D51590403EB6BA8B8B2B7D9CF00E68E18C",
        MustBeSigned: false,
        MinimumBytes: 20_000_000);

    public static readonly CompanionArtifact Sabnzbd = new(
        "SABnzbd-5.1.3-win-setup.exe",
        new Uri("https://github.com/sabnzbd/sabnzbd/releases/download/5.1.3/SABnzbd-5.1.3-win-setup.exe"),
        "136D8EF811677A1C65917F9E71AFCC16F6B3D60F7294E426DAC6210D4D186DC6",
        MustBeSigned: true,
        MinimumBytes: 20_000_000);

    /// <summary>The versions the pinned artifacts above are (#1118: compared with what is installed before an update is offered).</summary>
    public const string QbittorrentVersion = "5.2.1";
    public const string SabnzbdVersion = "5.1.3";

    public const string WeirRepository = Deluno.Contracts.ProjectRepositories.Weir;
    public const string WeirSetupAsset = "Weir-win-Setup.exe";
    public const string WeirChecksumsAsset = "SHA256SUMS.txt";
}

public sealed class CompanionDownloadException(string message) : Exception(message);

/// <summary>
/// Fetches a vendor build and refuses to hand it on unless it is exactly that
/// build. A failed check deletes the file: nothing unverified is left where
/// something could run it.
/// </summary>
public interface ICompanionDownloader
{
    Task<string> DownloadAsync(CompanionArtifact artifact, string folder, CancellationToken cancellationToken);
}

public sealed class CompanionDownloader(IHttpClientFactory httpClientFactory) : ICompanionDownloader
{
    public const string ClientName = "companion-downloads";

    public async Task<string> DownloadAsync(CompanionArtifact artifact, string folder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, artifact.FileName);
        try
        {
            using (var response = await httpClientFactory.CreateClient(ClientName).GetAsync(artifact.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new CompanionDownloadException($"{artifact.Url.Host} answered HTTP {(int)response.StatusCode} for {artifact.FileName}.");
                }

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(path);
                await source.CopyToAsync(target, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            TryDelete(path);
            throw new CompanionDownloadException($"Deluno could not download {artifact.FileName} from {artifact.Url.Host}. Check this computer's internet connection, or whether something is blocking {artifact.Url.Host}.");
        }

        var problem = Verify(artifact, path);
        if (problem is not null)
        {
            TryDelete(path);
            throw new CompanionDownloadException(problem);
        }

        return path;
    }

    /// <returns>Null when the file is the build the catalogue names; otherwise why not.</returns>
    public static string? Verify(CompanionArtifact artifact, string path)
    {
        var length = new FileInfo(path).Length;
        if (length < artifact.MinimumBytes)
        {
            return $"{artifact.FileName} is {length:N0} bytes, too small to be the installer. Nothing was installed.";
        }

        string actual;
        using (var stream = File.OpenRead(path))
        {
            actual = Convert.ToHexString(SHA256.HashData(stream));
        }

        if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return $"{artifact.FileName} is not the build Deluno expects (its SHA-256 does not match). Nothing was installed.";
        }

        if (artifact.MustBeSigned && !AuthenticodeTrust.IsValid(path))
        {
            return $"{artifact.FileName} does not carry a valid signature. Nothing was installed.";
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Authenticode, through Windows' own trust provider.</summary>
public static class AuthenticodeTrust
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool IsValid(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var file = new WinTrustFileInfo(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WinTrustData(filePointer);
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string path)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;              // 2 = no UI
        public uint RevocationChecks;      // 0 = none beyond policy
        public uint UnionChoice;           // 1 = file
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UrlReference;
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = null;
            ProviderFlags = 0x00000040; // safer
            UiContext = 0;
        }
    }
}
