Unicode true
RequestExecutionLevel user

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
OutFile "${OUTPUT_DIR}\VibeGauge-Setup-v${APP_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\VibeGauge"
InstallDirRegKey HKCU "Software\VibeGauge" "InstallDir"
SetCompressor /SOLID lzma

Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

Section "VibeGauge" SEC_MAIN
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
SectionEnd

Section "Uninstall"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "VibeGauge"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\VibeGauge"
  DeleteRegKey HKCU "Software\VibeGauge"
  RMDir /r "$SMPROGRAMS\VibeGauge"
  RMDir /r "$INSTDIR"
SectionEnd
