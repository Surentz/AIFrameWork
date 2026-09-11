@echo off
rem Single double-clickable entry point for the two local setups (plain dev loop, kind cluster).
rem Each option is a thin call into the real .ps1 scripts under scripts\ and deploy\ - this file
rem has no logic of its own beyond the menu, so those scripts stay the source of truth.
setlocal
title AIFrameWork - Local Environment Control

:menu
cls
echo ============================================
echo   AIFrameWork - Local Environment Control
echo ============================================
echo.
echo   1. Install/check prerequisites (Docker, .NET SDK, Node.js, kind, k9s)
echo   2. Start dev loop        (dev Postgres + API + Vite, scripts\dev.ps1)
echo   3. Stop dev loop         (scripts\stop-dev.ps1)
echo   4. Start Kubernetes      (creates the kind cluster if missing, else redeploys)
echo   5. Stop Kubernetes       (deletes the kind cluster - Postgres data goes with it)
echo   6. Exit
echo.
set "choice="
set /p choice="Choose an option (1-6): "

if "%choice%"=="1" goto install_prereqs
if "%choice%"=="2" goto start_dev
if "%choice%"=="3" goto stop_dev
if "%choice%"=="4" goto start_k8s
if "%choice%"=="5" goto stop_k8s
if "%choice%"=="6" goto end

echo.
echo Not a valid option: %choice%
pause
goto menu

:install_prereqs
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\install-prereqs.ps1"
goto done

:start_dev
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\dev.ps1"
goto done

:stop_dev
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\stop-dev.ps1"
goto done

:start_k8s
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\deploy\start-cluster.ps1"
goto done

:stop_k8s
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\deploy\teardown.ps1"
goto done

:done
echo.
pause
goto menu

:end
endlocal
exit /b 0
