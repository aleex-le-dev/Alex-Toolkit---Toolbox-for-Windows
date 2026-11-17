@echo off
title Publication de la Release GitHub - Alex Toolkit
echo ========================================================
echo   Publication de la Release GitHub - Alex Toolkit
echo ========================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ScriptsWindows\publish_release.ps1"
echo.
pause
