# 게시된 exe를 포터블 모드로 실행해 M1 기본 동작을 확인한다.
#  - 트레이 모드(--minimized)로 시작해서 계속 살아 있는지
#  - DB/설정/로그/메모 폴더가 데이터 폴더 한 곳에 만들어지는지 (복사만으로 이동 가능)
#  - 두 번째 실행이 바로 종료되는지 (단일 인스턴스)
#  - 트레이 상주 상태의 메모리 사용량 (보고만 함)
param([Parameter(Mandatory)] [string] $Exe, [string] $Label = '')

$ErrorActionPreference = 'Stop'

$work = Join-Path ([IO.Path]::GetTempPath()) "DayDeckSmoke-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path "$work/Data" | Out-Null
Copy-Item $Exe "$work/DayDeck.exe"
# CI 러너의 레지스트리에 자동 실행을 등록하지 않도록 한다.
'{ "startup": { "runAtLogin": false } }' | Set-Content "$work/Data/settings.json" -Encoding utf8

$first = Start-Process "$work/DayDeck.exe" -ArgumentList '--minimized' -PassThru
try {
    # 시작이 끝날 때까지 기다린다 (느린 CI 러너에서는 몇 초 더 걸린다).
    $logPath = "$work/Data/Logs/app.log"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 60 -and -not $first.HasExited -and
           -not ((Test-Path $logPath) -and ((Get-Content $logPath -Raw -Encoding utf8) -match '시작 완료'))) {
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    if ($first.HasExited) { throw "첫 번째 인스턴스가 종료됨 (exit code $($first.ExitCode))" }

    foreach ($f in 'Data/data.db', 'Data/settings.json', 'Data/Logs/app.log') {
        if (-not (Test-Path "$work/$f")) { throw "$f 파일이 없음" }
    }
    # 파일 복사만으로 옮길 수 있도록 DB는 data.db 한 파일이어야 한다 (WAL 파일 없음)
    foreach ($f in 'Data/data.db-wal', 'Data/data.db-shm') {
        if (Test-Path "$work/$f") { throw "$f 파일이 있음 (DB가 단일 파일이 아님)" }
    }
    if (-not (Test-Path "$work/Data/Notes")) { throw "Data/Notes 폴더가 없음" }
    $log = Get-Content "$work/Data/Logs/app.log" -Raw -Encoding utf8
    if ($log -notmatch '시작 완료') { throw "로그에 시작 완료 기록이 없음:`n$log" }

    $second = Start-Process "$work/DayDeck.exe" -ArgumentList '--minimized' -PassThru
    if (-not $second.WaitForExit(10000)) { $second.Kill(); throw "두 번째 인스턴스가 종료되지 않음" }
    if ($first.HasExited) { throw "두 번째 실행 후 첫 번째 인스턴스가 종료됨" }

    $first.Refresh()
    $ws = [math]::Round($first.WorkingSet64 / 1MB, 1)
    $private = [math]::Round($first.PrivateMemorySize64 / 1MB, 1)
    $startup = ($log -split "`n" | Select-String '시작 완료' | Select-Object -First 1).ToString().Trim()
    $trayLine = ($log -split "`n" | Select-String '트레이 표시' | Select-Object -First 1)
    $tray = if ($trayLine) { $trayLine.ToString().Trim() } else { '' }
    $size = [math]::Round((Get-Item $Exe).Length / 1MB, 1)

    $summary = @"
### 스모크 테스트 통과 $Label

| 항목 | 값 |
|---|---|
| 트레이 상주 Working Set | **$ws MB** (목표 30MB 이하) |
| Private Bytes | $private MB |
| exe 크기 | $size MB |
| 트레이 표시 | ``$tray`` |
| 시작 로그 | ``$startup`` |
"@
    Write-Host $summary
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
}
finally {
    if (-not $first.HasExited) { $first.Kill() }
    $first.WaitForExit(5000) | Out-Null
    if (Test-Path "$work/Data/Logs/app.log") {
        Write-Host '--- app.log ---'
        Get-Content "$work/Data/Logs/app.log" -Encoding utf8 | Write-Host
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
