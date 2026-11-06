@echo off
:: Builds the release files in dist\ (version from AppInfo.cs):
::   RenderSolver_Setup_<ver>.exe     installer for the current user (no admin rights), RenderSolver.exe + README.txt inside
::   RenderSolver_<ver>_portable.zip  RenderSolver.exe + README.txt, runs from any folder
:: resume_render.py is built into RenderSolver.exe. Your settings.ini and logs are NOT included. Telegram is left out of this build.
setlocal
set ROOT=%~dp0
set OBJ=%ROOT%obj
for /f tokens^=2^ delims^=^" %%v in ('findstr /c:"string Version" "%ROOT%AppInfo.cs"') do set VERSION=%%v
if "%VERSION%"=="" (
    echo Version not found in AppInfo.cs
    goto failed
)
set OUT=%ROOT%dist\RenderSolver
set ZIP=%ROOT%dist\RenderSolver_%VERSION%_portable.zip
set SETUP=%ROOT%dist\RenderSolver_Setup_%VERSION%.exe
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if exist "%ROOT%dist" rmdir /s /q "%ROOT%dist"
mkdir "%OUT%"

call "%ROOT%tools\make_icon.bat"
if errorlevel 1 goto failed

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ ^
    /out:"%OUT%\RenderSolver.exe" /win32icon:"%OBJ%\app.ico" /resource:"%ROOT%resume_render.py",resume_render.py ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
    "%ROOT%RenderSolver.cs" "%ROOT%QueueList.cs" "%ROOT%PreviewPanel.cs" "%ROOT%Telegram.cs" "%ROOT%Theme.cs" ^
    "%ROOT%AppInfo.cs" "%ROOT%Installer.cs"
if errorlevel 1 goto failed
copy /y "%ROOT%README.txt" "%OUT%\" >nul

powershell -NoProfile -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 goto failed

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /nowarn:649 ^
    /out:"%SETUP%" /win32icon:"%OBJ%\app.ico" ^
    /resource:"%OUT%\RenderSolver.exe",RenderSolver.exe /resource:"%ROOT%README.txt",README.txt ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    "%ROOT%Setup.cs" "%ROOT%Installer.cs" "%ROOT%Theme.cs" "%ROOT%AppInfo.cs"
if errorlevel 1 goto failed

echo.
echo Package %VERSION% OK:
echo   %SETUP%
echo   %ZIP%
exit /b 0

:failed
echo PACKAGE FAILED
pause
exit /b 1
