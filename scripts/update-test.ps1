# 자동 업데이트를 처음부터 끝까지 확인한다 (CI용).
#  - 로컬 서버가 "새 버전 99.0.0" 릴리스(같은 exe를 담은 zip + 해시)를 흉내 낸다
#  - 설정 → "지금 확인" → 업데이트 창에서 "지금 업데이트"를 누른다 (사용자 확인)
#  - 내려받기·해시 확인·백업·exe 바꾸기 후 새 프로세스로 다시 시작하고, .old 파일을 지우고, 데이터가 그대로인지 본다
param(
    [Parameter(Mandatory)] [string] $Exe
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Wait-Until([scriptblock] $condition, [int] $timeoutMs = 10000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $result = & $condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
function Find-ByName($parent, [string] $name, $controlType) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $controlType)))
    return $parent.FindFirst($Scope::Descendants, $cond)
}
function Find-Window([int] $processId, [string] $name, [int] $timeoutMs = 10000) {
    return Wait-Until {
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $processId)),
            (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)),
            (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window)))
        $A::RootElement.FindFirst($Scope::Descendants, $cond)
    } $timeoutMs
}
function Invoke-Named($root, [string] $name) {
    $button = Wait-Until { Find-ByName $root $name $CT::Button } 5000
    if (-not $button) { throw "'$name' 단추 없음" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$failures = [System.Collections.Generic.List[string]]::new()
function Check([bool] $condition, [string] $message) {
    if ($condition) { Write-Host "  ✓ $message" } else { Write-Host "  ✗ $message"; $failures.Add($message) }
}

$work = Join-Path ([IO.Path]::GetTempPath()) "DayDeckUpdate-$([Guid]::NewGuid().ToString('N'))"
$serve = Join-Path $work 'serve'
$app = Join-Path $work 'app'
New-Item -ItemType Directory -Path $serve, $app | Out-Null
Copy-Item $Exe "$app/DayDeck.exe"

# 가짜 릴리스: 같은 exe를 새 버전으로 담는다 (내용은 같아도 설치·재시작 경로는 실제와 같다).
$zip = Join-Path $serve 'DayDeck-99.0.0-x64.zip'
Compress-Archive -Path $Exe -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$port = Get-Random -Minimum 20000 -Maximum 40000
$base = "http://localhost:$port/"
@{
    tag_name = 'v99.0.0'; name = 'DayDeck 99.0.0'; draft = $false; prerelease = $false
    html_url = "${base}release"; published_at = '2026-10-29T00:00:00Z'
    body = "## 시험용 새 버전`n- 업데이트 시험"
    assets = @(@{ name = 'DayDeck-99.0.0-x64.zip'; size = (Get-Item $zip).Length; browser_download_url = "${base}DayDeck-99.0.0-x64.zip"; digest = "sha256:$hash" })
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $serve 'latest') -Encoding utf8

$server = Start-Job -ArgumentList $port, $serve -ScriptBlock {
    param($port, $dir)
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://localhost:$port/")
    $listener.Start()
    while ($true) {
        $context = $listener.GetContext()
        $path = Join-Path $dir ($context.Request.Url.AbsolutePath.TrimStart('/'))
        if (Test-Path $path -PathType Leaf) {
            $bytes = [IO.File]::ReadAllBytes($path)
            $context.Response.ContentLength64 = $bytes.Length
            $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
        else {
            $context.Response.StatusCode = 404
        }
        $context.Response.Close()
    }
}

$env:DAYDECK_UPDATE_API = "${base}latest"
$proc = $null
$restarted = $null
try {
    # 서버가 뜰 때까지
    $ready = Wait-Until { try { (Invoke-WebRequest "${base}latest" -UseBasicParsing).StatusCode -eq 200 } catch { $false } } 15000
    Check ($null -ne $ready) '시험용 릴리스 서버'

    $proc = Start-Process "$app/DayDeck.exe" -PassThru
    $main = Find-Window $proc.Id 'DayDeck' 20000
    Check ($null -ne $main) '앱 시작'
    Start-Sleep -Seconds 2
    # 데이터가 그대로인지 보려고 표시를 하나 남긴다.
    $marker = Join-Path $app 'Data/Notes/업데이트 전.md'
    Set-Content $marker '업데이트 전에 쓴 메모' -Encoding utf8

    Write-Host "[지금 확인 → 업데이트 창]"
    $nav = Wait-Until { $main.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'Nav'))) }
    $settings = Wait-Until { Find-ByName $nav '설정' $CT::ListItem }
    $settings.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 800
    Invoke-Named $main '지금 확인'
    $prompt = Find-Window $proc.Id 'DayDeck 업데이트' 20000
    Check ($null -ne $prompt) '새 버전 99.0.0을 알리는 창 (설치는 아직 안 함)'
    if (-not $prompt) { throw '업데이트 창이 뜨지 않음' }
    Check ($proc.HasExited -eq $false) '묻기만 하고 저절로 설치하지 않음'

    Write-Host "[지금 업데이트]"
    Invoke-Named $prompt '지금 업데이트'
    $exited = Wait-Until { $proc.HasExited } 120000
    Check ($null -ne $exited) '설치 후 지금 버전이 끝남'
    if (-not $exited) {
        # 진단: 업데이트 창의 진행 글자
        $texts = @($prompt.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } | Where-Object { $_ })
        Write-Host "  업데이트 창: $($texts -join ' | ')"
    }
    # 새 버전은 다른 프로세스여야 한다 (같은 exe 경로).
    $restarted = Wait-Until { Get-Process DayDeck -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $proc.Id -and $_.Path -eq (Resolve-Path "$app/DayDeck.exe").Path } | Select-Object -First 1 } 20000
    Check ($null -ne $restarted) '새 버전으로 다시 시작함'

    $log = Join-Path $app 'Data/Logs'
    $logText = Wait-Until { $t = Get-Content (Get-ChildItem $log -Filter *.log | Sort-Object LastWriteTime | Select-Object -Last 1).FullName -Raw -Encoding utf8; if ($t -match '업데이트 후 시작') { $t } } 20000
    Check ($logText -match '업데이트 설치: [\d.]+ → 99\.0\.0') '로그: 업데이트 설치'
    Check ($logText -match '업데이트 후 시작') '로그: 업데이트 후 시작 (--updated)'
    Check ($logText -match '백업 완료') '설치 전에 백업함'
    $oldGone = Wait-Until { -not (Test-Path "$app/DayDeck.exe.old") } 20000
    Check ($null -ne $oldGone) '지난 버전 파일(.old)을 지움'
    Check ((Get-Content $marker -Raw -Encoding utf8) -like '업데이트 전에 쓴 메모*') '데이터(Data 폴더)는 그대로'
    if (-not $logText) { $logText = Get-Content (Get-ChildItem $log -Filter *.log | Sort-Object LastWriteTime | Select-Object -Last 1).FullName -Raw -Encoding utf8 }
    Write-Host "--- app.log ---"; Write-Host $logText
}
finally {
    foreach ($p in @($proc, $restarted)) { if ($p -and -not $p.HasExited) { $p.Kill(); $p.WaitForExit(5000) | Out-Null } }
    # 서버 작업은 요청을 기다리며 멈춰 있어 Stop-Job이 오래 걸린다 (CI에서 2분). 바로 없앤다.
    Remove-Job $server -Force -ErrorAction SilentlyContinue
    Remove-Item Env:DAYDECK_UPDATE_API -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "업데이트 시험 실패: $($failures -join ', ')"
    exit 1
}
Write-Host "업데이트 시험 통과"
