# DayDeck 설치 프로그램(setup.exe)을 만든다 (Windows, Inno Setup 6 필요. 없으면 Chocolatey로 설치한다).
#
#   ./scripts/build-installer.ps1 -Exe artifacts/publish/win-x64/DayDeck.exe
#   → artifacts/installer/DayDeck-<버전>-setup.exe
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $OutDir = 'artifacts/installer'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Find-Iscc {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host 'Inno Setup이 없어 설치합니다 (choco install innosetup).'
    choco install innosetup -y --no-progress | Out-Host
    $iscc = Find-Iscc
    if (-not $iscc) { throw 'Inno Setup 6(ISCC.exe)을 찾지 못했습니다. https://jrsoftware.org/isdl.php 에서 설치하세요.' }
}

$version = ([xml](Get-Content 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$exePath = (Resolve-Path $Exe).Path
New-Item -ItemType Directory $OutDir -Force | Out-Null
$outPath = (Resolve-Path $OutDir).Path

& $iscc "/DAppVersion=$version" "/DSourceExe=$exePath" "/DOutputDir=$outPath" 'installer/DayDeck.iss'
if ($LASTEXITCODE -ne 0) { throw "ISCC 실패 (exit code $LASTEXITCODE)" }

Get-ChildItem $OutDir | ForEach-Object { Write-Host ("{0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
