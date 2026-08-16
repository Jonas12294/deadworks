@echo off
setlocal

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo Could not find vswhere.exe - is Visual Studio installed?
    exit /b 1
)

for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * ^
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 ^
    -property installationPath`) do set "VSPATH=%%i"

if not defined VSPATH (
    echo No Visual Studio installation with the C++ toolset was found.
    exit /b 1
)

call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1

cd /d "%~dp0"

REM protobuf-lite, for MessageLite::ParseFromArray against the ENGINE's own
REM message objects (client->server 280 send). No generated message is compiled
REM in: registering our own CClientMsg_CustomGameEvent descriptor alongside
REM client.dll's would be a duplicate-registration conflict.
set "PB=..\sourcesdk\thirdparty\protobuf"
set "PBLIB=%PB%\build\Release\libprotobuf-lite.lib"
if not exist "%PBLIB%" (
    echo Could not find libprotobuf-lite.lib at %PBLIB%
    exit /b 1
)

REM /MT to match libprotobuf-lite (static CRT), which also makes the injected
REM DLL self-contained - no msvcp140/vcruntime dependency in the target process.
cl /nologo /LD /O2 /EHsc /std:c++17 /W3 /MT ^
   /I "%PB%\src" ^
   /D_SILENCE_ALL_CXX17_DEPRECATION_WARNINGS ^
   /Fe:uiwatch.dll dllmain.cpp ^
   /link /OUT:uiwatch.dll "%PBLIB%"
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

del /q dllmain.obj uiwatch.exp uiwatch.lib 2>nul
echo.
echo Built uiwatch.dll
