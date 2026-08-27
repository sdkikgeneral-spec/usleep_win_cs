@echo off
setlocal ENABLEDELAYEDEXPANSION
cd /d %~dp0

echo === Building Unity Windows-only DLL (netstandard2.1, P/Invoke enabled) ===
where dotnet >nul 2>&1 || (echo [ERROR] dotnet CLI not found. Install .NET SDK. & exit /b 1)

dotnet restore unity\usleep_win_cs.unity.csproj || exit /b 1

rem MSBuild へ ';' を渡すには %%3B とエスケープする。バッチファイル内で %3B と
rem 書くと cmd が「3番目の引数 %3」+「B」として展開してしまい、定数が
rem USLP_UNITYBUSLP_WINDOWS という 1 個の別物に化ける（USLP_UNITY が定義されず、
rem PreciseDelay 系が Unity ビルドから除外されずにコンパイルエラーになる）。
dotnet build unity\usleep_win_cs.unity.csproj -c Release -p:DefineConstants=USLP_UNITY%%3BUSLP_WINDOWS %* || exit /b 1

set DLL=unity\bin\Release\netstandard2.1\usleep_win_cs.unity.dll
if exist "!DLL!" (
  echo [OK] DLL built: !DLL!
  echo [INFO] Limit asmdef platforms to Windows Editor / Windows Standalone.
) else (
  echo [WARN] DLL not found at !DLL!
)
exit /b 0
