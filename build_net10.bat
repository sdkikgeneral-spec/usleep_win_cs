@echo off
setlocal ENABLEDELAYEDEXPANSION
cd /d %~dp0

echo === Building NuGet package (.NET 10, Windows) ===
where dotnet >nul 2>&1 || (echo [ERROR] dotnet CLI not found. Install .NET SDK. & exit /b 1)

rem Optional: pass extra MSBuild properties via arguments (e.g. -p:PackageVersion=0.1.1)
dotnet restore pack\usleep_win_cs.nupkg.csproj || exit /b 1

dotnet pack pack\usleep_win_cs.nupkg.csproj -c Release %* || exit /b 1

set PKG=
for /f "delims=" %%i in ('dir /b /s pack\bin\Release\*.nupkg 2^>nul') do set PKG=%%i
if defined PKG (
  echo [OK] Package built: !PKG!
) else (
  echo [WARN] No .nupkg found under pack\bin\Release
)
exit /b 0
