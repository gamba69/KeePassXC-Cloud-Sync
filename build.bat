@echo off
setlocal EnableExtensions
rem Full build, mandatory tests, NativeAOT and portable ZIP/SHA256 by default.
rem -NoPause suppresses the final pause for CI, scripts and unattended runs.
set "BUILD_NO_PAUSE=0"
set "ROOT=%~dp0"
:check_args
if "%~1"=="" goto run_build
if /I "%~1"=="-NoPause" set "BUILD_NO_PAUSE=1"
shift
goto check_args
:run_build
pushd "%ROOT%"
if errorlevel 1 (
    >&2 echo [FAIL] Build / cannot enter project directory.
    set "BUILD_EXIT=1"
    goto finish
)
rem %* remains the original argument list even after SHIFT.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\bootstrap-build.ps1" %*
set "BUILD_EXIT=%ERRORLEVEL%"
popd
:finish
if "%BUILD_EXIT%"=="0" (
    echo [PASS] Build / finished successfully.
) else (
    >&2 echo [FAIL] Build / exit code %BUILD_EXIT%.
)
if "%BUILD_NO_PAUSE%"=="0" pause
exit /b %BUILD_EXIT%
