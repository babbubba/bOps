# Copyright 2026 Fabio Cavallari
# SPDX-License-Identifier: Apache-2.0

<#
.SYNOPSIS
  Verifies a published bOps artifact: structure, and (unless -StructureOnly) a smoke test of the artifact actually running. F-26 (BOPS-0120).

.DESCRIPTION
  -UiDirectory <dir> -StructureOnly   checks an Angular build output (index.html, every file it references, every lazy chunk it imports).
  -ArtifactDirectory <dir> -StructureOnly   additionally checks the publish layout (wwwroot-ui, appsettings, no state or secret files).
  -ArtifactDirectory <dir>   also starts the published application in a scratch data directory, WITHOUT Node.js on its PATH, and
                             drives it over HTTP: SPA shell, deep link, assets and cache headers, API 401/404, reserved files,
                             login / cookie / CSRF / foreign origin / logout. The artifact must be for the OS this runs on.

  The API key and vault key are generated per run, handed to the process through its environment and never printed.
#>
[CmdletBinding()]
param(
    [string]$ArtifactDirectory,
    [string]$UiDirectory,
    [switch]$StructureOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$failures = [System.Collections.Generic.List[string]]::new()
function Assert-That([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "  ok    $Message" } else { Write-Host "  FAIL  $Message"; $failures.Add($Message) }
}

function Test-UiBuild([string]$Dir) {
    Write-Host "== UI build: $Dir"
    $indexPath = Join-Path $Dir 'index.html'
    if (-not (Test-Path -LiteralPath $indexPath)) { Assert-That $false "index.html exists in '$Dir' (run npm run build in web/bops-ui)"; return }
    $index = Get-Content -Raw -LiteralPath $indexPath
    Assert-That ($index -match '<app-root') 'index.html hosts <app-root>'
    Assert-That ($index -notmatch '\son\w+\s*=') 'index.html has no inline event handler (CSP script-src self)'
    Assert-That ($index -notmatch '<script(?![^>]*\ssrc=)') 'index.html has no inline script'
    foreach ($pattern in 'main-*.js', 'polyfills-*.js', 'styles-*.css') {
        Assert-That ($null -ne (Get-ChildItem -LiteralPath $Dir -Filter $pattern -File | Select-Object -First 1)) "a $pattern bundle exists"
    }

    $references = [regex]::Matches($index, '(?:src|href)="([^"]+)"') | ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $_ -and $_ -notmatch '^(?:[a-z][a-z0-9+.-]*:|//|#)' -and $_ -ne '/' }
    foreach ($reference in $references) {
        $relative = $reference.TrimStart('/').Split('?')[0]
        Assert-That (Test-Path -LiteralPath (Join-Path $Dir $relative) -PathType Leaf) "index.html reference '$reference' exists"
    }

    # Lazy routes are separate chunks, imported by file name from the bundles; a missing one breaks a screen at run time.
    $missing = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($script in Get-ChildItem -LiteralPath $Dir -Filter '*.js' -File) {
        foreach ($match in [regex]::Matches((Get-Content -Raw -LiteralPath $script.FullName), '["''`]\./(chunk-[A-Za-z0-9]+\.js)["''`]')) {
            if (-not (Test-Path -LiteralPath (Join-Path $Dir $match.Groups[1].Value) -PathType Leaf)) { [void]$missing.Add($match.Groups[1].Value) }
        }
    }
    Assert-That ($missing.Count -eq 0) "every imported chunk exists$(if ($missing.Count) { ' (missing: ' + ($missing -join ', ') + ')' })"
}

