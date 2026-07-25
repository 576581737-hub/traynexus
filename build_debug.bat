@echo off
setlocal enabledelayedexpansion

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found.
  pause
  exit /b 1
)

pushd "%~dp0"

if not exist bin mkdir bin

echo === Checking WebView2 DLLs ===
echo Pure WinForms mode - no WebView2 needed.
echo.

echo.
echo === Compiling Traynexus.exe ===
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /debug- /win32icon:resources\tray_default.ico /win32manifest:src\Traynexus\app.manifest /resource:logo_256.png,Traynexus.logo_256.png /resource:logo_128.png,Traynexus.logo_128.png /resource:github_icon.png,Traynexus.github_icon.png /resource:github_icon_white.png,Traynexus.github_icon_white.png /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:Microsoft.CSharp.dll /out:bin\Traynexus.exe src\Traynexus\Program.cs src\Traynexus\TrayContext.cs src\Traynexus\ReleasePanel.cs src\Traynexus\MemoryInfo.cs src\Traynexus\MemoryCleaner.cs src\Traynexus\NativeMethods.cs src\Traynexus\IconRenderer.cs src\Traynexus\Settings.cs src\Traynexus\ConfigMigrator.cs src\Traynexus\AutoStartManager.cs src\Traynexus\MainForm.cs src\Traynexus\Fonts.cs src\Traynexus\QuickForm.cs src\Traynexus\BatteryInfo.cs src\Traynexus\OemChargeController.cs src\Traynexus\BrightnessController.cs src\Traynexus\UpdateChecker.cs

if errorlevel 1 (
  echo.
  echo [ERROR] Build failed! See errors above.
  echo.
  pause
  popd
  exit /b 1
)

echo.
echo === Build succeeded! ===
for %%F in (bin\Traynexus.exe) do echo Size: %%~zF bytes
echo.
pause
popd
endlocal
