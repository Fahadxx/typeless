@echo off
rem Builds shell\out\TypelessShell.dll. Usage: build.cmd [arm64 or x64], default arm64
setlocal

set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=arm64"

set "VCARCH="
if /i "%ARCH%"=="arm64" set "VCARCH=arm64"
if /i "%ARCH%"=="x64" set "VCARCH=arm64_amd64"
if "%VCARCH%"=="" (
    echo Unsupported arch "%ARCH%". Use arm64 or x64.
    exit /b 2
)

set "VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat"
if not exist "%VCVARS%" (
    echo vcvarsall.bat not found at "%VCVARS%"
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
