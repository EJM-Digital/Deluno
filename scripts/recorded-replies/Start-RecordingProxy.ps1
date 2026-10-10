<#
.SYNOPSIS
    A pass-through proxy that writes down every request and reply that crosses it (#1237). Runs INSIDE the golden VM.

.DESCRIPTION
    Deluno's side of its talk with Weir is recorded by putting this between them, so the requests are the real ones
    Deluno and Weir send, not ones this repository wrote:
      * in front of Weir:   -ListenPort 19347 -Target http://127.0.0.1:<Weir port>   (point Deluno's processor connection at port 19347)
      * in front of Deluno: -ListenPort 17879 -Target http://127.0.0.1:7879          (point Weir's Deluno callback at port 17879)
    Each exchange is appended as one line of JSON to <OutFile>. Invoke-RecordReplies.ps1 -DelunoSide turns those lines
    into tests/fixtures/recorded/deluno/<version>/<state>.json. Stop it with Ctrl+C. Binds 127.0.0.1 only.

.PARAMETER ListenPort  Where the proxy listens, on 127.0.0.1.
.PARAMETER Target      The real service it forwards to.
.PARAMETER Side        A label written on every line: weir-bound (Deluno's calls to Weir) or deluno-bound (Weir's calls to Deluno).
.PARAMETER OutFile     The JSON-lines file. Defaults to C:\golden\recorded\proxy\<side>.jsonl.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [int] $ListenPort,
    [Parameter(Mandatory)] [string] $Target,
    [ValidateSet('weir-bound', 'deluno-bound')] [string] $Side = 'weir-bound',
    [string] $OutFile
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
if (-not $OutFile) { $OutFile = "C:\golden\recorded\proxy\$Side.jsonl" }
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$ListenPort/")
$listener.Start()
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromMinutes(5)
Write-Host "Recording $Side on 127.0.0.1:$ListenPort -> $Target, writing $OutFile"
$keep = @('content-type', 'x-webhook-secret')
try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $req = $context.Request
        $reader = New-Object IO.StreamReader($req.InputStream, [Text.Encoding]::UTF8)
        $body = $reader.ReadToEnd()
        $forward = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($req.HttpMethod), ($Target.TrimEnd('/') + $req.Url.PathAndQuery))
        if ($body.Length -gt 0) { $forward.Content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, $(if ($req.ContentType) { ($req.ContentType -split ';')[0] } else { 'application/json' })) }
        foreach ($name in $req.Headers.AllKeys) { if ($name -match '^(x-|authorization)') { $null = $forward.Headers.TryAddWithoutValidation($name, $req.Headers[$name]) } }
        $reqHeaders = [ordered]@{}
        foreach ($name in $req.Headers.AllKeys) { if ($keep -contains $name.ToLowerInvariant()) { $reqHeaders[$name.ToLowerInvariant()] = $req.Headers[$name] } }
        try {
            $resp = $http.SendAsync($forward).GetAwaiter().GetResult()
            $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $status = [int]$resp.StatusCode
            $ctype = if ($resp.Content.Headers.ContentType) { $resp.Content.Headers.ContentType.ToString() } else { 'application/json' }
        } catch { $status = 502; $text = ''; $ctype = 'text/plain' }
        $bytes = [Text.Encoding]::UTF8.GetBytes($text)
        $context.Response.StatusCode = $status
        $context.Response.ContentType = $ctype
        $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $context.Response.Close()
        $line = [ordered]@{
            side = $Side; at = (Get-Date).ToUniversalTime().ToString('o')
            request = [ordered]@{ method = $req.HttpMethod; path = $req.Url.PathAndQuery; headers = $reqHeaders; bodyText = $body }
            reply = [ordered]@{ status = $status; headers = [ordered]@{ 'content-type' = $ctype }; bodyText = $text }
        } | ConvertTo-Json -Depth 8 -Compress
        Add-Content -LiteralPath $OutFile -Value $line -Encoding UTF8
    }
} finally { $listener.Stop() }
