@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Install-Connector.ps1" %*
exit /b %ERRORLEVEL%
