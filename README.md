# GridShift 0.1.0 — 게임과 함께 여는 나만의 작업 공간

게임을 실행하고, 게임 주변의 앱은 필요할 때 직접 불러오세요. GridShift는 한국어 Windows tray 중심 게임 런처입니다. 프로필마다 게임 실행 정보, 자동 동반 앱, **게임과 완전히 분리된 수동 관련 앱**, 선택적 가상 데스크톱 설정을 저장합니다.

> 테스트 빌드입니다. Windows 10/11 x64 설치 파일은 제공하지만, 이 빌드를 만든 환경은 macOS ARM64입니다. 아래에 적힌 빌드·테스트 외에 Windows에서 실행해 검증한 것처럼 간주하지 말아 주세요.

## 설치 파일

- 실제 로컬 installer: `artifacts/GridShift-0.1.0-x64-setup.exe`. 정확한 크기/SHA-256은 이 후보의 build handoff 기록에 포함됩니다.
- 사용자 범위 설치이며 관리자 권한을 요구하지 않습니다. 코드 서명은 되어 있지 않아 SmartScreen 경고가 나올 수 있습니다. 출처를 확인한 테스트 파일만 실행하세요.
- 설치 파일은 로컬 publish payload를 포함합니다. 실행 중 런타임을 내려받지 않습니다. 설치 시각의 dependency/license와 모든 payload 파일 해시는 `artifacts/publish/runtime-inventory.txt`에 기록됩니다.
- 제거는 빌드 시 생성된 exact payload file manifest만 삭제하고, 설치 폴더의 빈 디렉터리만 제거합니다. 사용자 파일과 `%APPDATA%\GridShift\profiles.json`은 보존합니다. license/notice는 설치 폴더의 `LICENSES/`에 있습니다.

## 2분 시작 가이드

1. GridShift를 시작 메뉴 또는 알림 영역의 아이콘에서 엽니다. 창을 닫거나 최소화하면 런처를 종료하지 않고 tray에 둡니다.
2. **게임 추가**에서 게임 EXE와 표시 이름을 지정합니다. Steam을 이용한다면 `steam://run/앱ID` 또는 `steam://rungameid/앱ID`도 추가할 수 있습니다. Steam URI는 실행 요청용이며, 실행 감지에는 실제 게임 EXE 경로도 필요합니다.
3. 게임과 함께 자동으로 시작할 앱만 **자동 동반 설정**에서 별도로 등록합니다. 이 기능은 게임 프로세스 그룹이 30초 안정된 후 누락된 앱을 시작합니다. 종료 설정은 정상 종료 요청과 강제 종료를 분리합니다. 기본은 모두 유지이며, 정상 종료는 별도 opt-in, 강제 종료는 다시 별도의 opt-in입니다.
4. 메모장, 위키, 음악 플레이어처럼 원할 때만 쓸 앱은 **수동 관련 앱**에 등록하세요. 목록에서 앱을 선택해 **관련 앱 실행** 또는 **관련 앱 활성화**를 누릅니다. 게임이 켜져 있지 않아도 됩니다. 수동 관련 앱은 자동 시작/게임 종료 정리/자동 companion ownership에 절대 편입되지 않습니다.
5. GridShift tray 아이콘의 메뉴에는 각 게임 실행과 수동 관련 앱 실행/활성화가 표시됩니다. 이 메뉴는 관리 창이 다른 가상 데스크톱에 남아 있을 때도 빠른 실행 경로를 제공합니다. 관리 창 열기 및 수동 관련 앱 창 활성화는 지원되는 빌드에서 선택된 GridShift/앱 창을 현재 데스크톱으로 옮긴 뒤 표시합니다. 이 이동은 사용자 요청으로 선택한 창에만 수행합니다.
6. **설치 앱 검색**은 uninstall registry에서 실행 경로가 확인되는 앱을 찾습니다. 선택한 앱은 새 게임 프로필 또는 현재 선택된 프로필의 수동 관련 앱으로 등록할 수 있습니다. 목록에 없는 EXE는 게임/수동 관련 앱 편집에서 직접 선택할 수 있습니다.

## 상태 확인과 창 활성화

- 게임 프로필 아래에 자동 동반 앱/수동 관련 앱 상태가 따로 나타납니다.
- **그룹 창 활성화**는 감지된 게임 프로세스 family와 런처가 시작한 자동 companion 창을 찾습니다. 수동 관련 앱은 전용 활성화 버튼이나 tray 메뉴에서 따로 다룹니다.
- 프로세스는 2초 간격으로 관찰됩니다. 부모 PID만 믿지 않고 process creation time을 포함한 process identity를 사용합니다. game root가 먼저 종료돼도 관찰된 worker가 남으면 같은 세션으로 추적합니다.
- 수동 관련 앱 창 활성화는 설정된 EXE 경로와 process identity가 확인된 창만 대상으로 합니다. 창을 이동/표시/foreground로 만들기 직전에 매번 HWND 소유 PID, creation identity, 실행 경로를 다시 확인합니다. 경로 접근 불가, 동명이인 EXE, PID 재사용 등 불확실한 상태는 경고와 함께 아무 창 동작도 하지 않습니다.