function Test-ArtifactLayout([string]$Dir) {
    Write-Host "== Artifact layout: $Dir"
    $onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
    Assert-That (Test-Path -LiteralPath (Join-Path $Dir 'bOps.Api.dll')) 'bOps.Api.dll'
    Assert-That (Test-Path -LiteralPath (Join-Path $Dir 'appsettings.json')) 'appsettings.json'
    Assert-That (Test-Path -LiteralPath (Join-Path $Dir 'appsettings.Production.json')) 'appsettings.Production.json'
    Assert-That (Test-Path -LiteralPath (Join-Path $Dir 'bOps.Api.runtimeconfig.json')) 'bOps.Api.runtimeconfig.json'
    Test-UiBuild (Join-Path $Dir 'wwwroot-ui')

    $state = Get-ChildItem -LiteralPath $Dir -Recurse -File | Where-Object {
        $_.Name -match '\.(db|db-shm|db-wal)$' -or $_.Name -match '^(audit.*\.jsonl|vault\.dat|policy\.ya?ml|settings\.json|\.env|secrets\.json|appsettings\.(Development|Local)\.json)$' }
    Assert-That (@($state).Count -eq 0) "no state, secret or development file is shipped$(if (@($state).Count) { ' (found: ' + (($state | ForEach-Object Name) -join ', ') + ')' })"
    $node = Get-ChildItem -LiteralPath $Dir -Recurse -Directory -Filter 'node_modules' -ErrorAction SilentlyContinue
    Assert-That (@($node).Count -eq 0) 'no node_modules in the artifact'
    if (-not $onWindows) { Assert-That ($null -ne (Get-ChildItem -LiteralPath $Dir -Filter 'bOps.Api' -File)) 'bOps.Api apphost (self-contained or apphost publish)' }
}

