# Microsoft Store 제출용 MSIX 패키지를 만든다 (Windows에서 실행, Windows SDK의 makeappx·makepri 필요).
#
#   ./scripts/build-msix.ps1                 # 게시 → artifacts/msix/DayDeck-<버전>-x64.msix 와 .msixupload
#   ./scripts/build-msix.ps1 -TestCert       # 위 + 테스트 인증서로 서명한 -test.msix 와 .cer (내 PC에 직접 설치해 보기용)
#
# 패키지 ID(이름·게시자)는 packaging/msix/identity.json, 버전은 Directory.Build.props의 <Version>에 .0을 붙여 쓴다
# (Store는 네 번째 자리가 0이어야 한다). Partner Center에 올리는 파일은 서명하지 않은 .msixupload다 (Store가 서명한다).
param(
    [string] $PublishDir = '',
    [string] $OutDir = 'artifacts/msix',
    [switch] $TestCert
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Find-SdkTool([string] $name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem $kits -Filter $name -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' -and $_.Directory.Parent.Name -match '^10\.' } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if (-not $tool) { throw "$name 을(를) 찾지 못했습니다. Windows SDK를 설치하세요." }
    return $tool.FullName
}

$identity = Get-Content 'packaging/msix/identity.json' -Raw -Encoding utf8 | ConvertFrom-Json
$version = ([xml](Get-Content 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$parts = @($version.Split('.')) + @('0', '0', '0')
$packageVersion = "$($parts[0]).$($parts[1]).$($parts[2]).0"
Write-Host "패키지 $($identity.identityName) $packageVersion ($($identity.publisher))"

if (-not $PublishDir) {
    # 단일 exe가 아니라 폴더로 게시한다: MSIX는 설치 때 압축을 풀어 두므로 실행할 때마다 파일을 꺼낼 필요가 없다.
    $PublishDir = 'artifacts/publish/msix-app'
    dotnet publish src/DayDeck.App -c Release -r win-x64 --self-contained `
        -p:StoreBuild=true -p:PublishReadyToRun=true -p:DebugType=none -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 실패' }
}

$layout = 'artifacts/msix-layout'
Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $layout | Out-Null
Copy-Item "$PublishDir/*" $layout -Recurse
Copy-Item 'packaging/msix/Assets' "$layout/Assets" -Recurse

$manifest = Get-Content 'packaging/msix/AppxManifest.xml' -Raw -Encoding utf8
$values = @{
    IdentityName = $identity.identityName
    Publisher = $identity.publisher
    PublisherDisplayName = $identity.publisherDisplayName
    DisplayName = $identity.displayName
    Version = $packageVersion
}
foreach ($key in $values.Keys) {
    $manifest = $manifest.Replace("{{$key}}", [Security.SecurityElement]::Escape($values[$key]))
}
if ($manifest -match '\{\{\w+\}\}') { throw "매니페스트에 채우지 않은 값이 있습니다: $($Matches[0])" }
[IO.File]::WriteAllText((Join-Path $root "$layout/AppxManifest.xml"), $manifest, [Text.UTF8Encoding]::new($false))

# 크기별 로고(scale-200, targetsize-48 …)를 Windows가 고를 수 있도록 resources.pri를 만든다.
$makepri = Find-SdkTool 'makepri.exe'
$priConfig = 'artifacts/priconfig.xml'
& $makepri createconfig /cf $priConfig /dq ko-KR /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri createconfig 실패' }
& $makepri new /pr $layout /cf $priConfig /mn "$layout/AppxManifest.xml" /of "$layout/resources.pri" /o
if ($LASTEXITCODE -ne 0) { throw 'makepri new 실패' }

New-Item -ItemType Directory $OutDir -Force | Out-Null
$name = "DayDeck-$version-x64"
$msix = Join-Path $OutDir "$name.msix"
$makeappx = Find-SdkTool 'makeappx.exe'
& $makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack 실패' }

# Partner Center에 올리는 파일: .msix를 zip으로 묶고 확장자를 .msixupload로 바꾼 것.
$upload = Join-Path $OutDir "$name.msixupload"
$zip = Join-Path $OutDir "$name.zip"
Remove-Item $upload, $zip -ErrorAction SilentlyContinue
Compress-Archive -Path $msix -DestinationPath $zip
Move-Item $zip $upload

if ($TestCert) {
    # 내 PC에서 설치해 보기용. 인증서 주체가 매니페스트의 Publisher와 같아야 설치된다.
    $cert = New-SelfSignedCertificate -Type Custom -Subject $identity.publisher -KeyUsage DigitalSignature `
        -FriendlyName 'DayDeck MSIX test' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    try {
        $cer = Join-Path $OutDir "$name-test.cer"
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        $testMsix = Join-Path $OutDir "$name-test.msix"
        Copy-Item $msix $testMsix -Force
        $signtool = Find-SdkTool 'signtool.exe'
        & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $testMsix
        if ($LASTEXITCODE -ne 0) { throw 'signtool sign 실패' }
    }
    finally {
        Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
    }
}

Get-ChildItem $OutDir | ForEach-Object { Write-Host ("{0,-50} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
