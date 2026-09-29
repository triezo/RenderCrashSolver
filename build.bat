@echo off
:: Builds RenderCrashSolver.exe (personal build, Telegram enabled) with the C# compiler bundled with Windows (.NET Framework 4).
:: For a shareable folder + zip use package.bat.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /define:TELEGRAM ^
    /out:"%~dp0RenderCrashSolver.exe" ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
    "%~dp0RenderCrashSolver.cs" "%~dp0PreviewPanel.cs" "%~dp0Telegram.cs"

if errorlevel 1 (
    echo BUILD FAILED
    pause
    exit /b 1
)
echo Build OK: %~dp0RenderCrashSolver.exe
