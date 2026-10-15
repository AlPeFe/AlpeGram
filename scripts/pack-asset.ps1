# Canonical HanniGram release asset builder.
#
# Produces `hannigram-windows-x64.zip`: the self-contained win-x64 daemon + CLI +
# MCP server (no .NET runtime needed on the target machine) plus the
# `pi-extension/` that registers the mem_* tools, plus VERSION.txt with the
# source revision.
#
# The same asset is consumed twice:
#   - by HanniGram's own GitHub Release (tag `v*`), which is the source of truth
#     for the binary, and
#   - by HanniPI's release, which ships a pinned copy so a single download
#     installs harness + memory atomically.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/pack-asset.ps1 -Version v0.1.0
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/pack-asset.ps1 -Version local
#
# Output (default `artifacts/`): hannigram-windows-x64.zip + SHA256SUMS + VERSION.txt
param(
    [string]$Version,
    [string]$OutDir = (Join-Path $PSScriptRoot "..\artifacts"),
    [string]$Runtime = "win-x64",
    # HanniGram only needs nuget.org; pinning the source keeps the build working
    # on machines that have other feeds configured (broken certs, private feeds).
    [string]$NuGetSource = "https://api.nuget.org/v3/index.json",
    [switch]$SkipSmokeTest,
    [switch]$KeepWork
)

$ErrorActionPreference = "Stop"
$rootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Write-Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "  $text" -ForegroundColor Green }
function Write-Warn($text) { Write-Host "  $text" -ForegroundColor Yellow }

function Get-Sha256($path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($path)
        try { return (($sha.ComputeHash($stream) | ForEach-Object { $_.ToString("x2") }) -join "") }
        finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
}

function Invoke-Dotnet($arguments) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet fallo (exit $LASTEXITCODE): dotnet $($arguments -join ' ')" }
}

# ---------------------------------------------------------------------------
# 0. Version + source revision
# ---------------------------------------------------------------------------
$commit = (& git -C $rootDir rev-parse HEAD).Trim()
$commitShort = $commit.Substring(0, 7)
if (-not $Version) { $Version = "local-$commitShort" }
$isTag = $Version -match "^v\d"
$isWindowsHost = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT

Write-Step "HanniGram $Version ($commitShort)"
if (-not $isTag) { Write-Warn "version sin tag vX.Y.Z; se publica como $Version" }