function Invoke-Smoke([string]$Dir) {
    $onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $origin = "http://localhost:$port"
    $data = Join-Path ([System.IO.Path]::GetTempPath()) ("bops-smoke-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $data | Out-Null
    $apiKey = 'k-' + [guid]::NewGuid().ToString('N')
    $vaultKey = 'v-' + [guid]::NewGuid().ToString('N')

    # Node.js must not be needed: take every directory that holds node or npm off the PATH the application gets.
    $separator = [System.IO.Path]::PathSeparator
    $kept = @(); $removed = 0
    foreach ($entry in ($env:PATH -split [regex]::Escape($separator) | Where-Object { $_ })) {
        $hasNode = $false
        foreach ($name in 'node', 'node.exe', 'npm', 'npm.cmd', 'npx', 'npx.cmd') {
            if ($entry -and (Test-Path -LiteralPath (Join-Path $entry $name) -PathType Leaf -ErrorAction SilentlyContinue)) { $hasNode = $true }
        }
        if ($hasNode) { $removed++ } else { $kept += $entry }
    }
    $cleanPath = $kept -join $separator
    $nodeLeft = foreach ($entry in $kept) { foreach ($name in 'node', 'node.exe') { if (Test-Path -LiteralPath (Join-Path $entry $name) -PathType Leaf -ErrorAction SilentlyContinue) { $entry } } }
    Assert-That (-not $nodeLeft) "Node.js is not on the PATH of the application ($removed PATH entries removed)"

    $overrides = @{
        PATH                                  = $cleanPath
        ASPNETCORE_CONTENTROOT                = $Dir
        ASPNETCORE_ENVIRONMENT                = $null
        DOTNET_ENVIRONMENT                    = $null
        ASPNETCORE_URLS                       = $null
        Kestrel__Endpoints__Http__Url         = $origin
        BrowserSession__Origins               = $origin
        BOPS_API_KEY                          = $apiKey
        BOPS_VAULT_MASTER_KEY                 = $vaultKey
        DOTNET_CLI_TELEMETRY_OPTOUT           = '1'
    }
    $saved = @{}
    foreach ($key in $overrides.Keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key) }

    $exe = Join-Path $Dir ($(if ($onWindows) { 'bOps.Api.exe' } else { 'bOps.Api' }))
    $stdout = Join-Path $data 'stdout.log'; $stderr = Join-Path $data 'stderr.log'
    $process = $null
    $client = $null
    try {
        foreach ($key in $overrides.Keys) { [Environment]::SetEnvironmentVariable($key, $overrides[$key]) }
        if (Test-Path -LiteralPath $exe) {
            $process = Start-Process -FilePath $exe -WorkingDirectory $data -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        } else {
            $process = Start-Process -FilePath 'dotnet' -ArgumentList (Join-Path $Dir 'bOps.Api.dll') -WorkingDirectory $data -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        }
    } finally {
        foreach ($key in $overrides.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseCookies = $false; $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(20)

    function Send([string]$Method, [string]$Path, [hashtable]$Headers = @{}, [string]$Body = $null) {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), "$origin$Path")
        foreach ($name in $Headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($name, [string]$Headers[$name]) }
        if ($null -ne $Body) { $request.Content = [System.Net.Http.StringContent]::new($Body, [System.Text.Encoding]::UTF8, 'application/json') }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $headerMap = @{}
        foreach ($h in $response.Headers) { $headerMap[$h.Key.ToLowerInvariant()] = ($h.Value -join '; ') }
        foreach ($h in $response.Content.Headers) { $headerMap[$h.Key.ToLowerInvariant()] = ($h.Value -join '; ') }
        [pscustomobject]@{ Status = [int]$response.StatusCode; Headers = $headerMap; Body = $text }
    }

    try {
        Write-Host "== Smoke test of the running artifact ($origin, data directory $data)"
        $ready = $false
        for ($i = 0; $i -lt 120 -and -not $ready; $i++) {
            if ($process.HasExited) { break }
            try { $ready = (Send 'GET' '/healthz').Status -eq 200 } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $ready) {
            $tail = ((Get-Content -LiteralPath $stdout, $stderr -Tail 30 -ErrorAction SilentlyContinue) -join "`n") -replace [regex]::Escape($apiKey), '***' -replace [regex]::Escape($vaultKey), '***'
            Assert-That $false "the published application became ready (exited: $($process.HasExited))`n$tail"
            return
        }
        Assert-That $true 'the published application started and answers /healthz'

        $shell = Send 'GET' '/' @{ Accept = 'text/html' }
        Assert-That ($shell.Status -eq 200 -and $shell.Body -match '<app-root') '/ serves the SPA shell'
        Assert-That ($shell.Headers['cache-control'] -eq 'no-cache') '/ is revalidated (Cache-Control: no-cache)'
        Assert-That ($shell.Headers['content-security-policy'] -match "script-src 'self'") '/ carries the Content-Security-Policy'
        $deep = Send 'GET' '/approvals' @{ Accept = 'text/html' }
        Assert-That ($deep.Status -eq 200 -and $deep.Body -eq $shell.Body) 'a deep link returns the same shell'
        $deep2 = Send 'GET' '/delegations/abc/def' @{ Accept = 'text/html' }
        Assert-That ($deep2.Status -eq 200 -and $deep2.Body -match '<app-root') 'a nested deep link returns the shell'

        $assets = [regex]::Matches($shell.Body, '(?:src|href)="([^"]+\.(?:js|css|png|ico))"') | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -notmatch '^(?:[a-z]+:|//)' }
        Assert-That (@($assets).Count -ge 3) "the shell references assets ($(@($assets).Count))"
        foreach ($asset in $assets) {
            $served = Send 'GET' "/$($asset.TrimStart('/'))"
            $hashed = $asset -match '-[A-Z0-9]{8}\.(js|css)$'
            Assert-That ($served.Status -eq 200 -and $served.Body.Length -gt 0 -and ($hashed -eq ($served.Headers['cache-control'] -match 'immutable'))) "asset /$asset is served ($(if ($hashed) { 'immutable' } else { 'revalidated' }))"
        }

        Assert-That ((Send 'GET' '/missing-asset.js').Status -eq 404) 'a missing asset is 404, not the shell'
        $unknownApi = Send 'GET' '/api/does-not-exist' @{ Accept = 'text/html' }
        Assert-That ($unknownApi.Status -eq 404 -and $unknownApi.Body -notmatch '<app-root') 'an unknown /api path is 404, not the shell'
        Assert-That ((Send 'GET' '/api/session/me').Status -eq 401) 'a protected API route still requires authorization'
        $health = Send 'GET' '/healthz' @{ Accept = 'text/html' }
        Assert-That ($health.Status -eq 200 -and $health.Body -eq 'ok') '/healthz is not intercepted by the fallback'

        foreach ($reserved in '/appsettings.json', '/appsettings.Production.json', '/bOps.Api.dll', '/bOps.Api.runtimeconfig.json', '/sessions.db', '/audit.jsonl', '/vault.dat', '/policy.yaml', '/settings.json', '/tasks.db', '/../appsettings.json', '/%2e%2e/appsettings.json') {
            $r = Send 'GET' $reserved @{ Accept = 'text/html' }
            Assert-That ($r.Status -ne 200 -and $r.Body -notmatch 'ApiKeys|BOPS_VAULT|BrowserSession') "reserved file $reserved is not served ($($r.Status))"
        }

        $loginBody = '{"apiKey":"' + $apiKey + '"}'
        $trusted = @{ 'Origin' = $origin; 'X-bOps-Request' = '1' }
        $foreign = Send 'POST' '/api/session' @{ 'Origin' = 'https://evil.example.com'; 'X-bOps-Request' = '1' } $loginBody
        Assert-That ($foreign.Status -eq 403) 'login from an unauthorized origin is refused (403)'
        $noHeader = Send 'POST' '/api/session' @{ 'Origin' = $origin } $loginBody
        Assert-That ($noHeader.Status -eq 403) 'login without X-bOps-Request is refused (403)'
        $login = Send 'POST' '/api/session' $trusted $loginBody
        Assert-That ($login.Status -eq 200) 'login from the configured origin succeeds'
        $setCookie = [string]$login.Headers['set-cookie']
        Assert-That ($setCookie -match '^__Host-bops_session=[A-Za-z0-9_-]{43}' -and $setCookie -match '(?i)secure' -and $setCookie -match '(?i)httponly' -and $setCookie -match '(?i)samesite=strict' -and $setCookie -match '(?i)path=/' -and $setCookie -notmatch '(?i)domain') 'the session cookie is __Host-, Secure, HttpOnly, SameSite=Strict, Path=/'
        Assert-That ($login.Body -notmatch [regex]::Escape($apiKey)) 'the login response does not echo the API key'
        $cookie = ($setCookie -split ';')[0]
        Assert-That ((Send 'GET' '/api/session/me' @{ Cookie = $cookie }).Status -eq 200) 'the session cookie authorizes /api/session/me'
        Assert-That ((Send 'DELETE' '/api/session' @{ Cookie = $cookie; 'Origin' = 'https://evil.example.com'; 'X-bOps-Request' = '1' }).Status -eq 403) 'logout from a foreign origin is refused'
        Assert-That ((Send 'DELETE' '/api/session' @{ Cookie = $cookie; 'Origin' = $origin }).Status -eq 403) 'logout without X-bOps-Request is refused'
        $logout = Send 'DELETE' '/api/session' (@{ Cookie = $cookie } + $trusted)
        Assert-That ($logout.Status -ge 200 -and $logout.Status -lt 300) 'logout succeeds'
        Assert-That ((Send 'GET' '/api/session/me' @{ Cookie = $cookie }).Status -eq 401) 'the session is gone after logout'

        Assert-That (Test-Path -LiteralPath (Join-Path $data 'sessions.db')) 'state is written to the working (data) directory'
        Assert-That (-not (Test-Path -LiteralPath (Join-Path $Dir 'sessions.db'))) 'nothing is written into the installation directory'
        Assert-That (-not $process.HasExited) 'the process is still running after the whole sequence'
    } finally {
        if ($client) { $client.Dispose() }
        if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; [void]$process.WaitForExit(15000) }
        Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not $ArtifactDirectory -and -not $UiDirectory) { throw 'Pass -ArtifactDirectory or -UiDirectory.' }
if ($UiDirectory) { Test-UiBuild ([System.IO.Path]::GetFullPath($UiDirectory)) }
if ($ArtifactDirectory) {
    $artifact = [System.IO.Path]::GetFullPath($ArtifactDirectory)
    Test-ArtifactLayout $artifact
    if (-not $StructureOnly -and $failures.Count -eq 0) { Invoke-Smoke $artifact }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "$($failures.Count) check(s) failed:"
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host 'All checks passed.'
