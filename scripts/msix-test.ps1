# 테스트 서명한 MSIX를 설치해 Store 판처럼 실행되는지 확인한다 (CI용, 관리자 권한 필요: 인증서를 컴퓨터 저장소에 넣는다).
#  - 패키지로 설치·실행되는지, 데이터가 패키지의 LocalState에 만들어지는지 (packaged=True)
#  - 로그인 자동 실행이 StartupTask로 켜지는지 (레지스트리 Run 키가 아님)
param(
    [Parameter(Mandatory)] [string] $Msix,
    [Parameter(Mandatory)] [string] $Cer
)

$ErrorActionPreference = 'Stop'
$identity = Get-Content (Join-Path $PSScriptRoot '../packaging/msix/identity.json') -Raw -Encoding utf8 | ConvertFrom-Json

$cert = Import-Certificate -FilePath $Cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
$package = $null
try {
    Add-AppxPackage -Path $Msix
    $package = Get-AppxPackage -Name $identity.identityName
    if (-not $package) { throw "설치된 패키지 $($identity.identityName) 를 찾지 못함" }
    Write-Host "설치됨: $($package.PackageFullName)"

    $localState = Join-Path $env:LOCALAPPDATA "Packages/$($package.PackageFamilyName)/LocalState"
    $logPath = Join-Path $localState 'Logs/app.log'

    Start-Process "shell:AppsFolder\$($package.PackageFamilyName)!App"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 90 -and
           -not ((Test-Path $logPath) -and ((Get-Content $logPath -Raw -Encoding utf8) -match '시작 완료'))) {
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 3
    if (-not (Test-Path $logPath)) { throw "LocalState에 로그가 없음: $logPath" }
    $log = Get-Content $logPath -Raw -Encoding utf8
    Write-Host '--- app.log ---'
    Write-Host $log

    if ($log -notmatch '시작 완료') { throw '시작 완료 기록이 없음' }
    if ($log -notmatch 'packaged=True') { throw '패키지 판으로 인식되지 않음 (packaged=True 없음)' }
    foreach ($f in 'data.db', 'settings.json', 'Notes') {
        if (-not (Test-Path (Join-Path $localState $f))) { throw "LocalState에 $f 가 없음" }
    }
    if ($log -notmatch '자동 실행\(StartupTask\) 켬') { throw '로그인 자동 실행(StartupTask)이 켜지지 않음' }
    if ($log -match '자동 실행 등록:') { throw 'Store 판인데 레지스트리 Run 키에 등록함' }
    if (-not (Get-Process DayDeck -ErrorAction SilentlyContinue)) { throw 'DayDeck 프로세스가 없음' }

    $summary = "### MSIX 설치 테스트 통과`n`n``$($package.PackageFullName)``, 데이터: LocalState, 자동 실행: StartupTask"
    Write-Host $summary
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
}
finally {
    Get-Process DayDeck -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
    if ($package) { Remove-AppxPackage -Package $package.PackageFullName -ErrorAction SilentlyContinue }
    Remove-Item "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
}
