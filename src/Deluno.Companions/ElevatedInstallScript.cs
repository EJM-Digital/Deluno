namespace Deluno.Companions;

/// <summary>
/// The one elevated step for every installer Deluno runs with administrator rights: a
/// per-machine .msi, a vendor .exe setup, and the Visual C++ runtime a package needs
/// first (#1052, #1061). Nothing is ever run as administrator straight from a folder a
/// user can write to. It is run as an
/// inline PowerShell command (nothing on disk to swap). Only administrators can create
/// a folder under <c>%ProgramFiles%</c>, so no parent of the install folder can belong
/// to a user (<c>%ProgramData%\Deluno</c> is Deluno's own data folder and is never used
/// or locked). The step still refuses a folder that is a junction or symbolic link,
/// takes ownership of <c>%ProgramFiles%\Deluno installer</c> and locks it
/// to SYSTEM and Administrators, then makes a brand-new folder with a random name,
/// locks that the same way before anything goes in, copies the downloaded installer into
/// it, refuses to run it unless its SHA-256 is the one the tray verified (and, when the
/// download was accepted by signature rather than a pinned checksum, unless that very
/// copy carries a valid signature from the expected publisher), runs it (msiexec for
/// an .msi) from that folder, and deletes the folder afterwards. For a package that installs a
/// Windows service Deluno does not want (Transmission's MSI registers an auto-start
/// service, run as LocalService and bound to every address) it stops and disables
/// that service in the same prompt.
/// </summary>
public static class ElevatedInstallScript
{
    /// <summary>Exit code the script returns when the copy does not match the verified hash.</summary>
    public const int HashMismatchExitCode = 4001;

    /// <summary>Exit code when the install folder could not be locked down.</summary>
    public const int FolderExitCode = 4002;

    /// <summary>Exit code when the install folder, or a parent of it, is a junction or symbolic link.</summary>
    public const int ReparsePointExitCode = 4003;

    /// <summary>Exit code when the copy about to run does not carry a valid signature from the expected publisher.</summary>
    public const int SignatureExitCode = 4004;

    /// <summary>
    /// The full path to Windows PowerShell, so the elevated launch never searches the current
    /// folder (or the PATH) for a <c>powershell.exe</c> a user could have put there.
    /// </summary>
    public static string PowerShellPath { get; } = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// The signer check, as PowerShell: true only when a whole CN or O value of the signer's
    /// subject is exactly the publisher. Each subject part is read on its own line, so text
    /// inside a quoted value (CN="Evil, O=SignPath Foundation") is one value and never matches.
    /// A subject with a line break anywhere in it is refused outright: a CN holding a newline
    /// would otherwise start a line that reads as an O= part of its own.
    /// </summary>
    public const string PublisherFunction = """
        function Test-Publisher($cert, $wanted) { if ($cert.SubjectName.Name -match '[\x0d\x0a]') { return $false }; foreach ($line in ($cert.SubjectName.Format($true) -split '\r?\n')) { if ($line -match '^\s*(CN|O)=(.*)$') { $v = $Matches[2].Trim(); if ($v.Length -ge 2 -and $v.StartsWith('"') -and $v.EndsWith('"')) { $v = $v.Substring(1, $v.Length - 2).Replace('""', '"') }; if ($v -eq $wanted) { return $true } } }; return $false }
        """;

