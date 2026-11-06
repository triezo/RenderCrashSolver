@echo off
:: Builds RenderSolver.exe (personal build, Telegram enabled) with the C# compiler bundled with Windows (.NET Framework 4).
:: resume_render.py and the icon are built into the exe: after editing the script, build again.
:: For the release files (installer + portable zip) use package.bat.
setlocal
set ROOT=%~dp0
set OBJ=%ROOT%obj
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

call "%ROOT%tools\make_icon.bat"
if errorlevel 1 goto failed

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /define:TELEGRAM ^
    /out:"%ROOT%RenderSolver.exe" /win32icon:"%OBJ%\app.ico" /resource:"%ROOT%resume_render.py",resume_render.py ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
    "%ROOT%RenderSolver.cs" "%ROOT%QueueList.cs" "%ROOT%PreviewPanel.cs" "%ROOT%Telegram.cs" "%ROOT%Theme.cs" ^
    "%ROOT%AppInfo.cs" "%ROOT%Installer.cs"
if errorlevel 1 goto failed

echo Build OK: %ROOT%RenderSolver.exe
exit /b 0

:failed
echo BUILD FAILED
pause
exit /b 1
