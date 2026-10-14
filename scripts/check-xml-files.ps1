<#
.SYNOPSIS
    Every tracked XML file (project, props, targets, runsettings, nuspec, config, resx, xml) parses as XML.

.DESCRIPTION
    A comment holding "--" in tests/coverage.runsettings made every .NET test project refuse to start in CI, so
    no backend test ran at all (Refs #1268). The test runner only says so when it is handed the file; this
    check says it on every push, before anything runs.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$patterns = @('*.csproj', '*.props', '*.targets', '*.runsettings', '*.nuspec', '*.config', '*.resx', '*.xml', '*.slnx')
$files = @(& git -C $root ls-files -- $patterns)
$bad = @()
foreach ($relative in $files) {
    # Captured replies of other apps keep their own (sometimes malformed) shape on purpose.
    if ($relative -match '(^|/)(fixtures|Fixtures)/') { continue }
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path)) { continue }
    try {
        $document = New-Object System.Xml.XmlDocument
        $document.Load($path)
    }
    catch {
        $reason = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        $bad += "${relative}: $reason"
    }
}
if ($bad.Count -gt 0) {
    $bad | ForEach-Object { Write-Host $_ }
    Write-Error "$($bad.Count) XML file(s) do not parse."
    exit 1
}
Write-Host "$($files.Count) XML files parse."
