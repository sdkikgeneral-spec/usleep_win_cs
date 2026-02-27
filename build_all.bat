@echo off
setlocal
cd /d %~dp0

echo ### Build All Targets ###
call "%~dp0build_net10.bat" %*
if errorlevel 1 exit /b 1

call "%~dp0build_unity_generic.bat" %*
if errorlevel 1 exit /b 1

call "%~dp0build_unity_windows.bat" %*
if errorlevel 1 exit /b 1

echo.
echo [OK] All builds completed.
exit /b 0
