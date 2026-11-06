@echo off
:: Writes obj\app.ico from the icon drawing in Theme.cs (called by build.bat and package.bat).
setlocal
set ROOT=%~dp0..\
set OBJ=%ROOT%obj
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%OBJ%" mkdir "%OBJ%"

"%CSC%" /nologo /codepage:65001 /target:exe /optimize+ /nowarn:649 /out:"%OBJ%\make_icon.exe" ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    "%ROOT%tools\make_icon.cs" "%ROOT%Theme.cs" "%ROOT%AppInfo.cs"
if errorlevel 1 exit /b 1
"%OBJ%\make_icon.exe" "%OBJ%\app.ico"
if errorlevel 1 exit /b 1
exit /b 0
