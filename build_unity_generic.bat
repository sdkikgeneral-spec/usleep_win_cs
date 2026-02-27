@echo off
setlocal ENABLEDELAYEDEXPANSION
cd /d %~dp0

echo === Building Unity generic DLL (netstandard2.1, no P/Invoke symbols) ===
where dotnet >nul 2>&1 || (echo [ERROR] dotnet CLI not found. Install .NET SDK. & exit /b 1)

dotnet restore unity\usleep_win_cs.unity.csproj || exit /b 1

dotnet build unity\usleep_win_cs.unity.csproj -c Release %* || exit /b 1

set DLL=unity\bin\Release\netstandard2.1\usleep_win_cs.unity.dll
if exist "!DLL!" (
  echo [OK] DLL built: !DLL!
) else (
  echo [WARN] DLL not found at !DLL!
)
exit /b 0
