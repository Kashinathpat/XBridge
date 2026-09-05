@echo off
setlocal
set "ROOT=%~dp0.."
set "CSPROJ=%ROOT%\src\XBridge.csproj"
set "PUBDIR=%ROOT%\src\bin\Release\net8.0-windows\win-x64\publish"

echo === dotnet publish (framework-dependent, single-file, win-x64) ===
echo     Target machine needs the .NET 8 Desktop Runtime (x64) - NOT bundled.
dotnet publish "%CSPROJ%" -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true ^
  -p:DebugType=none -p:DebugSymbols=false
if errorlevel 1 (
  echo.
  echo publish FAILED
  exit /b 1
)

if not exist "%ROOT%\publish" mkdir "%ROOT%\publish"
copy /y "%PUBDIR%\XBridge.exe" "%ROOT%\publish\XBridge.exe" >nul

echo.
echo Single-file EXE (needs .NET 8 Desktop Runtime x64 on the target):
echo   %ROOT%\publish\XBridge.exe
echo.

where ISCC >nul 2>nul
if errorlevel 1 (
  echo Inno Setup compiler ^(ISCC.exe^) is not on PATH.
  echo Open  installer\XBridge.iss  in the Inno Setup IDE to produce the setup EXE,
  echo or add Inno Setup 6 to PATH and re-run this script.
  exit /b 0
)

echo === ISCC ===
ISCC "%~dp0XBridge.iss"
