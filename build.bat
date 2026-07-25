@echo off
REM ====================================================================
REM Traynexus build script (no Visual Studio / dotnet SDK required)
REM Uses csc.exe shipped with .NET Framework 4.x
REM
REM 所有输出同步写入 build_log.txt，无论窗口是否闪退，日志都能查看
REM ====================================================================

setlocal enabledelayedexpansion

REM 清空旧日志
if exist build_log.txt del /f /q build_log.txt

call :Log "===================================================================="
call :Log " Traynexus Build - %DATE% %TIME%"
call :Log "===================================================================="

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  call :Log "[ERROR] csc.exe not found. Install .NET Framework 4.x."
  goto :Fail
)

pushd "%~dp0"

if not exist bin mkdir bin

call :Log "[1/2] Compiling Traynexus.exe ..."
call :Log "CSC = %CSC%"
call :Log ""

REM 把 csc 的输出同时打到屏幕和日志
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /debug- /win32icon:resources\tray_default.ico /win32manifest:src\Traynexus\app.manifest /resource:logo_256.png,Traynexus.logo_256.png /resource:logo_128.png,Traynexus.logo_128.png /resource:github_icon.png,Traynexus.github_icon.png /resource:github_icon_white.png,Traynexus.github_icon_white.png /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:Microsoft.CSharp.dll /reference:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll /out:bin\Traynexus.exe src\Traynexus\Program.cs src\Traynexus\TrayContext.cs src\Traynexus\ReleasePanel.cs src\Traynexus\MemoryInfo.cs src\Traynexus\MemoryCleaner.cs src\Traynexus\NativeMethods.cs src\Traynexus\IconRenderer.cs src\Traynexus\Settings.cs src\Traynexus\ConfigMigrator.cs src\Traynexus\AutoStartManager.cs src\Traynexus\MainForm.cs src\Traynexus\Fonts.cs src\Traynexus\QuickForm.cs src\Traynexus\BatteryInfo.cs src\Traynexus\OemChargeController.cs src\Traynexus\BrightnessController.cs src\Traynexus\UpdateChecker.cs src\Traynexus\LightSensorReader.cs src\Traynexus\AdaptiveBrightnessController.cs >> build_log.txt 2>&1

if errorlevel 1 (
  call :Log ""
  call :Log "[ERROR] Build failed. See errors above."
  goto :Fail
)

REM 纯 WinForms 不再需要 WebView2 运行时依赖

call :Log ""
call :Log "[2/2] Build succeeded: %CD%\bin\Traynexus.exe"
for %%F in (bin\Traynexus.exe) do call :Log "Size: %%~zF bytes"
call :Log ""
call :Log "Build OK. 详细日志见 build_log.txt"

REM 同步显示日志内容到屏幕
type build_log.txt
echo.
echo ========================================
echo  Build 成功！按任意键关闭窗口...
echo ========================================
pause
popd
endlocal
exit /b 0

:Fail
echo.
echo ========================================
echo  Build 失败！请查看上方错误或 build_log.txt
echo ========================================
type build_log.txt
echo.
pause
popd
endlocal
exit /b 1

REM ====================================================================
REM :Log 子例程 - 同步写屏幕和日志文件
REM ====================================================================
:Log
echo %~1
echo %~1 >> build_log.txt
exit /b 0
