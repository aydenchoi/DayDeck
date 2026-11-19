# 설치 프로그램을 조용히 실행해 확인한다 (CI용).
#  - 고른 폴더(공백·한글 포함)에 DayDeck.exe가 설치되고, 그 폴더 안의 Data가 데이터 폴더가 되는지
#  - 제거하면 프로그램은 지워지고 Data는 남는지 (조용히 제거하면 "데이터도 지울까요?"에 "아니요")
param([Parameter(Mandatory)] [string] $Setup)

$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) "DayDeckSetup-$([Guid]::NewGuid().ToString('N'))"
$dir = Join-Path $work '내 프로그램\DayDeck'
$proc = $null
try {
    $install = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$dir`"" -PassThru -Wait
    if ($install.ExitCode -ne 0) { throw "설치 실패 (exit code $($install.ExitCode))" }
    if (-not (Test-Path "$dir/DayDeck.exe")) { throw "설치 폴더에 DayDeck.exe가 없음: $dir" }
    if (-not (Test-Path "$dir/Data")) { throw '설치 폴더에 Data 폴더가 없음' }
    $uninstaller = Get-ChildItem $dir -Filter 'unins*.exe' | Select-Object -First 1
    if (-not $uninstaller) { throw '제거 프로그램이 없음' }

    # CI 러너의 레지스트리에 자동 실행을 등록하지 않도록 한다.
    '{ "startup": { "runAtLogin": false } }' | Set-Content "$dir/Data/settings.json" -Encoding utf8
    $proc = Start-Process "$dir/DayDeck.exe" -ArgumentList '--minimized' -PassThru
    $logPath = "$dir/Data/Logs/app.log"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 60 -and -not $proc.HasExited -and
           -not ((Test-Path $logPath) -and ((Get-Content $logPath -Raw -Encoding utf8) -match '시작 완료'))) {
        Start-Sleep -Milliseconds 500
    }
    if ($proc.HasExited) { throw "DayDeck이 종료됨 (exit code $($proc.ExitCode))" }
    if (-not (Test-Path "$dir/Data/data.db")) { throw '설치 폴더의 Data에 data.db가 없음 (다른 곳에 저장함)' }
    if ((Get-Content $logPath -Raw -Encoding utf8) -notmatch 'portable=True') { throw '설치 폴더의 Data를 쓰지 않음 (portable=True 없음)' }
    $proc.Kill(); $proc.WaitForExit(5000) | Out-Null

    $uninstall = Start-Process $uninstaller.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
    if ($uninstall.ExitCode -ne 0) { throw "제거 실패 (exit code $($uninstall.ExitCode))" }
    # 제거 프로그램은 자신을 지우려고 잠깐 더 돈다.
    $sw.Restart()
    while ($sw.Elapsed.TotalSeconds -lt 20 -and (Test-Path "$dir/DayDeck.exe")) { Start-Sleep -Milliseconds 500 }
    if (Test-Path "$dir/DayDeck.exe") { throw '제거 후에도 DayDeck.exe가 남음' }
    if (-not (Test-Path "$dir/Data/data.db")) { throw '제거할 때 데이터를 지움 (남겨야 함)' }

    $summary = "### 설치 프로그램 테스트 통과`n`n고른 폴더에 설치, 데이터는 ``설치 폴더\Data``, 제거 후 데이터 남음 ($((Get-Item $Setup).Name), $([math]::Round((Get-Item $Setup).Length / 1MB, 1)) MB)"
    Write-Host $summary
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill() }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
