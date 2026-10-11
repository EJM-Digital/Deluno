<#
.SYNOPSIS
    Runs the real component picker (apps/windows-tray ComponentPickerDialog) on this PC and drives it with the real
    Invoke-ComponentPicker.ps1, so the driver can be proved here instead of on the golden VM.

.DESCRIPTION
    Builds a scratch host in the temp folder (nothing is added to the repository or the solution) that shows the real dialog with a
    fake install service: no program is listed as installed, and every download fails at once, so nothing is ever installed. Then, for
    -Runs times, it opens the picker, runs the driver against it, and checks that all six rows were ticked and that Install reached
    the progress and finish view. Exit code 1 when any run fails. Needs the .NET SDK and a desktop; Windows PowerShell 5.1.

.EXAMPLE
    ./scripts/golden-path/Test-ComponentPicker.ps1 -Runs 10
#>
[CmdletBinding()]
param([int] $Runs = 10, [switch] $KeepHost, [switch] $ForceKeys)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('picker-host-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null

Set-Content -Path (Join-Path $work 'PickerHost.csproj') -Encoding utf8 -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Platforms>x64</Platforms>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$repo\apps\windows-tray\Deluno.Tray.csproj" /></ItemGroup>
</Project>
"@
Set-Content -Path (Join-Path $work 'Program.cs') -Encoding utf8 -Value @'
using Deluno.Api.Network;
using Deluno.Tray;

// The real picker, with nothing installed on this PC as far as it can tell, and downloads that fail at once.
internal sealed class NothingInstalled : IInstalledProgramsReader { public IReadOnlyList<string> ReadDisplayNames() => Array.Empty<string>(); }
internal sealed class NoDownloads : IComponentDownloader { public Task<string?> DownloadAsync(string url, string suggestedFileName, CancellationToken cancellationToken) => Task.FromResult<string?>(null); }

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        var service = new ComponentInstallService(programs: new NothingInstalled(), downloader: new NoDownloads());
        using var dialog = new ComponentPickerDialog(service);
        var result = dialog.ShowDialog();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "picker-host-result.txt"), result.ToString());
        return 0;
    }
}
'@

Write-Host 'Building the scratch picker host...'
$build = & dotnet build (Join-Path $work 'PickerHost.csproj') -c Release -o (Join-Path $work 'out') 2>&1
if ($LASTEXITCODE -ne 0) { $build | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }; throw 'The scratch host did not build.' }
$exe = Join-Path $work 'out\PickerHost.exe'

$fail = 0
for ($run = 1; $run -le $Runs; $run++) {
    $log = Join-Path $work "picker-$run.log"
    $resultFile = Join-Path ([IO.Path]::GetTempPath()) 'picker-host-result.txt'
    Remove-Item $resultFile -ErrorAction SilentlyContinue
    $host1 = Start-Process -FilePath $exe -PassThru
    try {
        $driver = Join-Path $PSScriptRoot 'Invoke-ComponentPicker.ps1'
        $p = Start-Process -FilePath powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$driver`" -UsenetUser tester -UsenetPassword tester-pass -LogPath `"$log`" -WaitForWindowSeconds 60 -InstallMinutes 3 -SkipOpenDeluno$(if ($ForceKeys) { " -ForceKeys" })" -PassThru -Wait -WindowStyle Hidden
        $text = if (Test-Path $log) { Get-Content -Raw $log } else { '' }
        $ticks = ([regex]::Matches($text, "row '[^']+': On")).Count
        $ok = ($p.ExitCode -eq 0) -and ($ticks -ge 6) -and ($text -match 'pressed Install selected') -and ($text -match 'Finished\.') -and ($text -cnotmatch 'FAILED')
        Write-Host ("run {0,2}: {1}  (exit {2}, {3} rows ticked, install pressed: {4}, finish screen: {5})" -f $run, $(if ($ok) { 'ok' } else { 'FAILED' }), $p.ExitCode, $ticks, ($text -match 'pressed Install selected'), ($text -match 'Finished\.')) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
        if (-not $ok) { $fail++; Write-Host ($text -split "`n" | Select-Object -Last 12 | ForEach-Object { '      ' + $_ }) }
    }
    finally { if (-not $host1.HasExited) { Stop-Process -Id $host1.Id -Force }; Start-Sleep -Milliseconds 800 }
}
Write-Host ("{0} of {1} runs passed." -f ($Runs - $fail), $Runs)
if (-not $KeepHost) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
if ($fail -gt 0) { exit 1 }
