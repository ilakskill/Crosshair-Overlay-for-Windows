@echo off
setlocal

rem Build script for Crosshair Overlay.
rem Works from any current directory: every path is relative to this script's folder (%~dp0).
rem Uses the in-box .NET Framework 4.x C# compiler, so no SDK install is required.

set "SRC=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: Could not find csc.exe in either
    echo   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319
    echo   %WINDIR%\Microsoft.NET\Framework\v4.0.30319
    echo The .NET Framework 4.x C# compiler is required. It ships with Windows 10 and 11.
    exit /b 1
)

echo Using compiler: %CSC%
echo Compiling "%SRC%Crosshair.cs" ...

rem Compiler output is captured to a temporary log so specific errors can be
rem recognised, then echoed back unchanged.
set "LOG=%TEMP%\crosshair-build-%RANDOM%%RANDOM%.log"

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
    /win32manifest:"%SRC%app.manifest" ^
    /out:"%SRC%Crosshair.exe" ^
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    "%SRC%Crosshair.cs" > "%LOG%" 2>&1

set "RC=%ERRORLEVEL%"
type "%LOG%"

rem CS0016 means csc could not write the output file. In practice that is
rem because Crosshair.exe is running and Windows keeps the image locked.
if "%RC%"=="0" (
    echo.
    echo BUILD SUCCEEDED: "%SRC%Crosshair.exe"
) else (
    echo.
    findstr /C:"CS0016" "%LOG%" >nul 2>&1
    if not errorlevel 1 (
        echo Crosshair.exe is running. Exit it from the tray icon ^(right-click ^> Exit^) and run build.cmd again.
        echo.
    )
    echo BUILD FAILED with exit code %RC%
)

del "%LOG%" >nul 2>&1
exit /b %RC%
