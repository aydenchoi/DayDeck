# Microsoft Store용 스크린샷을 찍는다 (CI용).
#  - Store 데스크톱 스크린샷은 1366x768 이상이어야 한다. CI 화면(1024x768)을 1920x1080으로 바꾸고,
#    샘플 데이터로 앱을 새로 띄워 최대화한 뒤 화면 전체를 찍는다.
#  - UI 테스트와 따로 돈다 (테스트가 데이터를 고친 뒤가 아니라 깨끗한 샘플 데이터로 찍으려고).
#  - 화면 해상도를 바꿀 수 없으면 경고만 하고 끝낸다 (빌드를 막지 않음).
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $SeedProject,
    [Parameter(Mandatory)] [string] $OutDir,
    [int] $Width = 1920,
    [int] $Height = 1080
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -Namespace Native -Name Display -MemberDefinition @'
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra;
    public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation; public int dmDisplayFixedOutput;
    public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;
    public int dmDisplayFlags; public int dmDisplayFrequency; public int dmICMMethod; public int dmICMIntent; public int dmMediaType;
    public int dmDitherType; public int dmReserved1; public int dmReserved2; public int dmPanningWidth; public int dmPanningHeight;
}
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
public static int SetResolution(int width, int height) {
    var dm = new DEVMODE();
    dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
    if (!EnumDisplaySettings(null, -1, ref dm)) return -100;
    dm.dmPelsWidth = width; dm.dmPelsHeight = height;
    dm.dmFields = 0x80000 | 0x100000; // DM_PELSWIDTH | DM_PELSHEIGHT
    return ChangeDisplaySettings(ref dm, 0);
}
'@

function Send-Chord([byte[]] $keys) {
    foreach ($k in $keys) { [Native.Display]::keybd_event($k, 0, 0, [UIntPtr]::Zero) }
    [array]::Reverse($keys)
    foreach ($k in $keys) { [Native.Display]::keybd_event($k, 0, 2, [UIntPtr]::Zero) }
}

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
function Find-ByName($parent, [string] $name, $controlType = $null) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
    if ($controlType) {
        $cond = New-Object System.Windows.Automation.AndCondition($cond, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $controlType)))
    }
    return $parent.FindFirst($Scope::Descendants, $cond)
}
function Find-ById($parent, [string] $id) {
    return $parent.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)))
}
function Find-AppWindow([string] $name, [int] $timeoutMs = 8000) {
    return Wait-Until {
        $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
        $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
        $A::RootElement.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.AndCondition($pidCond, $nameCond)))
    } $timeoutMs
}
# 이름으로 목록 항목을 찾는다 (가상화된 항목도). -Like면 와일드카드로 찾는다.
function Find-Item($list, [string] $name, [switch] $Like) {
    try { $icp = $list.GetCurrentPattern([System.Windows.Automation.ItemContainerPattern]::Pattern) }
    catch {
        $cond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::ListItem)
        return @($list.FindAll($Scope::Descendants, $cond) | Where-Object {
            ($Like -and $_.Current.Name -like $name) -or (-not $Like -and $_.Current.Name -eq $name) }) | Select-Object -First 1
    }
    $item = $icp.FindItemByProperty($null, $null, $null)
    while ($item) {
        try { $item.GetCurrentPattern([System.Windows.Automation.VirtualizedItemPattern]::Pattern).Realize() } catch {}
        $n = $item.Current.Name
        if (($Like -and $n -like $name) -or (-not $Like -and $n -eq $name)) {
            try { $item.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch {}
            return $item
        }
        $item = $icp.FindItemByProperty($item, $null, $null)
    }
    return $null
}
function Select-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 700
}
function Open-Page([string] $navTitle) {
    $item = Wait-Until { Find-ByName (Find-ById $script:window 'Nav') $navTitle $CT::ListItem }
    if (-not $item) { throw "탐색 항목 '$navTitle' 없음" }
    Select-Element $item
}

