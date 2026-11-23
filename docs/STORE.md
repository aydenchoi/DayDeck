# Microsoft Store 등록

Store 이름은 **DayDeck**으로 예약했습니다. 창 제목·트레이·알림에 보이는 이름도 DayDeck입니다.
실행 파일(`DayDeck.exe`), 프로젝트·네임스페이스, 데이터 폴더 등 내부 이름도 모두 DayDeck입니다.

DayDeck은 MSIX 패키지로 Microsoft Store에 올립니다. Store가 패키지에 서명하고 업데이트를 배포하므로
코드 서명 인증서는 필요 없습니다. 지금까지처럼 zip(단독 실행형 exe)도 함께 배포합니다.

## 무엇이 만들어지나

CI(`build` 워크플로)의 `DayDeck-msix` 결과물:

| 파일 | 용도 |
|---|---|
| `DayDeck-<버전>-x64.msixupload` | **Partner Center에 올리는 파일** (서명 안 함, Store가 서명) |
| `DayDeck-<버전>-x64.msix` | 위 파일 안에 든 패키지 |
| `DayDeck-<버전>-x64-test.msix`, `-test.cer` | 내 PC에 직접 설치해 보기용 (테스트 인증서로 서명) |

Windows에서 직접 만들려면 (Windows SDK 필요): `./scripts/build-msix.ps1 -TestCert`

## Store 판에서 달라지는 점

앱이 실행될 때 MSIX 패키지인지 스스로 알아내서(`PackageInfo`) 다음처럼 동작합니다. zip 판은 그대로입니다.

| | zip 판 | Store 판 |
|---|---|---|
| 업데이트 | 앱이 GitHub 릴리스를 확인해 묻고 exe를 바꿈 | **Store가 업데이트.** 앱의 업데이트 확인·설치 기능과 트레이 "업데이트 확인"을 숨김 (Store 정책) |
| 로그인 자동 실행 | 레지스트리 `HKCU\...\Run` | 매니페스트의 `StartupTask` (작업 관리자 → 시작 앱에도 보임) |
| 데이터 폴더 | exe 옆 `Data` (안 되면 `%APPDATA%\DayDeck`) | 기본은 `%LOCALAPPDATA%\Packages\<패키지 패밀리 이름>\LocalState`. 앱을 제거하면 함께 지워짐. 설정 → 데이터 → **위치 변경…**으로 다른 폴더에 두면 제거해도 남음 (AppData 아래는 고를 수 없음) |
| 명령줄 | `DayDeck.exe --quick-add` | 같음 (실행 별칭 `DayDeck.exe` 등록) |

Store 판은 `-p:StoreBuild=true`로 빌드합니다. StartupTask 같은 Windows API를 쓰려고 대상 프레임워크가
`net10.0-windows10.0.19041.0`으로 바뀌고 `STORE_BUILD` 기호가 정의됩니다.

## 등록 순서

1. **개발자 계정**: [Partner Center](https://partner.microsoft.com/dashboard)에서 Windows 개발자 계정을 만든다 (개인/회사).
2. **앱 이름 예약**: 앱 및 게임 → 새 제품 → MSIX 또는 PWA 앱 → 이름 예약.
3. **패키지 ID 넣기**: 제품 → 제품 관리 → **제품 ID(Product identity)** 페이지의 값을 `packaging/msix/identity.json`에 넣는다.

   | identity.json | Partner Center |
   |---|---|
   | `identityName` | Package/Identity/Name (예: `12345Name.DayDeck`) |
   | `publisher` | Package/Identity/Publisher (예: `CN=XXXXXXXX-XXXX-...`) |
   | `publisherDisplayName` | Package/Properties/PublisherDisplayName |
   | `displayName` | 예약한 앱 이름 (**DayDeck**, 넣어 둠) |

   지금 넣어 둔 값: `490D7318.DayDeck`, `CN=1602D1F2-D310-446D-9496-08FDC1449E0D`, `에이든초이`.
4. **버전**: `Directory.Build.props`의 `<Version>`(예: 0.8.3)이 패키지 버전 `0.8.3.0`이 된다. 제출할 때마다 이전보다 커야 한다.
5. **제출**: 새 제출 → 패키지에 `.msixupload`를 올리고, 가격·연령 등급 설문·스토어 등록 정보(설명, 스크린샷)를 채운다.
   - 개인정보처리방침 URL이 필요하다 (GitHub Pages 등).
   - `runFullTrust` 제한 기능을 쓰므로 "이 기능을 쓰는 이유"를 적는다: *데스크톱(WPF) 앱으로, 트레이 상주·전역 단축키·로컬 파일 저장을 위해 전체 신뢰가 필요합니다.*
   - 스크린샷은 CI의 `ui-screenshots` 결과물을 쓸 수 있다.
6. 인증(보통 수 시간~며칠)을 통과하면 Store에 게시된다.

## 내 PC에서 Store 판 시험하기

1. CI의 `DayDeck-msix`를 내려받아 푼다.
2. 관리자 PowerShell에서 테스트 인증서를 신뢰 목록에 넣는다 (시험이 끝나면 지워도 된다):
   `Import-Certificate -FilePath .\DayDeck-<버전>-x64-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople`
3. `DayDeck-<버전>-x64-test.msix`를 두 번 눌러 설치한다.

zip 판과 Store 판은 데이터 폴더가 달라서 따로 동작합니다. zip 판 데이터를 옮기려면 zip 판을 트레이에서 종료한 뒤
`Data` 폴더 안의 내용을 Store 판의 데이터 폴더(설정 → 데이터 폴더 "열기")에 복사하세요.
