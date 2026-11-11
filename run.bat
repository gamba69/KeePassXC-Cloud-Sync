@echo off
setlocal EnableExtensions
rem Launch the verified local NativeAOT publish; do not build or run the source implicitly.
set "PROJECT_ROOT=%~dp0"
set "PROGRAM=%PROJECT_ROOT%publish\KeyPassXCCloudSync.exe"
if not exist "%PROGRAM%" (
    >&2 echo ERROR: Published application not found: "%PROGRAM%"
    >&2 echo First run build.bat to compile, test and publish the program.
    exit /b 1
)
start "" /D "%PROJECT_ROOT%publish" "%PROGRAM%" %*
if errorlevel 1 exit /b 1
exit /b 0
