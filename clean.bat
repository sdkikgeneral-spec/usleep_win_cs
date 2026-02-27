@echo off
setlocal
cd /d %~dp0

echo === Cleaning build outputs ===
where dotnet >nul 2>&1 && (
  dotnet clean pack\usleep_win_cs.nupkg.csproj -c Release >nul 2>&1
  dotnet clean unity\usleep_win_cs.unity.csproj -c Release >nul 2>&1
) >nul 2>&1

for %%d in (pack unity src samples) do (
  if exist "%%d\bin" rd /s /q "%%d\bin" 2>nul
  if exist "%%d\obj" rd /s /q "%%d\obj" 2>nul
)

echo [OK] Clean done.
exit /b 0
