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
echo   1. Install/check prerequisites (Docker, .NET SDK, Node.js, kind, k9s, Playwright)
echo   2. Start dev loop        (dev Postgres + API + Vite, scripts\dev.ps1)
echo   3. Start dev loop + Seq  (same, plus a structured log UI at localhost:55341)
echo   4. Stop dev loop         (scripts\stop-dev.ps1 - also stops Seq, if it was started)
echo   5. Start Kubernetes      (creates the kind cluster if missing, else redeploys)
echo   6. Stop Kubernetes       (deletes the kind cluster - Postgres data goes with it)
echo   7. Run e2e tests         (local stack - stop the dev loop first, it uses port 5234)
echo   8. Run e2e tests         (against Kubernetes - deploy it first with option 5)
echo   9. Open last e2e report
echo   10. Exit
echo.
set "choice="
set /p choice="Choose an option (1-10): "

if "%choice%"=="1" goto install_prereqs
if "%choice%"=="2" goto start_dev
if "%choice%"=="3" goto start_dev_seq
if "%choice%"=="4" goto stop_dev
if "%choice%"=="5" goto start_k8s
if "%choice%"=="6" goto stop_k8s
if "%choice%"=="7" goto run_e2e
if "%choice%"=="8" goto run_e2e_k8s
if "%choice%"=="9" goto open_report
if "%choice%"=="10" goto end

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

:start_dev_seq
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\dev.ps1" -WithSeq
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

:run_e2e
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\e2e.ps1"
goto done

:run_e2e_k8s
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\deploy\e2e-k8s.ps1"
goto done

:open_report
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\e2e-report.ps1"
goto done

:done
echo.
pause
goto menu

:end
endlocal
exit /b 0
