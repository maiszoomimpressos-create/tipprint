@echo off
title Instalacao - TipPrint PrintServer
echo ============================================================
echo   TipPrint PrintServer - Instalacao
echo ============================================================
echo.

set DEST=%LOCALAPPDATA%\TipPrint

echo [1/4] Copiando o programa...
if not exist "%DEST%" mkdir "%DEST%"
copy /Y "%~dp0PrintServer.exe" "%DEST%\PrintServer.exe" >nul
if errorlevel 1 (
    echo ERRO: nao foi possivel copiar o programa.
    pause
    exit /b 1
)

echo [2/4] Ativando inicio com o Windows...
powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\TipPrint PrintServer.lnk');$s.TargetPath='powershell.exe';$s.Arguments='-NoProfile -WindowStyle Hidden -Command \"& ''%DEST%\PrintServer.exe'' 8080 9100 ''%DEST%\printserver.log''\"';$s.WorkingDirectory='%DEST%';$s.Save()"

echo [3/4] Ajustando energia do USB (evita desconexoes)...
net session >nul 2>&1
if %errorlevel%==0 (
    reg add HKLM\SYSTEM\CurrentControlSet\Services\USB /v DisableSelectiveSuspend /t REG_DWORD /d 1 /f >nul 2>&1
    echo    - Economia de energia USB desativada. (em alguns PCs pode pedir reiniciar)
) else (
    echo    - AVISO: Execute o instalador como Administrador para desativar
    echo      a economia de energia USB (evita a impressora desconectar sozinha).
)

echo [4/4] Iniciando o programa...
start "" "%DEST%\PrintServer.exe" 8080 9100 "%DEST%\printserver.log"

echo.
echo ============================================================
echo   INSTALACAO CONCLUIDA
echo ============================================================
echo.
echo   Agora:
echo   1) O Windows vai abrir as configuracoes de Bluetooth.
echo      Ligue a impressora e pareie. (PIN padrao: 0000)
echo.
start ms-settings:bluetooth
echo   2) Depois abra no navegador:  http://localhost:8080
echo      e clique na sua impressora na lista.
echo   3) Pronto! O sistema ja vai conseguir imprimir nela.
echo.
echo   O programa inicia sozinho sempre que o computador liga.
echo   Para testar: feche e reabra o programa desligando/ligando o PC.
echo.
pause