<#
.SYNOPSIS
  Builds GrannyCoop.dll and the one-click installer GrannyCoop-Setup.exe.

.DESCRIPTION
  The installer carries everything a player needs: MelonLoader 0.7.3, the .NET 6
  runtime installer and this mod. Two files are too big to keep in git, so the first
  run downloads them from the two pinned URLs below and checks their SHA-256. Any
  other failure (wrong hash, wrong size) stops the build.

  The mod is compiled against the Il2Cpp assemblies of a test install, because the
  MelonLoader interfaces are game-specific. Point -GrannyTest at any copy of the game
  that has MelonLoader already installed.

.EXAMPLE
  .\tools\build-setup.ps1 -GrannyTest C:\Users\Public\granny-test

.EXAMPLE
  $env:GRANNY_TEST = 'C:\Users\Public\granny-test'; .\tools\build-setup.ps1 -SkipDownload
#>
[CmdletBinding()]
param(
    [string]$GrannyTest = $env:GRANNY_TEST,
    [string]$Configuration = 'Release',
    # payloads were verified once already; reuse whatever is on disk
    [switch]$SkipDownload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo    = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $PSScriptRoot 'setup\payload'
$dist    = Join-Path $repo 'dist'

# Downloads are pinned: URL, byte size and SHA-256 are fixed here, nothing is taken
# on trust from the server.
$pinned = @(
    @{
        Name = 'MelonLoader.x64.zip'
        Url  = 'https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip'
        Size = 20155622
        Sha  = '5b2b2f3d1cd42b59ec886c5bdc2663edae87a0097a4f4a8f58c0965a99dda416'
    },
    @{
        Name = 'dotnet-runtime-6.0.36-win-x64.exe'
        Url  = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/6.0.36/dotnet-runtime-6.0.36-win-x64.exe'
        Size = 28101408
        Sha  = '6bdad7bc4c41fe93d4ae7b0312b1d017cfe369d28e7e2e421f5b675f9feefe84'
    }
)

function Get-Payload {
    param($Item)

    $path = Join-Path $payload $Item.Name
    $needDownload = $true

    if (Test-Path $path) {
        $file = Get-Item $path
        if ($file.Length -eq $Item.Size) {
            $hash = (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
            if ($hash -eq $Item.Sha) {
                Write-Host ("  {0}: на месте, SHA-256 ok" -f $Item.Name) -ForegroundColor DarkGray
                $needDownload = $false
            }
            else {
                Write-Host ("  {0}: хеш не совпал, скачиваю заново" -f $Item.Name) -ForegroundColor Yellow
                Remove-Item -LiteralPath $path -Force
            }
        }
        else {
            Write-Host ("  {0}: неполный файл, скачиваю заново" -f $Item.Name) -ForegroundColor Yellow
            Remove-Item -LiteralPath $path -Force
        }
    }

    if (-not $needDownload) { return $path }
    if ($SkipDownload) { throw "$($Item.Name) отсутствует, а указан -SkipDownload." }

    Write-Host ("  {0}: скачиваю {1}" -f $Item.Name, $Item.Url)
    $tmp = "$path.part"
    Invoke-WebRequest -Uri $Item.Url -OutFile $tmp -UseBasicParsing -MaximumRedirection 5
    $got = (Get-Item $tmp).Length
    if ($got -ne $Item.Size) {
        Remove-Item -LiteralPath $tmp -Force
        throw "$($Item.Name): ожидалось $($Item.Size) байт, получено $got."
    }
    $hash = (Get-FileHash -Algorithm SHA256 -Path $tmp).Hash.ToLowerInvariant()
    if ($hash -ne $Item.Sha) {
        Remove-Item -LiteralPath $tmp -Force
        throw "$($Item.Name): SHA-256 $hash не совпадает с закреплённым $($Item.Sha)."
    }
    Move-Item -LiteralPath $tmp -Destination $path -Force
    Write-Host ("  {0}: скачан и проверен" -f $Item.Name) -ForegroundColor Green
    return $path
}

Write-Host '1/3 payload' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $payload | Out-Null
foreach ($item in $pinned) { Get-Payload $item | Out-Null }

Write-Host '2/3 мод' -ForegroundColor Cyan
if ([string]::IsNullOrWhiteSpace($GrannyTest)) {
    throw 'Укажите тестовую установку: -GrannyTest <папка с Granny.exe и MelonLoader> или переменную среды GRANNY_TEST.'
}
if (-not (Test-Path (Join-Path $GrannyTest 'MelonLoader\net6\MelonLoader.dll'))) {
    throw "$GrannyTest не похожа на установку с MelonLoader (нет MelonLoader\net6\MelonLoader.dll)."
}
$env:GRANNY_TEST = $GrannyTest
& dotnet build (Join-Path $repo 'GrannyCoop.csproj') -c $Configuration -v m
if ($LASTEXITCODE -ne 0) { throw 'Сборка мода не удалась.' }

$modDll = Join-Path $repo (Join-Path 'bin' (Join-Path $Configuration 'GrannyCoop.dll'))
if (-not (Test-Path $modDll)) { throw "Мод не собрался: нет $modDll" }
$modVer = [Reflection.AssemblyName]::GetAssemblyName($modDll).Version
Write-Host ("  мод {0}, {1:N0} байт" -f $modVer, (Get-Item $modDll).Length) -ForegroundColor Green

Write-Host '3/3 установщик' -ForegroundColor Cyan
& dotnet build (Join-Path $PSScriptRoot 'setup\GrannyCoop.Setup.csproj') -c $Configuration -v m
if ($LASTEXITCODE -ne 0) { throw 'Сборка установщика не удалась.' }

$built = Join-Path $PSScriptRoot (Join-Path 'setup\bin' (Join-Path $Configuration 'GrannyCoop-Setup.exe'))
if (-not (Test-Path $built)) { throw "Установщик не собрался: нет $built" }

# one file, version in the name, so two players can compare what they run
$setupVer = (Get-Item $built).VersionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($setupVer)) { $setupVer = '1.0.0' }
# FileVersion is four parts (1.0.0.0); drop the revision, keep 1.0.0
if ($setupVer -match '^(.+)\.0$') { $setupVer = $Matches[1] }
if ([string]::IsNullOrWhiteSpace($setupVer)) { $setupVer = '1.0.0' }
$name = "GrannyCoop-Setup-$setupVer.exe"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$out = Join-Path $dist $name
Copy-Item -LiteralPath $built -Destination $out -Force

$size = (Get-Item $out).Length
$sha  = (Get-FileHash -Algorithm SHA256 -Path $out).Hash.ToLowerInvariant()
Write-Host ''
Write-Host 'Готово.' -ForegroundColor Green
Write-Host ("  {0}" -f $out)
Write-Host ("  {0:N0} байт ({1:N1} МБ)" -f $size, ($size / 1MB))
Write-Host ("  SHA-256 {0}" -f $sha)
