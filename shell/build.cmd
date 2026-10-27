@echo off
rem Builds shell\out\TypelessShell.dll. Usage: build.cmd [arm64 or x64], default arm64
setlocal

set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=arm64"

rem vcvarsall args depend on the host: native when it matches, a cross toolset otherwise.
rem The registry has the machine's own architecture (an emulated x64 cmd on ARM64 reports AMD64).
set "HOST=x64"
for /f "tokens=3" %%a in ('reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v PROCESSOR_ARCHITECTURE 2^>nul') do if /i "%%a"=="ARM64" set "HOST=arm64"

set "VCARCH="
set "VCCOMP="
if /i "%ARCH%"=="arm64" (
    set "VCCOMP=Microsoft.VisualStudio.Component.VC.Tools.ARM64"
    if "%HOST%"=="arm64" (set "VCARCH=arm64") else (set "VCARCH=amd64_arm64")
)
if /i "%ARCH%"=="x64" (
    set "VCCOMP=Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
    if "%HOST%"=="arm64" (set "VCARCH=arm64_amd64") else (set "VCARCH=amd64")
)
if "%VCARCH%"=="" (
    echo Unsupported arch "%ARCH%". Use arm64 or x64.
    exit /b 2
)

rem Any VS 2022+ edition with the C++ tools for the target, else the old Community path.
set "VCVARS="
set "VSWHERE_DIR=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
if not exist "%VSWHERE_DIR%\vswhere.exe" goto novswhere
set "PATH=%VSWHERE_DIR%;%PATH%"
for /f "delims=" %%i in ('vswhere.exe -latest -products * -requires %VCCOMP% -find VC\Auxiliary\Build\vcvarsall.bat') do set "VCVARS=%%i"
:novswhere
if "%VCVARS%"=="" set "VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat"
if not exist "%VCVARS%" (
    echo vcvarsall.bat not found. Install the Visual Studio C++ build tools with the %VCCOMP% component.
    exit /b 3
)

call "%VCVARS%" %VCARCH% >nul
if errorlevel 1 (
    echo vcvarsall.bat %VCARCH% failed
    exit /b 4
)

cd /d "%~dp0"
if not exist out mkdir out

cl /nologo /O2 /EHsc /std:c++17 /LD /DUNICODE /D_UNICODE /W4 /Foout\ /Fdout\ TypelessShell.cpp /link /DEF:TypelessShell.def /OUT:out\TypelessShell.dll
set "RC=%ERRORLEVEL%"

endlocal & exit /b %RC%
