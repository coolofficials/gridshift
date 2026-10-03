Unicode True
!include "MUI2.nsh"

!define PRODUCT_NAME "GridShift — 게임 시작 런처"
!define PRODUCT_VERSION "0.2.0"
!define PRODUCT_PUBLISHER "GridShift contributors"
!ifndef OUTFILE
  !define OUTFILE "artifacts/GridShift-0.2.0-x64-setup.exe"
!endif

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
Caption "${PRODUCT_NAME} 설치"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\GridShift"
InstallDirRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GridShift" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
Icon "Assets\gridshift.ico"
!define MUI_ICON "Assets\gridshift.ico"
!define MUI_UNICON "Assets\gridshift.ico"
ShowInstDetails hide
ShowUninstDetails hide

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\GridShift.exe"
!define MUI_FINISHPAGE_RUN_TEXT "GridShift 실행"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "LICENSES\THIRD-PARTY-NOTICES.md"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Korean"

VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey "FileDescription" "${PRODUCT_NAME} Windows x64 test installer"
VIAddVersionKey "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright © GridShift contributors"

Function .onInit
  SetRegView 64
FunctionEnd

Function un.onInit
  SetRegView 64
FunctionEnd

Section "GridShift 설치" SEC_MAIN
  SetRegView 64
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File /r "artifacts\publish\*"
  CreateDirectory "$SMPROGRAMS\GridShift"
  CreateShortcut "$SMPROGRAMS\GridShift\GridShift.lnk" "$INSTDIR\GridShift.exe" "" "$INSTDIR\GridShift.exe" 0
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GridShift" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GridShift" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GridShift" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GridShift" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GridShift" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GridShift" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GridShift" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext current
  Delete "$SMPROGRAMS\GridShift\GridShift.lnk"
  RMDir "$SMPROGRAMS\GridShift"
  !include "artifacts\uninstall-manifest.nsh"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GridShift"
SectionEnd
