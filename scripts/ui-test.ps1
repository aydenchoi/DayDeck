# 게시된 exe를 샘플 데이터로 실행하고 UI 자동화로 화면을 조작해 확인한다 (CI용).
#  - 각 화면의 구역/행 텍스트를 검사하고 스크린샷을 남긴다
#  - 상세 패널에서 제목을 고치면 자동 저장되어 목록에 반영되는지
#  - 체크 원을 누르면 완료 구역으로 옮겨지는지
#  - 창을 닫으면 트레이로 계속 실행되는지
#  - 알림 팝업: 시작 시 일일 브리핑과 놓친 알림 묶음, 실행 중 정시 알림과 "완료" 버튼
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $SeedProject,
    [Parameter(Mandatory)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -Namespace Native -Name Keyboard -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern short VkKeyScan(char ch);
'@
function Send-Chord([byte[]] $keys) {
    foreach ($k in $keys) { [Native.Keyboard]::keybd_event($k, 0, 0, [UIntPtr]::Zero) }
    [array]::Reverse($keys)
    foreach ($k in $keys) { [Native.Keyboard]::keybd_event($k, 0, 2, [UIntPtr]::Zero) }
}

# 글자를 키 입력으로 친다 (영문 자판 기준, Shift 포함). SendKeys는 일부 문자열을 거부해서 쓰지 않는다.
function Type-Text([string] $text) {
    foreach ($ch in $text.ToCharArray()) {
        $scan = [Native.Keyboard]::VkKeyScan($ch)
        $vk = [byte]($scan -band 0xFF)
        if (($scan -shr 8) -band 1) { Send-Chord @(0x10, $vk) } else { Send-Chord @($vk) }
        Start-Sleep -Milliseconds 15
    }
}

$A = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) "DayDeckUi-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
Copy-Item $Exe "$work/DayDeck.exe"
dotnet run --project $SeedProject -c Release -- "$work/Data"
if ($LASTEXITCODE -ne 0) { throw "샘플 데이터 생성 실패" }

$failures = [System.Collections.Generic.List[string]]::new()
function Check([bool] $condition, [string] $message) {
    if ($condition) { Write-Host "  ✓ $message" } else { Write-Host "  ✗ $message"; $failures.Add($message) }
}

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
        $typeCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $controlType)
        $cond = New-Object System.Windows.Automation.AndCondition($cond, $typeCond)
    }
    return $parent.FindFirst($Scope::Descendants, $cond)
}

function Find-ById($parent, [string] $id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)
    return $parent.FindFirst($Scope::Descendants, $cond)
}

function Get-Texts($element) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Text)
    return @($element.FindAll($Scope::Descendants, $cond) | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
}

# 목록은 가상화되어 화면 밖 행은 UI 자동화 트리에 없다 (글자 크기를 키우면 더 많다).
# ItemContainerPattern으로 모든 항목을 차례로 찾고, 가상화된 항목은 Realize로 만든다.
function Get-AllItems($list) {
    try {
        $icp = $list.GetCurrentPattern([System.Windows.Automation.ItemContainerPattern]::Pattern)
    }
    catch {
        $cond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::ListItem)
        return @($list.FindAll($Scope::Children, $cond))
    }
    $items = @()
    $item = $icp.FindItemByProperty($null, $null, $null)
    while ($item) {
        if (-not $item.Current.Name) {
            try { $item.GetCurrentPattern([System.Windows.Automation.VirtualizedItemPattern]::Pattern).Realize() } catch {}
        }
        $items += $item
        $item = $icp.FindItemByProperty($item, $null, $null)
    }
    return $items
}

function Get-ItemNames($list) {
    return @(Get-AllItems $list | ForEach-Object { $_.Current.Name })
}

# 이름으로 항목을 찾고 (가상화되어 있으면 만들어서) 화면에 보이게 한다.
function Find-Item($list, [string] $name) {
    try {
        $icp = $list.GetCurrentPattern([System.Windows.Automation.ItemContainerPattern]::Pattern)
        $item = $icp.FindItemByProperty($null, $A::NameProperty, $name)
        if ($item) {
            try { $item.GetCurrentPattern([System.Windows.Automation.VirtualizedItemPattern]::Pattern).Realize() } catch {}
            try { $item.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch {}
            return $item
        }
    }
    catch {}
    return Find-ByName $list $name $CT::ListItem
}

function Save-Shot([string] $name, $element = $script:window) {
    Start-Sleep -Milliseconds 500
    $r = $element.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Find-ReminderWindow([int] $timeoutMs) {
    return Wait-Until {
        $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
        $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'DayDeck 알림')
        $A::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.AndCondition($pidCond, $nameCond)))
    } $timeoutMs
}