## 안전 기본값

- 이미 실행 중이거나 경로/identity가 불확실한 companion은 기존/공유 프로세스로 보고 자동 시작을 건너뜁니다.
- 게임 종료 뒤 companion 정리를 선택한 경우 먼저 같은 열린 process handle의 PID/경로/creation identity와 소유권·공유/게임 보호 조건을 확인하고 `WM_CLOSE` 정상 종료를 요청합니다. 최대 10초 동안 UI를 막지 않고 기다리며, 매 대기 poll마다 같은 handle과 모든 보호 조건을 다시 확인합니다. 대기 시간 초과 후에도 실행 중인 앱의 강제 종료는 별도의 기본 꺼짐 opt-in이 있을 때만, `TerminateProcess` 직전에 같은 handle과 안전 조건을 재검증한 뒤 시도합니다. 설정 파일에 강제 종료 항목이 없던 기존 프로필도 기본값은 opt-in false입니다. 조건이 바뀌거나 불확실하면 취소하고 프로세스를 보존합니다. 런처를 종료해도 게임이나 앱을 정리하지 않습니다.
- 모든 프로필의 configured game EXE와 관찰된 game-family identity는 companion 종료와 가상 데스크톱 이동 양쪽에서 보수적으로 보호됩니다.
- 가상 데스크톱은 Windows 10 build 19041–19045 및 Windows 11 build 26100 UBR 2605+, build 26200 UBR 8117+에서만 private COM backend를 호출합니다. 미지원 빌드/호출 실패는 화면에 경고하고 기존 동작을 유지합니다. 앱은 데스크톱을 자동 삭제하지 않습니다. pinned view는 움직이지 않습니다.
- 게임/외부 프로그램에 코드 주입, 후킹, 메모리 접근을 하지 않으며 제3자 설정을 변경하지 않습니다.

## 가상 데스크톱 선택 기능

게임 프로필 수정에서 **가상 데스크톱 사용** 및 선택적 **배치 후 전환**을 지정할 수 있습니다. GridShift는 저장된 desktop ID가 여전히 존재하면 재사용하고, 아니면 새 desktop을 만듭니다. 알려진 게임 identity family 및 안전 조건을 통과한 launcher-owned companion의 보이는 창만 배치합니다. 배치가 실패하면 자동 전환도 하지 않습니다.

관리 창 또는 사용자가 tray에서 직접 활성화를 선택한 관련 앱은 지원되는 backend에서 현재 데스크톱으로 가져옵니다. tray quick-action 메뉴는 game/related-app 실행 경로를 제공합니다. 다른 빌드에서 관리 창 이동 API가 지원되지 않으면 지속 경고가 나타납니다. COM compatibility 및 실제 Windows 창 동작은 아직 Windows PC에서 검증되지 않았습니다.

## 알려진 한계와 검증 범위

- 현재 후보는 macOS ARM64에서 .NET 8 SDK로 `win-x64` self-contained publish 및 NSIS 3.13 installer를 교차 생성합니다.
- fake snapshot/clock/termination/desktop backend 정책 회귀와 source guard checks는 빌드에 포함됩니다. 이들은 Windows runtime test를 대신하지 않습니다.
- 실제 Windows 설치/제거, tray 동작, Steam URI, 게임/companion process handle cleanup, 가상 데스크톱 COM movement/switching, 다중 모니터/pinned view 동작은 Windows 10/11 x64 PC에서 확인해야 합니다.
- 지원 ABI와 upstream vtable/IID source/license/commit/hash: `virtual-desktop-com-audit.md`, `references/virtual-desktop-com/`. 외부 private COM ABI가 모든 업데이트에서 보장된다고 주장하지 않습니다.

## 소스 빌드

.NET 8 SDK, Python 3, NSIS 3.11+이 필요합니다. NSIS 배포 폴더(`Include/MUI2.nsh`, `Stubs/`, `Plugins/`)를 `NSISDIR`에 지정합니다. NuGet locked restore, self-contained publish, runtime/license inventory, uninstall manifest 및 source/policy tests, installer 생성은 아래 스크립트가 수행하며 자신이 관리하는 이전 publish/installer 출력만 먼저 정리합니다.

```sh
export NSISDIR="/path/to/nsis"
./build-release.sh
```

Windows PowerShell에서는:

```powershell
$env:NSISDIR = 'C:\Program Files (x86)\NSIS'
.\build-release.ps1
```

실제 설치 후 테스트 전용 계정/VM에서 프로필 및 사용자 데이터 보존을 확인하세요. Windows에서 확인한 결과와 macOS 교차 빌드 결과를 구분해 기록해 주세요.