# ---------------------------------------------------------------------------
# 1. Publish self-contained single-file win-x64 (Server = daemon, Cli = hannigram)
# ---------------------------------------------------------------------------
$workRoot = Join-Path $env:TEMP ("hannigram-asset-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$publishRoot = Join-Path $workRoot "publish"
$stageDir = Join-Path $workRoot "stage"
New-Item -ItemType Directory -Path $publishRoot, $stageDir -Force | Out-Null

$publishArgs = @(
    "publish",
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=none",
    "--source", $NuGetSource,
    "--nologo"
)

$serverOut = Join-Path $publishRoot "server"
$cliOut = Join-Path $publishRoot "cli"
$mcpOut = Join-Path $publishRoot "mcp"
Write-Step "Publicando HanniGram.Server ($Runtime)"
Invoke-Dotnet ($publishArgs + @((Join-Path $rootDir "src\HanniGram.Server\HanniGram.Server.csproj"), "-o", $serverOut))
Write-Step "Publicando HanniGram.Cli ($Runtime)"
Invoke-Dotnet ($publishArgs + @((Join-Path $rootDir "src\HanniGram.Cli\HanniGram.Cli.csproj"), "-o", $cliOut))
Write-Step "Publicando HanniGram.Mcp ($Runtime)"
Invoke-Dotnet ($publishArgs + @((Join-Path $rootDir "src\HanniGram.Mcp\HanniGram.Mcp.csproj"), "-o", $mcpOut))

$serverExe = Join-Path $serverOut "HanniGram.Server.exe"
$cliExe = Join-Path $cliOut "HanniGram.Cli.exe"
$mcpExe = Join-Path $mcpOut "HanniGram.Mcp.exe"
$appSettings = Join-Path $serverOut "appsettings.json"
foreach ($file in @($serverExe, $cliExe, $mcpExe, $appSettings)) {
    if (-not (Test-Path $file)) { throw "La publicacion no produjo $file" }
}

# ---------------------------------------------------------------------------
# 2. Staging: daemon + CLI + MCP server + pi-extension + docs
# ---------------------------------------------------------------------------
Write-Step "Preparando el paquete"
Copy-Item $serverExe (Join-Path $stageDir "HanniGram.Server.exe")
Copy-Item $cliExe (Join-Path $stageDir "hannigram.exe")
Copy-Item $mcpExe (Join-Path $stageDir "hannigram-mcp.exe")
Copy-Item $appSettings (Join-Path $stageDir "appsettings.json")
Copy-Item (Join-Path $rootDir "README.md") (Join-Path $stageDir "README-HanniGram.md")

# La extension que da las tools mem_* al harness viaja dentro del asset: instalar
# HanniGram incluye su memoria y su interfaz.
$extensionSource = Join-Path $rootDir "pi-extension"
$extensionStage = Join-Path $stageDir "pi-extension"
New-Item -ItemType Directory -Path $extensionStage -Force | Out-Null
foreach ($name in @("index.ts", "package.json", "README.md")) {
    $source = Join-Path $extensionSource $name
    if (Test-Path $source) { Copy-Item $source (Join-Path $extensionStage $name) }
}

$leeme = @(
    "HanniGram: memoria persistente por proyecto para HanniPI.",
    "",
    "- HanniGram.Server.exe  -> daemon HTTP (127.0.0.1:8765). Lo arranca HanniPI solo.",
    "- hannigram.exe         -> CLI (init/save/search/list/topics/...).",
    "- hannigram-mcp.exe     -> servidor MCP por stdio (opcional, no se registra solo).",
    "- pi-extension/         -> extension del harness (tools mem_*).",
    "",
    "BD en %USERPROFILE%\.hannigram\hannigram.db",
    "Version: $Version ($commitShort)"
) -join "`r`n"
Set-Content -Path (Join-Path $stageDir "LEEME.txt") -Value ($leeme + "`r`n") -Encoding ascii -NoNewline

$versionInfo = @(
    "asset=hannigram-windows-x64.zip",
    "version=$Version",
    "commit=$commit",
    "builtAt=$((Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))",
    "runtime=$Runtime"
) -join "`r`n"
Set-Content -Path (Join-Path $stageDir "VERSION.txt") -Value ($versionInfo + "`r`n") -Encoding ascii -NoNewline

# ---------------------------------------------------------------------------
# 3. Smoke test: the packaged daemon must answer /health on its own
# ---------------------------------------------------------------------------
if ($SkipSmokeTest) {
    Write-Warn "smoke test omitido (-SkipSmokeTest)"
} elseif (-not $isWindowsHost) {
    Write-Warn "smoke test omitido: el binario $Runtime solo se ejecuta en Windows"
} else {
    Write-Step "Smoke test del daemon empaquetado"
    $smokeUrl = "http://127.0.0.1:8799"
    # El daemon lee HANNIGRAM_URLS (no --urls); asi un daemon ya en marcha en el
    # puerto por defecto no puede hacer pasar la prueba por accidente.
    $previousUrls = $env:HANNIGRAM_URLS
    $env:HANNIGRAM_URLS = $smokeUrl
    $process = Start-Process -FilePath (Join-Path $stageDir "HanniGram.Server.exe") -PassThru -WindowStyle Hidden
    try {
        $deadline = (Get-Date).AddSeconds(60)
        $healthy = $null
        while ((Get-Date) -lt $deadline) {
            try {
                $healthy = Invoke-RestMethod -Uri "$smokeUrl/health" -TimeoutSec 3
                if ($healthy.status -eq "ok") { break }
                $healthy = $null
            } catch { }
            Start-Sleep -Milliseconds 500
        }
        if (-not $healthy) { throw "El daemon empaquetado no respondio /health en $smokeUrl" }
        Write-Ok "daemon OK en $smokeUrl (db: $($healthy.db))"
    } finally {
        $env:HANNIGRAM_URLS = $previousUrls
        if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------------------
# 3b. Smoke test: the packaged MCP server must complete initialize over stdio
# ---------------------------------------------------------------------------
if ($SkipSmokeTest) {
    Write-Warn "smoke test MCP omitido (-SkipSmokeTest)"
} elseif (-not $isWindowsHost) {
    Write-Warn "smoke test MCP omitido: el binario $Runtime solo se ejecuta en Windows"
} else {
    Write-Step "Smoke test del servidor MCP empaquetado"
    # El servidor abre su propia BD: se apunta a un archivo temporal para no tocar
    # la BD real del usuario ni la que deje el smoke test del daemon.
    $mcpSmokeDb = Join-Path $workRoot "mcp-smoke\hannigram.db"
    New-Item -ItemType Directory -Path (Split-Path $mcpSmokeDb) -Force | Out-Null
    $mcpStderrFile = Join-Path $workRoot "mcp-smoke-stderr.txt"
    $previousMcpDb = $env:HANNIGRAM_DB
    $env:HANNIGRAM_DB = $mcpSmokeDb
    try {
        $initLine = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}'
        $listLine = '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
        # stdout es el canal de protocolo; stderr va a un archivo aparte y no se lee
        # como respuesta.
        $mcpStdout = @($initLine, $listLine) | & (Join-Path $stageDir "hannigram-mcp.exe") 2>$mcpStderrFile
    } finally {
        $env:HANNIGRAM_DB = $previousMcpDb
    }
    # Cada respuesta tiene que ser una linea JSON-RPC; la ausencia de framing
    # correcto (o un throw) hace fallar el asset.
    if (-not ($mcpStdout -match '"jsonrpc"')) {
        throw "El servidor MCP empaquetado no respondio initialize (stdout: $($mcpStdout -join ' / '))"
    }
    Write-Ok "MCP OK (initialize + tools/list respondidos por stdio)"
}

# ---------------------------------------------------------------------------
# 4. Zip + checksums
# ---------------------------------------------------------------------------
Write-Step "Empaquetando hannigram-windows-x64.zip"
$assetName = "hannigram-windows-x64.zip"
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$zip = Join-Path $OutDir $assetName
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$sha = Get-Sha256 $zip
Set-Content -Path (Join-Path $OutDir "SHA256SUMS") -Value "$sha *$assetName" -Encoding ascii
Set-Content -Path (Join-Path $OutDir "VERSION.txt") -Value ($versionInfo + "`r`n") -Encoding ascii -NoNewline

Write-Ok "$zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Write-Ok "sha256 $sha"
Get-ChildItem $stageDir | ForEach-Object { Write-Host "    $($_.Name)" }

if (-not $KeepWork) { Remove-Item $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
Write-Step "Listo"
Write-Ok "asset: $zip"
