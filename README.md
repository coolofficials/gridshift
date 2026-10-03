# GridShift 0.2.0 — 게임 시작과 작업 공간

게임을 중심으로 앱을 준비하고, 시작·대기·오류 상태를 한국어로 확인하는 Windows 트레이 런처입니다. Windows 10/11 x64 테스트 빌드이며, 게임이나 앱에 코드를 주입하거나 메모리를 읽지 않습니다.

> 이 릴리스 후보는 macOS ARM64 교차 빌드 환경에서 생성할 수 있습니다. 아래 정책 테스트와 Windows 설치/runtime 검증은 구분합니다. 실제 데스크톱 COM, 트레이 표시, 실행/제거 동작은 Windows PC/VM에서 확인해야 합니다.

## 설치

- 로컬 installer: `artifacts/GridShift-0.2.0-x64-setup.exe` (정확한 bytes와 SHA-256은 빌드 증거에 기록).
- 사용자 범위 설치로 관리자 권한이 필요 없습니다. 코드 서명이 없어 SmartScreen 경고가 날 수 있습니다. 출처를 확인한 테스트 파일만 실행하세요.
- installer는 publish payload를 포함하며 실행 중 runtime을 내려받지 않습니다. 설치되는 runtime/license 파일 목록과 SHA-256은 `artifacts/publish/runtime-inventory.txt`에 생성됩니다.
- 제거는 설치 payload의 정확한 파일 목록만 지웁니다. 하위 폴더 전체를 재귀 삭제하지 않으며, 사용자가 추가한 파일/프로필(`%APPDATA%\GridShift\profiles.json`)과 데스크톱 생성 기록은 보존합니다. notices는 설치 폴더 `LICENSES/`에 포함됩니다.

## 3분 시작

1. 시작 메뉴 또는 알림 영역에서 GridShift를 엽니다. 창을 닫으면 종료하지 않고 트레이에 둡니다.
2. **게임 등록** 또는 **앱 찾기**에서 게임을 선택합니다. 앱 찾기는 설치 앱과 실행 중인 EXE 경로를 보여주며, 직접 선택한 EXE 또는 Windows `.lnk` 바로가기를 등록할 수 있습니다. 실행 파일이 실제 존재하는 `.exe`인지 확인하고 경로를 표시합니다.
3. Steam 사용자는 프로필의 `steam://run/앱ID` 또는 `steam://rungameid/앱ID`를 선택할 수 있습니다. 프로세스 감지용 EXE 경로도 반드시 지정합니다.
4. **함께 실행할 앱**은 새 항목을 게임과 함께 열고, 게임이 끝나면 정상 닫기 요청을 보냅니다. 이미 실행 중인 앱은 변경하지 않습니다. force 종료는 기본 꺼짐이며 별도의 사용자 동의가 필요합니다. 기존 항목은 자동으로 바꾸지 않습니다. 선택 항목에 **기존 앱에 닫기 요청 허용**을 눌러 확인하고 저장해야 적용됩니다. 취소하면 기존 설정이 유지됩니다.
5. 메모장·위키·음악 앱은 **필요할 때 켜는 앱**에 따로 등록하세요. 목록/트레이에서 개별적으로 열거나 실행 중인 창으로 이동할 수 있으며, 게임이 실행 중이지 않아도 됩니다. 자동 실행/게임 종료 정리 대상이 아닙니다.
6. 선택 게임의 **게임 시작**, **게임 창 보기**, 선택 앱 열기/이동을 사용합니다. 창 아래 상태에는 실행 확인, 잠깐 대기, 실패 이유가 표시됩니다.

## 속도와 안전

- 프로세스 확인은 2초 간격입니다. companion 시작 전 안정성 확인은 2초입니다. 게임 종료 오감지를 막는 debounce는 프로필마다 1–10초, 기본 3초로 설정할 수 있습니다. 실제 반응은 poll 주기만큼 늦을 수 있습니다.
- 앱 닫기 요청은 UI를 멈추지 않고 최대 10초 기다립니다. 앱이 닫히지 않아도 force 종료 동의가 없으면 그대로 보존합니다. 각 poll과 실행 직전에 process ID/생성 시각/경로/소유 관계/게임 보호 설정을 다시 확인합니다. 다른 프로필의 게임 또는 companion으로 쓰이는 앱은 보호됩니다. 게임이 다시 실행되면 대기 중 종료 요청을 취소합니다.
- 현재 실행 중인 앱이 이미 있는 경우 GridShift는 그것을 새로 시작하거나 닫지 않습니다. 다른 게임에서도 사용 중인 앱은 그대로 둡니다. 확인할 수 없는 프로세스는 안전을 위해 동작을 보류합니다.
- 수동 앱 창 활성화는 EXE 경로와 process creation identity를 확인하고, 창을 옮기거나 표시하거나 foreground로 만들기 직전에 HWND 소유 프로세스를 매번 재검증합니다. 확인이 모호하면 창을 건드리지 않고 경고합니다.

## 가상 데스크톱