$saved = [System.Collections.Generic.List[string]]::new()
function Save-Screen([string] $name) {
    Start-Sleep -Milliseconds 800
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    $saved.Add("$name ($($b.Width)x$($b.Height))")
    Write-Host "  ✓ $name"
}
# 한 장이 실패해도 나머지는 찍는다.
function Shot([string] $name, [scriptblock] $prepare) {
    try { & $prepare; Save-Screen $name }
    catch { Write-Host "::warning::스토어 스크린샷 '$name' 실패: $_" }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$result = [Native.Display]::SetResolution($Width, $Height)
Start-Sleep -Seconds 2
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Host "화면 해상도 변경 결과 $result, 지금 $($screen.Width)x$($screen.Height)"
if ($screen.Width -lt 1366 -or $screen.Height -lt 768) {
    Write-Host "::warning::화면을 Store 최소 크기(1366x768) 이상으로 바꾸지 못해 스토어 스크린샷을 건너뜁니다."
    exit 0
}

$work = Join-Path ([IO.Path]::GetTempPath()) "DayDeckStore-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
Copy-Item $Exe "$work/DayDeck.exe"
dotnet run --project $SeedProject -c Release -- "$work/Data"
if ($LASTEXITCODE -ne 0) { throw "샘플 데이터 생성 실패" }
# 로그인 자동 실행을 러너에 등록하지 않는다.
$settingsPath = "$work/Data/settings.json"
if (Test-Path $settingsPath) {
    $json = Get-Content $settingsPath -Raw -Encoding utf8 | ConvertFrom-Json
    if (-not $json.startup) { $json | Add-Member -NotePropertyName startup -NotePropertyValue ([pscustomobject]@{}) }
    $json.startup | Add-Member -NotePropertyName runAtLogin -NotePropertyValue $false -Force
    $json | ConvertTo-Json -Depth 20 | Set-Content $settingsPath -Encoding utf8
}
else {
    '{ "startup": { "runAtLogin": false } }' | Set-Content $settingsPath -Encoding utf8
}

$proc = Start-Process "$work/DayDeck.exe" -PassThru
try {
    $script:window = Wait-Until {
        $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
        $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'DayDeck')
        $A::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.AndCondition($pidCond, $nameCond)))
    } 20000
    if (-not $script:window) { throw "메인 창이 열리지 않음" }

    # 시작 때 뜨는 알림 팝업(일일 브리핑, 놓친 알림)은 닫는다.
    $popup = Find-AppWindow 'DayDeck 알림' 8000
    if ($popup) {
        $popup.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Start-Sleep -Milliseconds 500
    }
    $script:window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState(
        [System.Windows.Automation.WindowVisualState]::Maximized)
    Start-Sleep -Seconds 1

    Shot '01-today' { Open-Page '오늘' }

    Shot '02-quick-add' {
        Start-Process "$work/DayDeck.exe" -ArgumentList '--quick-add' | Out-Null
        $script:quick = Find-AppWindow '빠른 추가'
        if (-not $script:quick) { throw '빠른 추가 창이 열리지 않음' }
        (Find-ByName $script:quick '빠른 추가 입력' $CT::Edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue(
            '내일 오후 3시 치과 #개인 30분 전 알림')
        Start-Sleep -Milliseconds 500
    }
    if ($script:quick) { try { $script:quick.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch {} }

    Shot '03-calendar-month' { Open-Page '달력' }

    Shot '04-task-detail' {
        Open-Page '오늘'
        $row = Wait-Until { Find-Item (Find-ById $script:window 'Rows') '디자인 리뷰 자료 준비' }
        if (-not $row) { throw "할 일 '디자인 리뷰 자료 준비' 없음" }
        Select-Element $row
    }

    Shot '05-notes' {
        Open-Page '메모'
        $folder = Wait-Until { Find-ByName (Find-ById $script:window 'FolderTree') '업무' $CT::TreeItem }
        if (-not $folder) { throw "메모 폴더 '업무' 없음" }
        Select-Element $folder
        $note = Wait-Until { Find-Item (Find-ById $script:window 'NoteList') '*주간회의' -Like }
        if (-not $note) { throw "메모 '주간회의' 없음" }
        Select-Element $note
    }

    Shot '06-search' {
        # Ctrl+K는 메인 창이 앞에 있어야 한다: 두 번째 실행으로 앱이 스스로 창을 앞으로 가져오게 한다.
        $show = Start-Process "$work/DayDeck.exe" -PassThru
        $show.WaitForExit(10000) | Out-Null
        Start-Sleep -Milliseconds 600
        (Find-ById $script:window 'Nav').SetFocus()
        Start-Sleep -Milliseconds 300
        Send-Chord @(0x11, 0x4B)
        $script:search = Find-AppWindow '전체 검색' 4000
        if (-not $script:search) {
            Select-Element (Find-ByName (Find-ById $script:window 'Nav') '검색   Ctrl+K' $CT::ListItem)
            $script:search = Find-AppWindow '전체 검색' 4000
        }
        if (-not $script:search) { throw '전체 검색 창이 열리지 않음' }
        (Find-ByName $script:search '검색어' $CT::Edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('리뷰')
        Start-Sleep -Milliseconds 800
    }
    if ($script:search) { try { $script:search.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch {} }

    Shot '07-calendar-week' {
        Open-Page '달력'
        $week = Wait-Until { Find-ByName $script:window '주' $CT::RadioButton }
        if (-not $week) { throw "'주' 버튼 없음" }
        Select-Element $week
    }

    Shot '08-today-dark' {
        Open-Page '설정'
        $theme = Wait-Until { Find-ByName $script:window '테마' $CT::ComboBox }
        if (-not $theme) { throw '테마 선택 없음' }
        $theme.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 300
        Select-Element (Wait-Until { Find-ByName $theme '어둡게' $CT::ListItem } 3000)
        $theme.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        Open-Page '오늘'
    }

    $summary = "### 스토어 스크린샷 $($saved.Count)장`n`n" + (($saved | ForEach-Object { "- $_" }) -join "`n")
    Write-Host $summary
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    $proc.WaitForExit(5000) | Out-Null
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
