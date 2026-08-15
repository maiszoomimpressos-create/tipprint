@echo off
title Remocao - TipPrint PrintServer
echo ============================================================
echo   Removendo o TipPrint PrintServer...
echo ============================================================
set DEST=%LOCALAPPDATA%\TipPrint

taskkill /f /im PrintServer.exe >nul 2>&1

if exist "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\TipPrint PrintServer.lnk" (
    del "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\TipPrint PrintServer.lnk" >nul 2>&1
)

if exist "%USERPROFILE%\Desktop\TipPrint.url" (
    del "%USERPROFILE%\Desktop\TipPrint.url" >nul 2>&1
)

if exist "%DEST%" rmdir /s /q "%DEST%"

echo.
echo   Programa removido. O Windows tambem nao vai mais iniciar ele junto.
echo.
pause