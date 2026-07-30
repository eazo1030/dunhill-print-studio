@echo off
echo ========================================
echo Dunhill Print Studio — Complete Clean Slate
echo ========================================
echo This removes every trace of Dunhill Print Studio, Velopack,
echo and any partial MSI installs. Run as Administrator.
echo.

:: 1. Kill any running app instances
echo [1/8] Killing running Dunhill Print Studio processes...
taskkill /F /IM DunhillPrintStudio.exe 2>nul
taskkill /F /IM Update.exe 2>nul
taskkill /F /IM DunhillPrintStudio-1.2.2-full.exe 2>nul
ping 127.0.0.1 -n 2 >nul

:: 2. Wipe Velopack per-user install
echo [2/8] Removing Velopack install dir...
rmdir /s /q "%LOCALAPPDATA%\DunhillPrintStudio" 2>nul
rmdir /s /q "%LOCALAPPDATA%\Update.exe" 2>nul
rmdir /s /q "%LOCALAPPDATA%\Update" 2>nul
rmdir /s /q "%LOCALAPPDATA%\Temp\.net\DunhillPrintStudio" 2>nul

:: 3. Remove ANY Desktop / Start Menu shortcuts
echo [3/8] Removing shortcuts...
del /f /q "%USERPROFILE%\Desktop\Dunhill Print Studio.lnk" 2>nul
del /f /q "%PUBLIC%\Desktop\Dunhill Print Studio.lnk" 2>nul
del /f /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Dunhill Print Studio.lnk" 2>nul
del /f /q "%PROGRAMDATA%\Microsoft\Windows\Start Menu\Programs\Dunhill Print Studio.lnk" 2>nul
rmdir /s /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Dunhill Print Studio" 2>nul
rmdir /s /q "%PROGRAMDATA%\Microsoft\Windows\Start Menu\Programs\Dunhill Print Studio" 2>nul

:: 4. Remove MSI leftover from WiX burn (Velopack uses WiX for Setup.exe)
echo [4/8] Removing MSI leftover...
rmdir /s /q "%PROGRAMDATA%\Package Cache" 2>nul

:: 5. Remove registry entries (per-user and per-machine uninstall keys)
echo [5/8] Removing registry entries...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Dunhill Print Studio" /f 2>nul
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Dunhill Print Studio" /f 2>nul
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{*Dunhill*" /f 2>nul
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\{*Dunhill*" /f 2>nul
reg delete "HKCU\Software\DunhillGlobal" /f 2>nul
reg delete "HKLM\Software\DunhillGlobal" /f 2>nul

:: 6. Remove Windows service entries (Velopack sometimes leaves these)
echo [6/8] Removing Windows services...
sc delete "DunhillPrintStudio" 2>nul
sc delete "Dunhill Print Studio" 2>nul

:: 7. Clear any pending file locks by emptying the recycle bin + running disk cleanup
echo [7/8] Forcing any pending file locks to release...
echo Y | cleanmgr /d C 2>nul

echo.
echo [8/8] Done. Now manually:
echo   1. Close this elevated PowerShell
echo   2. Open a NORMAL (non-admin) PowerShell
echo   3. Download v1.2.2: https://github.com/eazo1030/dunhill-print-studio/releases/latest/download/DunhillPrintStudio-win-Setup.exe
echo   4. Double-click the Setup.exe — DO NOT right-click for admin
echo.
pause