    /// <param name="asExe">Run the copy itself (a vendor .exe setup) instead of msiexec.</param>
    /// <param name="alsoSuccessCodes">Exit codes besides 0 that count as done (3010: succeeded, restart needed).</param>
    public static string Build(string installerPath, string sha256, string? silentArgs, string? serviceName, string? publisher = null, bool requireSignature = false, string? baseFolder = null, bool asExe = false, IReadOnlyList<int>? alsoSuccessCodes = null)
    {
        if (requireSignature && string.IsNullOrWhiteSpace(publisher))
        {
            // A signature with no named publisher would accept any signer Windows trusts.
            throw new ArgumentException("A signature check needs the publisher it must be signed by.", nameof(publisher));
        }

        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "$code = 0",
            "$fresh = $null",
            PublisherFunction,
            "function Test-Link($p) { (Test-Path -LiteralPath $p) -and (((Get-Item -LiteralPath $p -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) }",
            // Take the folder over, drop every entry it had, and leave only SYSTEM and Administrators (owner: Administrators).
            "function Lock-Folder($p) { & icacls.exe $p /setowner '*S-1-5-32-544' | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'owner' }; & icacls.exe $p /reset | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'reset' }; & icacls.exe $p /inheritance:r | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'inherit' }; & icacls.exe $p /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'grant' } }",
            "try {",
            // Under Program Files only administrators can create a folder, so nothing a user made can be a parent of the
            // install folder. (%ProgramData%\Deluno is Deluno's own data folder and is left alone.)
            baseFolder is null ? "$parent = $env:ProgramFiles" : $"$parent = {Quote(baseFolder)}",
            "$root = Join-Path $parent 'Deluno installer'",
            $"foreach ($p in @($parent, $root)) {{ if (Test-Link $p) {{ $code = {ReparsePointExitCode}; throw 'link' }} }}",
            "if (-not (Test-Path -LiteralPath $root)) { New-Item -ItemType Directory -Path $root | Out-Null }",
            $"foreach ($p in @($parent, $root)) {{ if (Test-Link $p) {{ $code = {ReparsePointExitCode}; throw 'link' }} }}",
            "Lock-Folder $root",
            "$name = [guid]::NewGuid().ToString('N')",
            "$fresh = Join-Path $root $name",
            "New-Item -ItemType Directory -Path $fresh | Out-Null",
            $"if (Test-Link $fresh) {{ $code = {ReparsePointExitCode}; throw 'link' }}",
            "Lock-Folder $fresh",
            asExe ? "$copy = Join-Path $fresh ($name + '.exe')" : "$copy = Join-Path $fresh ($name + '.msi')",
            "$log = Join-Path $fresh ($name + '.log')",
            $"Copy-Item -LiteralPath {Quote(installerPath)} -Destination $copy",
            $"if ((Get-FileHash -Algorithm SHA256 -LiteralPath $copy).Hash -ne {Quote(sha256)}) {{ $code = {HashMismatchExitCode}; throw 'hash' }}",
        };
        if (requireSignature)
        {
            // The copy in the locked folder is the file msiexec runs, so it is the file whose signature is checked
            // (one Get-AuthenticodeSignature call: status and signer come from the same verification).
            lines.Add("$sig = Get-AuthenticodeSignature -LiteralPath $copy");
            lines.Add($"if ($sig.Status -ne 'Valid' -or $null -eq $sig.SignerCertificate) {{ $code = {SignatureExitCode}; throw 'signature' }}");
            // The whole CN or O value of the signer's subject must be the publisher, never a piece of it.
            lines.Add($"if (-not (Test-Publisher $sig.SignerCertificate {Quote(publisher!.Trim())})) {{ $code = {SignatureExitCode}; throw 'publisher' }}");
        }

        if (asExe)
        {
            // The vendor's own setup, run from the locked copy. No argument list is passed when there are none
            // (Start-Process refuses an empty one).
            lines.Add(string.IsNullOrWhiteSpace(silentArgs)
                ? "$process = Start-Process -FilePath $copy -Wait -PassThru"
                : $"$process = Start-Process -FilePath $copy -ArgumentList {Quote(silentArgs)} -Wait -PassThru");
        }
        else
        {
            lines.Add($"$arguments = '/i \"' + $copy + '\" ALLUSERS=1 ' + {Quote(silentArgs ?? string.Empty)} + ' /l*v \"' + $log + '\"'");
            // msiexec by its full path, so nothing named msiexec.exe in another folder can be run instead.
            lines.Add("$process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\msiexec.exe') -ArgumentList $arguments -Wait -PassThru");
        }

        lines.AddRange(new[]
        {
            "$code = $process.ExitCode",
            alsoSuccessCodes is { Count: > 0 } ? $"if (@({string.Join(", ", alsoSuccessCodes)}) -contains $code) {{ $code = 0 }}" : "# no other exit code counts as done",
            "} catch { if ($code -eq 0) { $code = " + FolderExitCode + " } } finally {",
            "if ($fresh) { Remove-Item -LiteralPath $fresh -Recurse -Force -ErrorAction SilentlyContinue }",
            "}",
            "if ($code -ne 0) { exit $code }"
        });
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            lines.Add($"Stop-Service -Name {Quote(serviceName)} -ErrorAction SilentlyContinue");
            lines.Add($"Set-Service -Name {Quote(serviceName)} -StartupType Disabled -ErrorAction SilentlyContinue");
        }

        lines.Add("exit 0");
        return string.Join("\n", lines);
    }

    /// <summary>The arguments for <c>powershell.exe</c> that run <paramref name="script"/> with no file involved.</summary>
    public static string PowerShellArguments(string script)
        => "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
