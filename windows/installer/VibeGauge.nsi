Unicode true
RequestExecutionLevel user
ManifestDPIAware true
!include "MUI2.nsh"

!ifndef APP_VERSION
  !define APP_VERSION "0.0.0"
!endif
!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "..\artifacts\publish"
!endif
!ifndef OUTPUT_DIR
  !define OUTPUT_DIR "..\artifacts"
!endif

Name "VibeGauge"
Caption "VibeGauge ${APP_VERSION} 安装"
UninstallCaption "VibeGauge ${APP_VERSION} 卸载"
BrandingText " "
ShowInstDetails hide
ShowUninstDetails hide
OutFile "${OUTPUT_DIR}\VibeGauge-Setup-v${APP_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\VibeGauge"
InstallDirRegKey HKCU "Software\VibeGauge" "InstallDir"
SetCompressor /SOLID lzma

!define MUI_ICON "..\src\VibeGauge.Windows\Assets\AppIcon.ico"
!define MUI_UNICON "${MUI_ICON}"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "header.bmp"

!define MUI_PAGE_HEADER_TEXT "安装 VibeGauge ${APP_VERSION}"
!define MUI_PAGE_HEADER_SUBTEXT "选择安装位置"
!define MUI_DIRECTORYPAGE_TEXT_TOP "将为当前 Windows 用户安装 VibeGauge。"
!insertmacro MUI_PAGE_DIRECTORY
!define MUI_PAGE_HEADER_TEXT "正在安装 VibeGauge ${APP_VERSION}"
!define MUI_PAGE_HEADER_SUBTEXT "请稍候。"
!define MUI_INSTFILESPAGE_FINISHHEADER_TEXT "VibeGauge ${APP_VERSION} 安装完成"
!define MUI_INSTFILESPAGE_FINISHHEADER_SUBTEXT "可从开始菜单打开 VibeGauge。"
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Section "VibeGauge" SEC_MAIN
  SetDetailsPrint textonly
  DetailPrint "正在安装程序文件…"
  SetDetailsPrint listonly
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\VibeGauge" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\VibeGauge" "DisplayName" "VibeGauge"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\VibeGauge" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\VibeGauge" "DisplayIcon" "$INSTDIR\VibeGauge.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\VibeGauge" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\VibeGauge" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\VibeGauge" "NoRepair" 1
  CreateDirectory "$SMPROGRAMS\VibeGauge"
  CreateShortcut "$SMPROGRAMS\VibeGauge\VibeGauge.lnk" "$INSTDIR\VibeGauge.exe"
  CreateShortcut "$SMPROGRAMS\VibeGauge\Uninstall VibeGauge.lnk" "$INSTDIR\Uninstall.exe"
  SetDetailsPrint both
  DetailPrint "安装完成"
SectionEnd

Section "Uninstall"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "VibeGauge"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\VibeGauge"
  DeleteRegKey HKCU "Software\VibeGauge"
  RMDir /r "$SMPROGRAMS\VibeGauge"
  RMDir /r "$INSTDIR"
SectionEnd
