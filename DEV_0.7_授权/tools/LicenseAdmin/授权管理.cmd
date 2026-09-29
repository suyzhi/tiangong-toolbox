@echo off
rem Admin entry for activation codes (TianGong Toolbox).
rem   Double-click                     -> open the GUI (recommended)
rem   this-cmd.cmd new M "Customer A"   -> command line: new / batch / list / verify / revoke / plans
rem Note: TianGongLicenseAdmin.exe itself is a CONSOLE tool; double-clicking IT only prints usage and exits.
if "%~1"=="" (
  start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0license-admin-gui.ps1"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0license-admin.ps1" %*
  if errorlevel 1 pause
)