- 프로필에서 가상 데스크톱 배치/전환을 선택할 수 있습니다. 기존에 저장된 데스크톱을 재사용하고 없으면 새로 만듭니다. pinned 창은 이동하지 않습니다.
- **GridShift가 만든 빈 데스크톱만 게임 종료 후 정리**는 별도 선택 사항이며 기본 꺼짐입니다. 별도 로컬 생성 기록, 다른 활성 게임의 미공유, 모든 최상위 창의 위치 확인, 대상 데스크톱의 완전한 비어 있음, 남겨 둘 데스크톱으로 안전하게 돌아갈 수 있음을 모두 확인해야 합니다. 확인 실패, 기존 데스크톱, 다른 앱 창 또는 출처를 알 수 없는 경우 삭제하지 않습니다. GridShift 관리 창만 필요한 경우 남겨 둘 데스크톱으로 옮기며, 외부 창은 옮기거나 닫아 비우지 않습니다.
- 이 기능은 빌드별 private COM ABI에 의존합니다. Windows 10 build 19041–19045 및 Windows 11 26100 UBR 2605+, 26200 UBR 8117+만 허용합니다. ABI 호출 실패/미지원에서는 데스크톱을 유지하고 경고합니다. 선언 근거와 정확한 method slot/IID/lifetime은 `virtual-desktop-com-audit.md` 및 `references/virtual-desktop-com/`를 참조하세요.
- 트레이 아이콘은 다중 크기 투명 ICO를 앱 리소스로 사용합니다. 실제 Windows에서 100–250% DPI, 밝고 어두운 테마, Explorer 재시작 뒤의 표시/선명도를 확인해야 합니다.

## 빌드와 검증

필요: `.NET SDK 8.0.408` (repository `global.json` exact pin), Python 3, NSIS 3.13 (`NSISDIR`에는 `Include/MUI2.nsh`, `Stubs/`, `Plugins/` 포함). self-contained runtime packs도 `8.0.15`로 고정하며 inventory/audit가 실제 package 버전을 확인합니다.

```sh
export NSISDIR="/path/to/nsis"
./build-release.sh
```

Windows PowerShell:

```powershell
$env:NSISDIR = 'C:\Path\To\NSIS'
.\build-release.ps1
```

빌드는 locked restore, win-x64 self-contained publish, runtime/license inventory, 실제 payload와 정확히 일치하는 제거 manifest, payload PE/CodeView/privacy/symbol audit, safety/policy/source/icon/ABI checks, 그리고 실제 NSIS installer를 생성합니다. Release publish에는 PDB/symbol files를 넣지 않으며 deterministic `PathMap`을 설정합니다. SDK apphost의 PE CodeView 경로도 inventory 작성 전에 검사해 task/host 경로 대신 안정된 `/_/GridShift/apphost.pdb` 표기로 정규화합니다. `build-release.ps1`는 Windows에서 실제 WinForms `UiChecks`를 실행합니다. `build-release.sh`는 교차 컴파일만 하며 명시적으로 UI 실행 미수행을 출력합니다. Windows harness는 새 설정 실제 체크 상태, autostart ItemCheck/Save/Cancel, 정상 닫기/강제 종료 동의의 Yes/Save/Cancel, legacy migration Yes→Cancel/No→Save/Yes→Save, 현 DPI에서 desktop-cleanup checkbox의 실제 측정 범위를 검사합니다. macOS 결과는 이 UI 검증을 통과한 증거가 아닙니다. 정책 fake도 실제 Windows COM/runtime 검증을 대신하지 않습니다.

허가된 public test-branch workflow `.github/workflows/windows-test-validation.yml`은 SHA-pinned actions로 hosted Windows 2022에서 pinned .NET SDK 및 runtime pack으로 `build-release.ps1`의 live WinForms harness를 실행하고, `tests/InstallerSmoke.ps1`로 실제 NSIS silent install/uninstall 및 payload file/hash와 unrelated-file/profile/desktop-ledger 보존을 확인합니다. Native SDK/build/UI/audit/install 프로세스 stdout/stderr와 exit codes를 각각 30일 Actions artifact로 보존합니다. Actions 서비스는 실행 중인 run의 완료 로그를 같은 run에 업로드할 수 없으므로, 완료 후 `gh run view <run-id> --repo coolofficials/gridshift --log` 전체 출력을 `artifacts/windows-tested/actions-full-run.log`에 따로 보존합니다. 이 hosted run은 사용자의 interactive display/session을 제공하지 않습니다. 따라서 runner의 `DeviceDpi`/bounds/display/session 측정은 해당 runner context의 결과일 뿐 100/150/200% 시각 확인이나 user's tray/game/real COM 동작을 증명하지 않습니다.

Interactive Windows 10/11 x64 PC/VM에서는 별도로 `build-release.ps1` 원시 로그를 보존하고, app launch·설정 round-trip과 실제 desktop API create/move/fallback/cleanup retry/cancel, companion restart/debounce/normal-close/force-off, EXE/Steam launch, DPI 100/150/200%, theme 및 Explorer restart 후 tray/checkbox 시각 행렬을 확인하세요. 세부 게이트는 `tests/Windows-Verification.md`; compile/policy/hosted check를 user visual/runtime 성공으로 확대 주장하지 마세요.
