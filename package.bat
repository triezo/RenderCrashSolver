@echo off
:: Builds a shareable test package: dist\RenderSolver\ (exe + script + README) and dist\RenderSolver_<ver>.zip
:: Your settings.ini and logs are NOT included. Telegram is left out of this build.
setlocal
set VERSION=0.6
set ROOT=%~dp0
set OUT=%ROOT%dist\RenderSolver
set ZIP=%ROOT%dist\RenderSolver_%VERSION%.zip
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if exist "%ROOT%dist" rmdir /s /q "%ROOT%dist"
mkdir "%OUT%"

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ ^
    /out:"%OUT%\RenderSolver.exe" ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
    "%ROOT%RenderSolver.cs" "%ROOT%QueueList.cs" "%ROOT%PreviewPanel.cs" "%ROOT%Telegram.cs" "%ROOT%Theme.cs"
if errorlevel 1 (
    echo BUILD FAILED
    pause
    exit /b 1
)

copy /y "%ROOT%resume_render.py" "%OUT%\" >nul
copy /y "%ROOT%README.txt" "%OUT%\" >nul

powershell -NoProfile -Command "Compress-Archive -Path '%OUT%' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
    echo ZIP FAILED
    pause
    exit /b 1
)

echo.
echo Package OK:
echo   %OUT%
echo   %ZIP%