function Click-InGroup($root, [string] $groupName, [string] $buttonName) {
    $group = Find-ByName $root $groupName $CT::Group
    if (-not $group) { throw "알림 카드 '$groupName' 없음" }
    $button = Find-ByName $group $buttonName $CT::Button
    if (-not $button) { throw "카드 '$groupName'에 '$buttonName' 버튼 없음" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
}

function Open-Page([string] $navTitle) {
    $nav = Find-ById $script:window 'Nav'
    $item = Wait-Until { Find-ByName $nav $navTitle $CT::ListItem }
    if (-not $item) { throw "탐색 항목 '$navTitle' 없음" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 600
    return Find-ById $script:window 'Rows'
}

function Select-Row($rows, [string] $title) {
    $row = Wait-Until { Find-Item $rows $title }
    if (-not $row) { throw "행 '$title' 없음" }
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
    return $row
}

# 워크플로우: Excalidraw 기준 샘플 (tools/excalidraw-ref, 진짜 Excalidraw 렌더링과 비교용 스크린샷을 남긴다)
$drawingSamples = Join-Path $PSScriptRoot '../tools/excalidraw-ref/samples'
New-Item -ItemType Directory -Force -Path "$work/Data/Drawings" | Out-Null
Get-ChildItem $drawingSamples -Filter '*.excalidraw' | Copy-Item -Destination "$work/Data/Drawings"

$proc = Start-Process "$work/DayDeck.exe" -PassThru
try {
    # 알림 팝업("DayDeck 알림")이 먼저 뜰 수 있으므로 제목으로 메인 창을 찾는다.
    $script:window = Wait-Until {
        $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
        $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'DayDeck')
        $A::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.AndCondition($pidCond, $nameCond)))
    } 20000
    if (-not $script:window) { throw "메인 창이 열리지 않음" }

    # CI 화면(1024x768)에 맞춘다.
    $transform = $script:window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    $transform.Move(0, 0)
    $transform.Resize(1024, 740)

    Write-Host "[알림 팝업: 시작 시]"
    $popup = Find-ReminderWindow 10000
    Check ($null -ne $popup) '알림 팝업이 뜸'
    if ($popup) {
        $groups = @($popup.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Group))) | ForEach-Object { $_.Current.Name })
        Write-Host ("  카드: " + ($groups -join ' | '))
        Check ($groups -contains '오늘의 할 일') '일일 브리핑 카드'
        Check ($groups -contains '놓친 알림 2개') '놓친 알림 2개를 한 카드로 묶음'
        $briefingTexts = Get-Texts (Find-ByName $popup '오늘의 할 일' $CT::Group)
        Check (@($briefingTexts | Where-Object { $_ -like '*기한 지남 1개*' }).Count -ge 1) "브리핑에 기한 지남 표시 ($($briefingTexts -join ' / '))"
        Save-Shot '00-reminder-popup' $popup
        Click-InGroup $popup '놓친 알림 2개' '모두 확인'
        Click-InGroup $popup '오늘의 할 일' '확인'
        $closed = Wait-Until { -not (Find-ReminderWindow 300) } 3000
        Check ($null -ne $closed) '모두 처리하면 팝업이 닫힘'
    }

    Write-Host "[오늘]"
    $rows = Open-Page '오늘'
    $names = Get-ItemNames $rows
    Write-Host ("  행: " + ($names -join ' | '))
    Check ($names -contains '⚠ 기한 지남 1') '기한 지남 구역 머리글'
    Check ($names[$names.IndexOf('⚠ 기한 지남 1') + 1] -eq '분기 보고서 제출') '기한 지난 항목이 할 일 맨 위'
    Check (@($names | Where-Object { $_ -like '일정 *' }).Count -eq 1 -and $names -contains '점심 약속') '오늘 일정도 함께 표시'
    Check ($names -contains '완료됨 2') '완료됨 구역에 2개'
    Check ($names.IndexOf('스탠드업 미팅') -gt $names.IndexOf('완료됨 2')) '완료 항목은 완료 구역 아래'
    $texts = Get-Texts $rows
    Check (@($texts | Where-Object { $_ -like '*1일 지남*' }).Count -ge 1) '기한 지남 문구 (1일 지남)'
    Check ($texts -contains '하위 할 일 1/3 · 오늘까지' -or @($texts | Where-Object { $_ -like '*하위 할 일 1/3*' }).Count -ge 1) '하위 할 일 진행 표시'
    $progress = @(Get-Texts $script:window | Where-Object { $_ -like '오늘 *개 중 *개 완료' })
    Check ($progress.Count -eq 1) "진행률 표시 ($progress)"
    Save-Shot '01-today'

    Write-Host "[상세 패널 + 자동 저장]"
    # 다른 할 일로 바꿔 선택해도 목록/마감 값이 지워지면 안 된다 (아래 업무 목록에서 확인)
    Select-Row $rows '분기 보고서 제출' | Out-Null
    Select-Row $rows '디자인 리뷰 자료 준비' | Out-Null
    $detail = Find-ById $script:window 'Detail'
    $titleBox = Find-ById $detail 'TitleBox'
    Check ($null -ne $titleBox) '상세 패널 열림'
    Check ($null -ne (Find-ByName $detail '시안 3종 정리')) '하위 할 일 표시'
    Save-Shot '02-detail'
    $titleBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('디자인 리뷰 자료 준비 (수정)')
    # 기본 설정: 편집 멈춤 3초 후 저장
    $renamed = Wait-Until { Find-Item $rows '디자인 리뷰 자료 준비 (수정)' } 8000
    Check ($null -ne $renamed) '제목 수정이 자동 저장되어 목록에 반영'

    Write-Host "[완료 전환]"
    $row = Select-Row $rows '장보기 (우유, 계란)'
    $check = Find-ByName $row '완료 전환' $CT::Button
    $check.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $done = Wait-Until { (Get-ItemNames (Find-ById $script:window 'Rows')) -contains '완료됨 3' } 5000
    Check ($null -ne $done) '체크하면 완료 구역으로 이동 (완료됨 3)'
    Save-Shot '03-today-after-complete'

    Write-Host "[예정]"
    $names = Get-ItemNames (Open-Page '예정')
    Write-Host ("  행: " + ($names -join ' | '))
    Check ($names -contains '치과 예약') '내일 일정 표시'
    Check ($names -contains '자동차 보험 갱신') '5일 뒤 일정 표시'
    Check (-not ($names -contains '분기 보고서 제출')) '기한 지난 항목은 예정에 없음'
    Check (-not ($names -contains '건강검진 예약')) '기본 7일: 10일 뒤 할 일은 예정에 없음'
    Save-Shot '04-upcoming'
    $upcomingRange = Find-ByName $script:window '기간' $CT::ComboBox
    Check ($null -ne $upcomingRange) '예정 화면에 기간 선택 (7일/14일)'
    if ($upcomingRange) {
        $upcomingRange.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $fourteen = Wait-Until { Find-ByName $upcomingRange '14일' $CT::ListItem } 3000
        $fourteen.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $upcomingRange.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        $wider = Wait-Until { (Get-ItemNames (Find-ById $script:window 'Rows')) -contains '건강검진 예약' } 4000
        Check ($null -ne $wider) '14일로 넓히면 10일 뒤 할 일도 보임'
        # 다음 단계들을 위해 기본(7일)으로 되돌린다
        $upcomingRange.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $seven = Wait-Until { Find-ByName $upcomingRange '7일' $CT::ListItem } 3000
        $seven.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $upcomingRange.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    }

    Write-Host "[기한 지남]"
    $names = Get-ItemNames (Open-Page '기한 지남')
    Check ($names -contains '분기 보고서 제출') '기한 지난 항목'
    Save-Shot '05-overdue'

    Write-Host "[완료됨]"
    $names = Get-ItemNames (Open-Page '완료됨')
    Check ($names -contains '스탠드업 미팅' -and $names -contains '장보기 (우유, 계란)') '완료 항목 목록'
    Save-Shot '06-completed'

    Write-Host "[목록: 업무]"
    $names = Get-ItemNames (Open-Page '업무')
    Write-Host ("  행: " + ($names -join ' | '))
    Check ($names -contains '디자인 리뷰 회의') '목록의 할 일'
    Check ($names -contains '분기 보고서 제출' -and $names -contains '디자인 리뷰 자료 준비 (수정)') '상세 패널을 거친 할 일의 목록이 유지됨'
    Check (-not ($names -contains '장보기 (우유, 계란)')) '다른 목록 항목은 없음'
    Save-Shot '07-list-work'

    Write-Host "[설정]"
    Open-Page '설정' | Out-Null
    Check (@(Get-Texts $script:window) -contains '자동 저장') '설정 화면'
    Save-Shot '08-settings'
    $checkUpdate = Find-ByName $script:window '지금 확인' $CT::Button
    Check ($null -ne $checkUpdate) '업데이트 "지금 확인" 단추'
    if ($checkUpdate) {
        $checkUpdate.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $updateResult = Wait-Until {
            $t = (Find-ById $script:window 'UpdateStatus').Current.Name
            if ($t -and $t -ne '확인하는 중…') { $t }
        } 20000
        Write-Host "  업데이트 확인: $updateResult"
        # 테스트하는 빌드보다 새 릴리스는 없으므로 최신이어야 한다 (네트워크가 막혔으면 확인 실패 문구).
        Check ($updateResult -like '최신 버전입니다*' -or $updateResult -like '*확인하지 못했습니다*') '지금 확인 결과 표시'
    }

    Write-Host "[달력]"
    Open-Page '달력' | Out-Null
    function Select-Radio([string] $name) {
        $radio = Wait-Until { Find-ByName $script:window $name $CT::RadioButton }
        if (-not $radio) { throw "'$name' 버튼 없음" }
        $radio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 700
    }
    $texts = Get-Texts $script:window
    Check ($texts -contains '프로젝트 마감 주간') '월 보기: 여러 날 종일 일정'
    Check (@($texts | Where-Object { $_ -like '*스탠드업*' }).Count -ge 1) '월 보기: 반복 일정'
    Save-Shot '10-calendar-month'
    Select-Radio '주'
    $texts = Get-Texts $script:window
    Check (@($texts | Where-Object { $_ -like '*스탠드업*' }).Count -ge 1) '주 보기: 반복 일정 블록'
    Check (@($texts | Where-Object { $_ -like '09:00 – 09:15*' }).Count -ge 1) '주 보기: 시간 표시'
    Save-Shot '11-calendar-week'

    $newButton = Find-ByName $script:window '＋ 새 일정' $CT::Button
    $newButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $editor = Wait-Until {
        $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
        $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, '새 일정')
        $A::RootElement.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.AndCondition($pidCond, $nameCond)))
    } 5000
    Check ($null -ne $editor) '새 일정 창이 열림'
    if ($editor) {
        (Find-ById $editor 'TitleBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('UI 테스트 일정')
        # 일정 메모 서식
        $eventMemo = Find-ByName $editor '일정 메모' $CT::Edit
        Check ($null -ne $eventMemo) '일정 메모 입력란'
        if ($eventMemo) {
            $eventMemo.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('회의실 예약 확인')
            $eventMemo.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.FindText('회의실', $false, $false).Select()
            (Find-ByName $editor '메모 굵게' $CT::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 300
            $eventMemoValue = $eventMemo.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            Check ($eventMemoValue -eq '**회의실** 예약 확인') "일정 메모 굵게 ($eventMemoValue)"
        }
        Save-Shot '12-event-editor' $editor
        (Find-ByName $editor '저장' $CT::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 800
    }
    Select-Radio '목록'
    $texts = Get-Texts $script:window
    Check ($texts -contains 'UI 테스트 일정') '편집 창에서 만든 일정이 목록 보기에 표시'
    Check ($texts -contains '엄마 생신' -and $texts -contains '디자인 리뷰') '목록 보기: 앞으로의 일정'
    Save-Shot '13-calendar-agenda'
    Select-Radio '월'

    Write-Host "[메모]"
    Open-Page '메모' | Out-Null
    $tree = Find-ById $script:window 'FolderTree'
    $workFolder = Wait-Until { Find-ByName $tree '업무' $CT::TreeItem }
    Check ($null -ne $workFolder) '폴더 트리에 업무 폴더'
    $workFolder.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 500
    $noteNames = Get-ItemNames (Find-ById $script:window 'NoteList')
    Write-Host ("  메모: " + ($noteNames -join ' | '))
    Check ($noteNames -contains '아이디어') '폴더의 메모 목록'
    Check (@($noteNames | Where-Object { $_ -like '*주간회의' }).Count -ge 1) '하위 폴더(회의록)의 메모도 함께 보임'
    (Find-ByName $script:window '새 메모' $CT::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $richDefault = Wait-Until { Find-ById $script:window 'NoteRichEditor' } 5000
    Check ($null -ne $richDefault) '메모는 "보기"(바로 고치는 편집기)로 열림'
    Check ($null -eq (Find-ById $script:window 'NoteEditor')) '원문 편집기는 누르기 전에는 안 보임'
    (Find-ByName $script:window '편집 보기' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $editor = Wait-Until { Find-ById $script:window 'NoteEditor' } 5000
    Check ($null -ne $editor) '"편집"을 누르면 원문 편집기'
    if ($editor) {
        $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("# UI 테스트 메모`r`n- [ ] 메모에서 만든 할 일")
        $saved = Wait-Until { Test-Path "$work/Data/Notes/업무/UI 테스트 메모.md" } 8000
        Check ($null -ne $saved) '자동 저장 후 첫 줄로 파일 이름이 정해짐 (업무/UI 테스트 메모.md)'
        if ($saved) {
            $content = Get-Content "$work/Data/Notes/업무/UI 테스트 메모.md" -Raw -Encoding utf8
            Check ($content -like '*메모에서 만든 할 일*') '파일에 내용이 저장됨'
        }
        Save-Shot '14-notes'
        (Find-ByName $script:window '☑ 할 일로 만들기' $CT::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 500
        Check (@(Get-Texts $script:window | Where-Object { $_ -like '할 일 1개를 오늘 할 일에 추가*' }).Count -eq 1) '메모 줄을 할 일로 만들기'
    }

    Write-Host "[빠른 추가]"
    function Find-QuickAdd([int] $timeoutMs) {
        return Wait-Until {
            $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
            $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, '빠른 추가')
            $A::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.AndCondition($pidCond, $nameCond)))
        } $timeoutMs
    }
    Start-Process "$work/DayDeck.exe" -ArgumentList '--quick-add' | Out-Null
    $quick = Find-QuickAdd 8000
    Check ($null -ne $quick) '--quick-add로 빠른 추가 창이 열림'
    if ($quick) {
        $quickInput = Find-ByName $quick '빠른 추가 입력' $CT::Edit
        $quickInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('내일 오후 3시 UI 빠른 일정 #업무 30분 전 알림')
        Start-Sleep -Milliseconds 400
        $summary = @(Get-Texts $quick | Where-Object { $_ -like '📅 일정*' })
        Check ($summary.Count -ge 1) "입력을 일정으로 해석 ($summary)"
        Save-Shot '15-quick-add' $quick
        $quickInput.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        $closed = Wait-Until { -not (Find-QuickAdd 200) } 4000
        Check ($null -ne $closed) 'Enter로 저장하고 창이 닫힘'
        Open-Page '달력' | Out-Null
        Select-Radio '목록'
        Check (@(Get-Texts $script:window) -contains 'UI 빠른 일정') '빠른 추가로 만든 일정이 달력에 표시'
        Select-Radio '월'
    }

    # 전역 단축키 Ctrl+Alt+Space (VK: Ctrl 0x11, Alt 0x12, Space 0x20)
    Send-Chord @(0x11, 0x12, 0x20)
    $quick = Find-QuickAdd 4000
    Check ($null -ne $quick) '전역 단축키 Ctrl+Alt+Space로 빠른 추가 창이 열림'
    if ($quick) {
        $quick.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Start-Sleep -Milliseconds 300
    }

    Write-Host "[전체 검색 Ctrl+K]"
    # 단축키는 메인 창에 키보드 포커스가 있어야 한다. 테스트 프로세스는 다른 프로세스 창을 앞으로 가져올 수 없으므로
    # (포그라운드 잠금) 두 번째 실행의 "show" 명령으로 앱이 스스로 창을 앞으로 가져오게 한다.
    $show = Start-Process "$work/DayDeck.exe" -PassThru
    $show.WaitForExit(10000) | Out-Null
    Start-Sleep -Milliseconds 600
    (Find-ById $script:window 'Nav').SetFocus()
    Start-Sleep -Milliseconds 300
    Send-Chord @(0x11, 0x4B)
    $search = Wait-Until {
        $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
        $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, '전체 검색')
        # 소유 창(Owner = 메인 창)은 UI 자동화 트리에서 메인 창 아래에 있다.
        $A::RootElement.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.AndCondition($pidCond, $nameCond)))
    } 4000
    Check ($null -ne $search) 'Ctrl+K로 전체 검색 창이 열림'
    if (-not $search) {
        # 진단: 키가 안 간 것인지, 창이 안 뜬 것인지 구분한다.
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        Write-Host "  포커스: $($focused.Current.Name) / pid $($focused.Current.ProcessId) (앱 pid $($proc.Id))"
        $navSearch = Find-ByName (Find-ById $script:window 'Nav') '검색   Ctrl+K' $CT::ListItem
        $navSearch.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $search = Wait-Until {
            $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
            $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, '전체 검색')
            $A::RootElement.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.AndCondition($pidCond, $nameCond)))
        } 4000
        Write-Host "  탐색의 검색 항목으로는 열림: $($null -ne $search)"
    }
    if ($search) {
        (Find-ByName $search '검색어' $CT::Edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('리뷰')
        $found = Wait-Until { $names = Get-ItemNames (Find-ById $search 'Results'); if ($names -contains '디자인 리뷰 회의' -and $names -contains '디자인 리뷰') { $names } } 4000
        Write-Host ("  결과: " + ($found -join ' | '))
        Check ($null -ne $found) '할 일과 일정을 함께 찾음 (디자인 리뷰 회의, 디자인 리뷰)'
        (Find-ByName $search '검색어' $CT::Edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('장보기')
        $found = Wait-Until { (Get-ItemNames (Find-ById $search 'Results')) -contains '장보기' } 4000
        Check ($null -ne $found) '메모도 찾음 (개인/장보기.txt)'
        Save-Shot '16-search' $search
        $search.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Start-Sleep -Milliseconds 300
    }

    Write-Host "[삭제 → 휴지통 → 복원]"
    $rows = Open-Page '업무'
    Select-Row $rows '디자인 리뷰 회의' | Out-Null
    (Find-ByName $script:window '🗑 삭제' $CT::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    Check (-not ((Get-ItemNames (Find-ById $script:window 'Rows')) -contains '디자인 리뷰 회의')) '삭제하면 목록에서 사라짐'
    Open-Page '휴지통' | Out-Null
    $trashList = Find-ById $script:window 'Items'
    $trashed = Wait-Until { Find-Item $trashList '디자인 리뷰 회의' } 3000
    Check ($null -ne $trashed) '휴지통에 지운 할 일이 있음'
    Save-Shot '17-trash'
    if ($trashed) {
        (Find-ByName $trashed '↩ 복원' $CT::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 500
        Check ((Get-ItemNames (Open-Page '업무')) -contains '디자인 리뷰 회의') '휴지통에서 복원하면 목록에 돌아옴'
    }

    Write-Host "[설정: 백업, 어두운 테마]"
    Open-Page '설정' | Out-Null
    $before = @(Get-ChildItem "$work/Data/Backups" -Filter '*.zip' -ErrorAction SilentlyContinue).Count
    Start-Sleep -Milliseconds 1100  # 백업 파일 이름은 초 단위
    (Find-ByName $script:window '지금 백업' $CT::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $backup = Wait-Until { @(Get-ChildItem "$work/Data/Backups" -Filter '*.zip' -ErrorAction SilentlyContinue).Count -gt $before } 8000
    Check ($null -ne $backup) "지금 백업으로 백업 파일이 하나 더 생김 (전 ${before}개)"
    $theme = Find-ByName $script:window '테마' $CT::ComboBox
    Check ($null -ne $theme) '테마 선택'
    if ($theme) {
        $theme.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 300
        $dark = Wait-Until { Find-ByName $theme '어둡게' $CT::ListItem } 3000
        $dark.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $theme.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        $saved = Wait-Until { (Get-Content "$work/Data/settings.json" -Raw -Encoding utf8) -match '"theme":\s*"Dark"' } 5000
        Check ($null -ne $saved) '어두운 테마가 설정 파일에 저장됨'
        Save-Shot '18-settings-dark'
        Open-Page '오늘' | Out-Null
        Save-Shot '19-today-dark'
        Open-Page '달력' | Out-Null
        Save-Shot '20-calendar-dark'
        Open-Page '메모' | Out-Null
        Save-Shot '21-notes-dark'
    }

    function Find-AppWindow([string] $name, [int] $timeoutMs = 5000) {
        return Wait-Until {
            $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
            $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
            $A::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.AndCondition($pidCond, $nameCond)))
        } $timeoutMs
    }
    function Invoke-Button($root, [string] $name) {
        $button = Wait-Until { Find-ByName $root $name $CT::Button } 3000
        if (-not $button) { throw "'$name' 버튼 없음" }
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 500
    }

    Write-Host "[바탕화면 위젯]"
    Open-Page '설정' | Out-Null
    $widgetBox = Find-ByName $script:window '바탕화면 위젯 표시 (오늘 할 일과 일정)' $CT::CheckBox
    $widgetBox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $widget = Find-AppWindow 'DayDeck 위젯'
    Check ($null -ne $widget) '설정에서 켜면 위젯이 뜸'
    if ($widget) {
        $taskBox = Wait-Until { Find-ByName $widget '영어 단어 30개' $CT::CheckBox } 3000
        Check ($null -ne $taskBox) '위젯에 오늘 할 일'
        Save-Shot '26-widget' $widget
    }

    Write-Host "[스티커 메모]"
    Open-Page '메모' | Out-Null
    $tree = Find-ById $script:window 'FolderTree'
    (Wait-Until { Find-ByName $tree '개인' $CT::TreeItem }).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
    $noteItem = Wait-Until { Find-ByName (Find-ById $script:window 'NoteList') '장보기' $CT::ListItem } 3000
    $noteItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 600
    Invoke-Button $script:window '📌 스티커로'
    $sticky = Find-AppWindow '스티커 메모 - 장보기'
    Check ($null -ne $sticky) '메모를 스티커로 띄움'
    if ($sticky) {
        $stickyText = Find-ByName $sticky '스티커 내용' $CT::Edit
        Check ($stickyText.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -like '우유*') '스티커에 메모 내용'
        $stickyText.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("우유`r`n계란`r`n사과`r`n스티커에서 추가")
        $saved = Wait-Until { (Get-Content "$work/Data/Notes/개인/장보기.txt" -Raw -Encoding utf8) -like '*스티커에서 추가*' } 5000
        Check ($null -ne $saved) '스티커에서 고친 내용이 메모 파일에 저장됨'
        Save-Shot '27-sticky' $sticky
    }

    Write-Host "[반복 할 일 · 태그]"
    $rows = Open-Page '업무'
    $weekly = Wait-Until { Find-Item $rows '주간 업무 보고' } 3000
    Check ($null -ne $weekly) '반복 할 일이 목록에 있음'
    Check (@(Get-Texts $weekly | Where-Object { $_ -like '*↻ 매주*' -and $_ -like '*@보고*' }).Count -ge 1) '행에 반복(↻)과 태그(@) 표시'
    if ($weekly) {
        (Find-ByName $weekly '완료 전환' $CT::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $two = Wait-Until { @(Get-ItemNames (Find-ById $script:window 'Rows') | Where-Object { $_ -eq '주간 업무 보고' }).Count -eq 2 } 4000
        Check ($null -ne $two) '완료하면 다음 회차가 새로 생김 (완료 1 + 미완료 1)'
        Save-Shot '28-recurring'
    }
    $tagRows = Open-Page '보고'
    Check ((Get-ItemNames $tagRows) -contains '주간 업무 보고') '탐색의 태그(@보고)로 모아 보기'

    Write-Host "[완료됨 기간]"
    Open-Page '완료됨' | Out-Null
    $range = Find-ByName $script:window '기간' $CT::ComboBox
    Check ($null -ne $range) '완료됨 화면에 기간 선택'

    Write-Host "[오늘 팝업 --today]"
    Start-Process "$work/DayDeck.exe" -ArgumentList '--today' | Out-Null
    $popup = Find-AppWindow '오늘' 6000
    Check ($null -ne $popup) '--today로 오늘 팝업이 열림'
    if ($popup) {
        $popupTask = Wait-Until { Find-ByName $popup '영어 단어 30개' $CT::CheckBox } 3000
        Check ($null -ne $popupTask) '팝업에 오늘 할 일'
        Save-Shot '29-today-popup' $popup
        $popup.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Start-Sleep -Milliseconds 300
    }

    Write-Host "[음력 표시]"
    Open-Page '설정' | Out-Null
    $lunarBox = Find-ByName $script:window '월 보기에 음력 날짜 표시' $CT::CheckBox
    $lunarBox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $savedLunar = Wait-Until { (Get-Content "$work/Data/settings.json" -Raw -Encoding utf8) -match '"showLunar":\s*true' } 4000
    Check ($null -ne $savedLunar) '음력 표시 설정 저장'
    Open-Page '달력' | Out-Null
    Start-Sleep -Milliseconds 500
    $lunarTexts = @(Get-Texts $script:window | Where-Object { $_ -like '음 *' })
    Check ($lunarTexts.Count -ge 28) "월 보기에 음력 날짜 ($($lunarTexts.Count)칸, 예: $($lunarTexts | Select-Object -First 2))"
    Save-Shot '30-calendar-lunar'

    Write-Host "[글꼴 · 글자 크기]"
    $seeded = Get-Content "$work/Data/settings.json" -Raw -Encoding utf8
    Check ($seeded -match '"fontSize":\s*16') '기본 글자 크기 16pt'
    Open-Page '설정' | Out-Null
    $sizeBox = Find-ByName $script:window '글자 크기' $CT::ComboBox
    Check ($null -ne $sizeBox) '글자 크기 선택'
    if ($sizeBox) {
        $sizeBox.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $big = Wait-Until { Find-ByName $sizeBox '18pt' $CT::ListItem } 3000
        $big.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $sizeBox.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        $savedSize = Wait-Until { (Get-Content "$work/Data/settings.json" -Raw -Encoding utf8) -match '"fontSize":\s*18' } 4000
        Check ($null -ne $savedSize) '글자 크기 18pt가 설정 파일에 저장됨'
        Open-Page '오늘' | Out-Null
        Save-Shot '31-font-18pt'
        Open-Page '설정' | Out-Null
        $sizeBox = Find-ByName $script:window '글자 크기' $CT::ComboBox
        $sizeBox.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        (Wait-Until { Find-ByName $sizeBox '16pt (기본)' $CT::ListItem } 3000).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $sizeBox.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    }
    Check ($null -ne (Find-ByName $script:window '글꼴' $CT::ComboBox)) '글꼴 선택'
    Check ($null -ne (Find-ByName $script:window '메모 글꼴' $CT::ComboBox)) '메모 편집기 글꼴 선택'

    Write-Host "[단축키 켜기/끄기]"
    $newNoteToggle = Find-ByName $script:window '새 메모 단축키 사용' $CT::CheckBox
    $formatToggle = Find-ByName $script:window '서식 단축키 사용 (Ctrl+B 굵게, Ctrl+I 기울임, Ctrl+U 밑줄, Ctrl+K 링크 …)' $CT::CheckBox
    Check ($null -ne $newNoteToggle) '전역 단축키마다 사용 체크'
    Check ($null -ne $formatToggle) '서식 단축키 사용 체크'
    if ($newNoteToggle -and $formatToggle) {
        $newNoteToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        $formatToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        $off = Wait-Until { $j = Get-Content "$work/Data/settings.json" -Raw -Encoding utf8; if ($j -match '"newNoteEnabled":\s*false' -and $j -match '"formatShortcuts":\s*false') { $true } } 4000
        Check ($null -ne $off) '끈 단축키가 설정에 저장됨'
        $newNoteToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        $formatToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        $on = Wait-Until { $j = Get-Content "$work/Data/settings.json" -Raw -Encoding utf8; if ($j -match '"newNoteEnabled":\s*true' -and $j -match '"formatShortcuts":\s*true') { $true } } 4000
        Check ($null -ne $on) '다시 켜면 설정에 저장됨'
    }

    Write-Host "[메모 서식: 굵게 · 링크 · 미리보기]"
    Open-Page '메모' | Out-Null
    Invoke-Button $script:window '새 메모'
    (Find-ByName $script:window '편집 보기' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    # 탭마다 편집기가 있으므로 화면에 보이는 것(새 메모)을 고른다.
    $editor = Wait-Until {
        $idCond = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'NoteEditor')
        @($script:window.FindAll($Scope::Descendants, $idCond)) | Where-Object { -not $_.Current.IsOffscreen -and $_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '' } | Select-Object -First 1
    } 4000
    if ($editor) {
        $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("서식 테스트`r`n굵게 만들 글자`r`n링크 자리")
        Start-Sleep -Milliseconds 300
        $textPattern = $editor.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $textPattern.DocumentRange.FindText('굵게 만들 글자', $false, $false).Select()
        Invoke-Button $script:window '굵게'
        $textPattern.DocumentRange.FindText('링크 자리', $false, $false).Select()
        Invoke-Button $script:window '링크'
        $linkDialog = Wait-Until {
            $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
            $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, '링크 넣기')
            $A::RootElement.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.AndCondition($pidCond, $nameCond)))
        } 4000
        Check ($null -ne $linkDialog) '링크 넣기 창'
        if ($linkDialog) {
            (Find-ByName $linkDialog '링크 주소' $CT::Edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('www.example.com')
            Invoke-Button $linkDialog '넣기'
        }
        $file = Wait-Until { Get-ChildItem "$work/Data/Notes" -Recurse -File -Filter '서식 테스트.md' | Where-Object { $_.FullName -notmatch '[\\/]\.' } | Select-Object -First 1 } 8000
        $content = if ($file) { Wait-Until { $c = Get-Content $file.FullName -Raw -Encoding utf8; if ($c -like '*[[]링크 자리](https://www.example.com)*') { $c } } 8000 }
        Write-Host "  파일: $($file.Name) / $content"
        Check ($content -like '*`*`*굵게 만들 글자`*`**') '굵게가 마크다운(**)으로 저장됨'
        Check ($content -like '*[[]링크 자리](https://www.example.com)*') '링크가 [글자](주소)로 저장되고 www.는 https://로 바뀜'
        $split = Find-ByName $script:window '나란히 보기' $CT::RadioButton
        $split.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $preview = Wait-Until { Find-ById $script:window 'NotePreview' } 3000
        Check ($null -ne $preview) '나란히 보기에 서식 미리보기'
        Save-Shot '32-note-format'
        (Find-ByName $script:window '편집 보기' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    else {
        Check $false '새 메모 편집기'
    }

    Write-Host "[메모 그림 붙여 넣기 · 버전 기록]"
    if ($editor -and $file) {
        # 클립보드에 그림만 넣는다 (클립보드는 STA가 필요해 별도 프로세스로; 복사본이라 프로세스가 끝나도 남는다).
        powershell.exe -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms, System.Drawing; `$b = New-Object System.Drawing.Bitmap 120, 60; `$g = [System.Drawing.Graphics]::FromImage(`$b); `$g.Clear([System.Drawing.Color]::SteelBlue); [System.Windows.Forms.Clipboard]::SetImage(`$b)"
        # 키 입력은 앱 창이 앞에 있어야 한다 (Ctrl+K 단계와 같은 방법).
        $show = Start-Process "$work/DayDeck.exe" -PassThru
        $show.WaitForExit(10000) | Out-Null
        Start-Sleep -Milliseconds 600
        $editor.SetFocus()
        Start-Sleep -Milliseconds 300
        Send-Chord @(0x11, 0x56)   # Ctrl+V
        $withImage = Wait-Until { $c = Get-Content $file.FullName -Raw -Encoding utf8; if ($c -match '!\[\]\(\.images/[^)]+\.png\)') { $c } } 8000
        Check ($null -ne $withImage) 'Ctrl+V로 클립보드 그림이 ![](.images/….png)로 들어감'
        $png = @(Get-ChildItem (Join-Path $file.DirectoryName '.images') -Filter '*.png' -ErrorAction SilentlyContinue)
        Check ($png.Count -ge 1) "그림 파일이 메모 폴더의 .images에 저장됨 ($($png.Name))"
        if ($withImage) {
            $split = Find-ByName $script:window '나란히 보기' $CT::RadioButton
            $split.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            $picture = Wait-Until { $pv = Find-ById $script:window 'NotePreview'; if ($pv) { Find-ByName $pv '그림' $CT::Image } } 4000
            Check ($null -ne $picture) '미리보기에 그림이 보임'
            Save-Shot '34-note-image'
            (Find-ByName $script:window '편집 보기' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()

            # 그림을 넣기 전 내용이 기록에 남았으니 되돌려 본다.
            Invoke-Button $script:window '메모 기록'
            $history = Wait-Until {
                $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
                $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, '메모 기록')
                $typeCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window)
                $conditions = [System.Windows.Automation.Condition[]]@($pidCond, $nameCond, $typeCond)
                $A::RootElement.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.AndCondition(, $conditions)))
            } 4000
            Check ($null -ne $history) '메모 기록 창'
            if ($history) {
                $versionList = Find-ByName $history '버전 목록' $CT::List
                $versions = if ($versionList) { @(Get-AllItems $versionList) } else { @() }
                Check ($versions.Count -ge 1) "예전 버전이 남아 있음 ($($versions.Count)개)"
                Save-Shot '35-note-history' $history
                Invoke-Button $history '이 버전으로 되돌리기'
                $restored = Wait-Until { $c = Get-Content $file.FullName -Raw -Encoding utf8; if ($c -notmatch '!\[\]\(' -and $c -like '*링크 자리*') { $c } } 6000
                Check ($null -ne $restored) '되돌리면 그림을 넣기 전 내용으로 돌아감'
            }
        }
    }

    Write-Host "[메모 보기에서 바로 고치기]"
    Open-Page '메모' | Out-Null
    # 앞 단계에서 직접 "편집"을 골라 두었으므로 (같은 화면에서는 고른 방식이 유지된다) "보기"로 돌아온다.
    (Find-ByName $script:window '보기 모드' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Button $script:window '새 메모'
    # 복원된 탭의 숨은 보기 편집기도 트리에 남아 있을 수 있으므로 보이는 것을 고른다.
    $rich = Wait-Until {
        $idCond = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'NoteRichEditor')
        @($script:window.FindAll($Scope::Descendants, $idCond)) | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 } | Select-Object -Last 1
    } 5000
    Check ($null -ne $rich) '새 메모가 보기 편집기로 열림'
    if ($rich) {
        # 키 입력은 앱 창이 앞에 있어야 한다.
        $show = Start-Process "$work/DayDeck.exe" -PassThru
        $show.WaitForExit(10000) | Out-Null
        Start-Sleep -Milliseconds 600
        $rich.SetFocus()
        Start-Sleep -Milliseconds 300
        # "# " 제목, "- " 목록(Enter로 다음 항목, 빈 항목에서 Enter로 끝), Ctrl+B 굵게, "[] " 체크 목록
        $enter = 0x0D
        Type-Text '# Rich note'; Send-Chord @($enter)
        Type-Text '- first item'; Send-Chord @($enter)
        Type-Text 'second item'; Send-Chord @($enter); Send-Chord @($enter)
        Type-Text 'plain '; Send-Chord @(0x11, 0x42); Type-Text 'bold'; Send-Chord @(0x11, 0x42); Type-Text ' end'; Send-Chord @($enter)
        Type-Text '[] task one'
        $expected = "# Rich note`n- first item`n- second item`nplain **bold** end`n- [ ] task one"
        $richFile = Wait-Until { Get-ChildItem "$work/Data/Notes" -Recurse -File -Filter 'Rich note.md' | Where-Object { $_.FullName -notmatch '[\\/]\.' } | Select-Object -First 1 } 10000
        $richContent = if ($richFile) { Wait-Until { $c = (Get-Content $richFile.FullName -Raw -Encoding utf8) -replace "`r`n", "`n"; if ($c -eq $expected) { $c } } 10000 }
        if (-not $richContent) {
            # 진단: 포커스, 보기 편집기 글자, 이 단계에서 저장된 메모 파일
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            Write-Host "  포커스: $($focused.Current.ControlType.ProgrammaticName) '$($focused.Current.Name)' id=$($focused.Current.AutomationId)"
            try { Write-Host "  편집기 글자: $(($rich.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(500)) -replace "`r?`n", ' / ')" } catch { Write-Host "  편집기 글자를 읽을 수 없음: $_" }
            Get-ChildItem "$work/Data/Notes" -Recurse -File -Filter '*.md' | Where-Object { $_.FullName -notmatch '[\\/]\.' -and $_.LastWriteTime -gt (Get-Date).AddMinutes(-1) } | ForEach-Object {
                Write-Host "  저장된 메모 $($_.Name): $((Get-Content $_.FullName -Raw -Encoding utf8) -replace "`r?`n", ' / ')"
            }
        }
        Check ($null -ne $richContent) '보기에서 친 제목·목록·굵게·체크가 마크다운으로 저장됨'
        Save-Shot '36-note-rich-edit'
        (Find-ByName $script:window '편집 보기' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $raw = Wait-Until { Find-ById $script:window 'NoteEditor' } 3000
        $rawValue = if ($raw) { $raw.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -replace "`r`n", "`n" }
        Check ($rawValue -eq $expected) '편집(원문)으로 바꾸면 같은 마크다운'
        (Find-ByName $script:window '보기 모드' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }

    Write-Host "[워크플로우: Excalidraw 샘플 그리기]"
    Open-Page '워크플로우' | Out-Null
    $drawingList = Wait-Until { Find-ByName $script:window '워크플로우 목록' } 5000
    Check ($null -ne $drawingList) '워크플로우 목록'
    if ($drawingList) {
        # 목록은 화면을 연 뒤 채워지므로 첫 샘플이 보일 때까지 기다린다.
        $firstSample = (Get-ChildItem $drawingSamples -Filter '*.excalidraw' | Sort-Object Name | Select-Object -First 1).BaseName
        Wait-Until { Find-ByName $drawingList $firstSample $CT::ListItem } 15000 | Out-Null
        foreach ($sample in (Get-ChildItem $drawingSamples -Filter '*.excalidraw' | Sort-Object Name)) {
            $item = Wait-Until { Find-ByName $drawingList $sample.BaseName $CT::ListItem } 3000
            if (-not $item) { Check $false "샘플 $($sample.BaseName)이 목록에 있음"; continue }
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 700
            $fit = Find-ByName $script:window '전체 보기'
            if ($fit) { $fit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 400 }
            Save-Shot "wf-$($sample.BaseName)"
        }
        Check $true '워크플로우 샘플 스크린샷'
        # 바탕색: 진짜 마우스로 단추를 누르면 바탕색 줄이 펼쳐지고, 색을 고르면 파일의 viewBackgroundColor로 저장된다.
        $bgButton = Find-ByName $script:window '바탕색' $CT::Button
        Check ($null -ne $bgButton) '바탕색 단추'
        if ($bgButton) {
            $show = Start-Process "$work/DayDeck.exe" -PassThru
            $show.WaitForExit(10000) | Out-Null
            Start-Sleep -Milliseconds 600
            $r = $bgButton.Current.BoundingRectangle
            [Native.Keyboard]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null; Start-Sleep -Milliseconds 150
            [Native.Keyboard]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
            [Native.Keyboard]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
            # 바탕색 줄의 닫기 단추가 보이면 펼쳐진 것 (Border는 UI 자동화에 보이지 않음).
            $bgClose = Wait-Until { $b = Find-ByName $script:window '바탕색 닫기' $CT::Button; if ($b -and -not $b.Current.IsOffscreen) { $b } } 3000
            Check ($null -ne $bgClose) '바탕색 단추를 마우스로 누르면 바탕색 줄이 펼쳐짐'
            $bgPanel = $script:window
            if ($bgClose) {
                $yellow = Find-ByName $bgPanel '밝은 노랑' $CT::Button
                if ($yellow) {
                    $r = $yellow.Current.BoundingRectangle
                    [Native.Keyboard]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null; Start-Sleep -Milliseconds 150
                    [Native.Keyboard]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
                    [Native.Keyboard]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
                }
                $lastSample = (Get-ChildItem $drawingSamples -Filter '*.excalidraw' | Sort-Object Name | Select-Object -Last 1).Name
                $saved = Wait-Until { $c = Get-Content "$work/Data/Drawings/$lastSample" -Raw -Encoding utf8; if ($c -match '"viewBackgroundColor":\s*"#fffce8"') { $c } } 6000
                Check ($null -ne $saved) '바탕색을 고르면 그림 파일에 저장됨 (#fffce8)'
                Save-Shot 'wf-canvas-background'
                $bgClose.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            }
        }
    }
    # 다음 단계(메모)를 위해 메모 화면으로 돌아간다.
    Open-Page '메모' | Out-Null

    Write-Host "[메모 옵시디언 모드]"
    (Find-ByName $script:window '옵시디언 모드' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Button $script:window '새 메모'
    $live = Wait-Until {
        $idCond = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'NoteLiveEditor')
        @($script:window.FindAll($Scope::Descendants, $idCond)) | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 } | Select-Object -Last 1
    } 5000
    Check ($null -ne $live) '새 메모가 옵시디언 편집기로 열림'
    if ($live) {
        $show = Start-Process "$work/DayDeck.exe" -PassThru
        $show.WaitForExit(10000) | Out-Null
        Start-Sleep -Milliseconds 600
        $box = $live.Current.BoundingRectangle
        [Native.Keyboard]::SetCursorPos([int]($box.X + 80), [int]($box.Y + 30)) | Out-Null; Start-Sleep -Milliseconds 150
        [Native.Keyboard]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [Native.Keyboard]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 300
        # 목록은 Enter로 이어지고 빈 항목에서 Enter면 끝난다. Ctrl+B는 **로 감싼다. 원문 그대로 저장된다.
        $enter = 0x0D
        Type-Text '# Live note'; Send-Chord @($enter)
        Type-Text '- one'; Send-Chord @($enter)
        Type-Text 'two'; Send-Chord @($enter); Send-Chord @($enter)
        Type-Text '- [ ] task'; Send-Chord @($enter); Send-Chord @($enter)
        Type-Text 'plain '; Send-Chord @(0x11, 0x42); Type-Text 'bold'; Send-Chord @(0x11, 0x42)
        $liveExpected = "# Live note`n- one`n- two`n- [ ] task`nplain **bold**"
        $liveFile = Wait-Until { Get-ChildItem "$work/Data/Notes" -Recurse -File -Filter 'Live note.md' | Where-Object { $_.FullName -notmatch '[\\/]\.' } | Select-Object -First 1 } 10000
        $liveContent = if ($liveFile) { Wait-Until { $c = (Get-Content $liveFile.FullName -Raw -Encoding utf8) -replace "`r`n", "`n"; if ($c -eq $liveExpected) { $c } } 10000 }
        if (-not $liveContent -and $liveFile) { Write-Host "  저장된 내용: $((Get-Content $liveFile.FullName -Raw -Encoding utf8) -replace "`r?`n", ' / ')" }
        Check ($null -ne $liveContent) '옵시디언 모드에서 친 제목·목록·체크·굵게가 원문 그대로 저장됨'
        # 커서를 첫 줄로 옮기면 다른 줄은 기호가 숨고 체크 상자가 보인다 (스크린샷으로 확인).
        Send-Chord @(0x11, 0x24)
        Start-Sleep -Milliseconds 400
        Save-Shot '37-note-obsidian'
        (Find-ByName $script:window '보기 모드' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }

    Write-Host "[메모 보기에서 표 칸 고치기]"
    Invoke-Button $script:window '새 메모'
    (Find-ByName $script:window '편집 보기' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $tableRaw = Wait-Until { Find-ById $script:window 'NoteEditor' } 3000
    $tableBefore = "# Table note`n`n| Name | Qty |`n| --- | ---: |`n| apple | 3 |`n| pear | 5 |"
    $tableRaw.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($tableBefore)
    (Find-ByName $script:window '보기 모드' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $tableFile = Wait-Until { Get-ChildItem "$work/Data/Notes" -Recurse -File -Filter 'Table note.md' | Where-Object { $_.FullName -notmatch '[\\/]\.' } | Select-Object -First 1 } 10000
    function Read-TableNote { if ($tableFile) { (Get-Content $tableFile.FullName -Raw -Encoding utf8) -replace "`r`n", "`n" } }
    $tableRich = Wait-Until {
        $idCond = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'NoteRichEditor')
        @($script:window.FindAll($Scope::Descendants, $idCond)) | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 } | Select-Object -Last 1
    } 5000
    $headerCell = if ($tableRich) { Wait-Until { Find-ByName $tableRich 'Name' $CT::Text } 5000 }
    Check ($null -ne $headerCell) '보기에서 표 칸 글자가 보임'
    if ($headerCell) {
        # 칸을 누르는 마우스·키 입력은 앱 창이 앞에 있어야 한다.
        $show = Start-Process "$work/DayDeck.exe" -PassThru
        $show.WaitForExit(10000) | Out-Null
        Start-Sleep -Milliseconds 600
        $r = (Find-ByName $tableRich 'Name' $CT::Text).Current.BoundingRectangle
        $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
        # 정시 알림 창은 항상 위에 떠 있다: 누를 자리를 덮으면 알림 단추를 누르게 되므로 잠시 왼쪽 위로 옮긴다.
        $reminder = Find-ReminderWindow 300
        $reminderRect = if ($reminder) { $reminder.Current.BoundingRectangle }
        $reminderHandle = if ($reminder) { $reminder.Current.NativeWindowHandle }
        $reminderMoved = $reminderHandle -and $reminderRect.Contains($x, $y)
        if ($reminderMoved) {
            [Native.Keyboard]::SetWindowPos([IntPtr]$reminderHandle, [IntPtr]::Zero, 0, 0, 0, 0, 0x15) | Out-Null
            Start-Sleep -Milliseconds 300
        }
        [Native.Keyboard]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 150
        [Native.Keyboard]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [Native.Keyboard]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        # 글 상자는 문서 안 요소라 UI 자동화 트리에 늦게 보일 수 있으므로 키보드 포커스로 찾는다.
        $cellBox = Wait-Until { $f = $A::FocusedElement; if ($f -and $f.Current.AutomationId -eq 'TableCellEditor') { $f } } 3000
        if (-not $cellBox) {
            $f = $A::FocusedElement
            Write-Host "  누른 곳 ($x, $y), 칸 $r, 알림 창 $reminderRect (옮김: $reminderMoved), 포커스: $($f.Current.ControlType.ProgrammaticName) '$($f.Current.Name)' id=$($f.Current.AutomationId)"
        }
        Check ($null -ne $cellBox) '표 칸을 누르면 그 칸을 고치는 글 상자가 열림'
        if ($cellBox) {
            Check ($cellBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'Name') '글 상자에 칸 내용 (Name)'
            # Enter 아래 칸(apple) → Tab 오른쪽 칸(3) → Enter 아래 칸(5) → 마지막 줄에서 Enter로 표 밖으로.
            # 키보드로 옮겨 간 칸은 글자가 모두 골라져 있어 치면 바뀐다.
            Send-Chord @(0x0D); Type-Text 'plum'
            Send-Chord @(0x09); Type-Text '42'
            Send-Chord @(0x0D); Type-Text '7'
            Save-Shot '37-note-table-cell-edit'
            Send-Chord @(0x0D)
            $tableAfter = "# Table note`n`n| Name | Qty |`n| --- | ---: |`n| plum | 42 |`n| pear | 7 |"
            $saved = Wait-Until { $c = Read-TableNote; if ($c -eq $tableAfter) { $c } } 10000
            if (-not $saved) { Write-Host "  저장된 표 메모: $((Read-TableNote) -replace "`n", ' / ')" }
            Check ($null -ne $saved) '보기에서 고친 칸(Tab·Enter로 이동)이 마크다운 표로 저장됨'
            Check ($A::FocusedElement.Current.AutomationId -ne 'TableCellEditor') '마지막 줄에서 Enter를 누르면 칸 고치기가 끝남'
            Send-Chord @(0x11, 0x5A)
            $undone = Wait-Until { $c = Read-TableNote; if ($c -eq $tableBefore) { $c } } 10000
            if (-not $undone) { Write-Host "  되돌린 표 메모: $((Read-TableNote) -replace "`n", ' / ')" }
            Check ($null -ne $undone) 'Ctrl+Z 한 번에 표 고치기 전으로 돌아감'
        }
        if ($reminderMoved) {
            [Native.Keyboard]::SetWindowPos([IntPtr]$reminderHandle, [IntPtr]::Zero, [int]$reminderRect.X, [int]$reminderRect.Y, 0, 0, 0x15) | Out-Null
        }
    }

    Write-Host "[할 일 메모: 보기에서 바로 고치기 · 원문]"
    $rows = Open-Page '오늘'
    Select-Row $rows '영어 단어 30개' | Out-Null
    $richMemo = Wait-Until { Find-ById $script:window 'TaskNotesRich' } 3000
    Check ($null -ne $richMemo) '할 일 메모가 오른쪽 상세에서 보기 편집기로 열림'
    $rawToggle = Find-ByName $script:window '메모 원문' $CT::Button
    Check ($null -ne $rawToggle) '메모 원문 단추'
    if ($rawToggle) {
        $rawToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        $memo = Wait-Until { Find-ByName $script:window '할 일 메모' $CT::Edit } 3000
        Check ($null -ne $memo) '원문을 누르면 마크다운 입력란'
        if ($memo) {
            $memo.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("단어장 챙기기`r`n발음 연습")
            Start-Sleep -Milliseconds 300
            $memo.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.FindText('단어장', $false, $false).Select()
            Invoke-Button $script:window '메모 굵게'
            $value = $memo.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            Check ($value -like '`*`*단어장`*`* 챙기기*') "메모 굵게 → **단어장** ($($value -replace "`r?`n", ' / '))"
            Invoke-Button $script:window '메모 체크 목록'
            $marked = $memo.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            # 다시 누르면 보기 편집기로 돌아온다. (보기 편집기는 내용을 새 문서로 바꿔 넣는데, UI 자동화의 글자 읽기는
            # 처음 문서를 계속 보는 일이 있어 글자를 직접 비교하지 않고, 원문으로 한 번 더 바꿔 내용이 그대로인지 본다.)
            $rawToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            $backToRich = Wait-Until { -not (Find-ByName $script:window '할 일 메모' $CT::Edit) } 3000
            Check ($null -ne $backToRich -and $null -ne (Find-ById $script:window 'TaskNotesRich')) '다시 누르면 보기 편집기로 돌아옴'
            Save-Shot '33-task-memo-format'
            $rawToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            $memo = Wait-Until { Find-ByName $script:window '할 일 메모' $CT::Edit } 3000
            $again = if ($memo) { $memo.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } else { '' }
            Check (($again -replace "`r", '') -eq ($marked -replace "`r", '') -and $again -like '*`*`*단어장`*`* 챙기기*') "보기를 거쳐도 메모 내용 유지 ($($again -replace "`r?`n", ' / '))"
            $rawToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        }
    }

    Write-Host "[알림 팝업: 정시 알림]"
    $popup = Find-ReminderWindow 150000
    Check ($null -ne $popup) '실행 중 정시 알림이 뜸'
    if ($popup) {
        Check ($null -ne (Find-ByName $popup '알림: 치과 예약' $CT::Group)) '치과 예약 알림 카드'
        Save-Shot '09-reminder-due' $popup
        Click-InGroup $popup '알림: 치과 예약' '✓ 완료'
        $names = Get-ItemNames (Open-Page '완료됨')
        Check ($names -contains '치과 예약') '알림의 완료 버튼으로 할 일이 완료됨'
    }

    Write-Host "[구역 너비 끌어서 조절]"
    $nav = Find-ById $script:window 'Nav'
    $splitter = Find-ById $script:window 'NavSplitter'
    Check ($null -ne $splitter) '탐색 메뉴 경계선'
    $navBefore = $nav.Current.BoundingRectangle.Width
    if ($splitter) {
        $r = $splitter.Current.BoundingRectangle
        $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
        [Native.Keyboard]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 150
        [Native.Keyboard]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # 왼쪽 단추 누름
        foreach ($step in 1..8) { [Native.Keyboard]::SetCursorPos($x + $step * 10, $y); Start-Sleep -Milliseconds 40 }
        [Native.Keyboard]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # 놓음
        Start-Sleep -Milliseconds 400
    }
    $navAfter = $nav.Current.BoundingRectangle.Width
    Write-Host ("  탐색 메뉴 너비: {0:0} → {1:0}" -f $navBefore, $navAfter)
    Check ($navAfter -gt $navBefore + 40) '경계선을 끌면 탐색 메뉴가 넓어짐'
    Save-Shot '34-resized-nav'
    $windowBefore = $script:window.Current.BoundingRectangle

    Write-Host "[창 닫기 → 트레이]"
    $script:window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Start-Sleep -Seconds 4
    Check (-not $proc.HasExited) '창을 닫아도 트레이에서 계속 실행'
    $proc.Refresh()
    Write-Host ("  트레이 상주 Working Set: {0:0.0} MB" -f ($proc.WorkingSet64 / 1MB))

    Write-Host "[다시 열기 → 메모 탭 복원]"
    $again = Start-Process "$work/DayDeck.exe" -PassThru
    $again.WaitForExit(10000) | Out-Null
    $script:window = Wait-Until {
        $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
        $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'DayDeck')
        $A::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.AndCondition($pidCond, $nameCond)))
    } 10000
    Check ($null -ne $script:window) '두 번째 실행이 기존 창을 다시 엶'
    if ($script:window) {
        Open-Page '메모' | Out-Null
        Start-Sleep -Milliseconds 800
        Check (@(Get-Texts $script:window) -contains 'UI 테스트 메모.md') '다시 열면 메모 탭이 복원됨'
        $navReopened = (Find-ById $script:window 'Nav').Current.BoundingRectangle.Width
        Check ([math]::Abs($navReopened - $navAfter) -le 3) ("다시 열어도 끌어서 바꾼 탐색 메뉴 너비 유지 ({0:0})" -f $navReopened)
        $windowAfter = $script:window.Current.BoundingRectangle
        Check ([math]::Abs($windowAfter.Width - $windowBefore.Width) -le 2 -and [math]::Abs($windowAfter.Height - $windowBefore.Height) -le 2) `
            ("다시 열어도 창 크기 유지 ({0:0}x{1:0})" -f $windowAfter.Width, $windowAfter.Height)
        $script:window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Start-Sleep -Seconds 2
    }

    $log = Get-Content "$work/Data/Logs/app.log" -Raw -Encoding utf8
    Check ($log -notmatch ' ERROR ') '로그에 오류 없음'
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    if (Test-Path "$work/Data/Logs/app.log") {
        Write-Host '--- app.log ---'
        Get-Content "$work/Data/Logs/app.log" -Encoding utf8 | Write-Host
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    throw "UI 테스트 실패 $($failures.Count)건:`n - " + ($failures -join "`n - ")
}
Write-Host "UI 테스트 통과"
