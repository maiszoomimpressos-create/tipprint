@echo off
title Instalacao - TipPrint PrintServer
echo ============================================================
echo   TipPrint PrintServer - Instalacao
echo ============================================================
echo.

set DEST=%LOCALAPPDATA%\TipPrint

echo [1/6] Copiando o programa...
if not exist "%DEST%" mkdir "%DEST%"
copy /Y "%~dp0PrintServer.exe" "%DEST%\PrintServer.exe" >nul
if errorlevel 1 (
    echo ERRO: nao foi possivel copiar o programa.
    pause
    exit /b 1
)
rem Pacotes provisionados (baixados via tipo7.com) vem com um config.txt proprio, ja
rem com a chave de instalacao - copia se existir. Pacotes genericos (sem essa chave)
rem nao tem esse arquivo, entao esta linha nao faz nada neles.
if exist "%~dp0config.txt" copy /Y "%~dp0config.txt" "%DEST%\config.txt" >nul

echo [2/6] Ativando inicio com o Windows...
rem O -Command de uma linha so com aspas aninhadas (cmd -> powershell -> "-Command"
rem interno) e' fragil - cmd.exe nao entende \" como aspas escapada do jeito que o
rem PowerShell precisa, e a linha quebra dependendo do %APPDATA%/%DEST% de cada PC
rem (bug real, achado em instalacao de verdade em 2026-08-14: "A cadeia de caracteres
rem nao tem o terminador"). Escrever um .ps1 de verdade evita o problema de vez -
rem o parser do PowerShell le o arquivo direto, sem o cmd.exe reinterpretar aspas.
rem WindowStyle=7 (minimizado) no atalho - documentado, ao contrario de "Hidden" que
rem nao e' um valor valido pra atalho (so' funciona em Start-Process, usado abaixo).
> "%TEMP%\tipprint-startup.ps1" (
    echo $s = ^(New-Object -ComObject WScript.Shell^).CreateShortcut^('%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\TipPrint PrintServer.lnk'^)
    echo $s.TargetPath = '%DEST%\PrintServer.exe'
    echo $s.Arguments = '8080 9100 "%DEST%\printserver.log"'
    echo $s.WorkingDirectory = '%DEST%'
    echo $s.WindowStyle = 7
    echo $s.Save^(^)
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\tipprint-startup.ps1" >nul 2>&1
del "%TEMP%\tipprint-startup.ps1" >nul 2>&1

echo [3/6] Criando icone na area de trabalho...
rem Icone que abre o painel (localhost:8080) no navegador - nao faz sentido "abrir" o
rem PrintServer em si (ele nao tem janela, so' roda por tras) - .url e' so' um arquivo
rem de texto, nao precisa de COM/PowerShell.
> "%USERPROFILE%\Desktop\TipPrint.url" (
    echo [InternetShortcut]
    echo URL=http://localhost:8080
)

echo [4/6] Ajustando energia do USB (evita desconexoes)...
net session >nul 2>&1
if %errorlevel%==0 (
    reg add HKLM\SYSTEM\CurrentControlSet\Services\USB /v DisableSelectiveSuspend /t REG_DWORD /d 1 /f >nul 2>&1
    echo    - Economia de energia USB desativada. (em alguns PCs pode pedir reiniciar)
) else (
    echo    - AVISO: Execute o instalador como Administrador para desativar
    echo      a economia de energia USB (evita a impressora desconectar sozinha).
)

echo [5/6] Iniciando o Agent (sem janela de console)...
rem "start" sozinho deixava a janela preta do PrintServer.exe aberta na tela (ele e'
rem um app de console) - Start-Process -WindowStyle Hidden do PowerShell esconde de
rem verdade, o Startup shortcut acima (WindowStyle 7) cuida das proximas vezes que o
rem Windows ligar.
> "%TEMP%\tipprint-launch.ps1" (
    echo Start-Process -FilePath '%DEST%\PrintServer.exe' -ArgumentList '8080','9100','%DEST%\printserver.log' -WorkingDirectory '%DEST%' -WindowStyle Hidden
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\tipprint-launch.ps1" >nul 2>&1
del "%TEMP%\tipprint-launch.ps1" >nul 2>&1

echo [6/6] Instalando o TipPrint Desktop (app de configuracao)...
rem So' existe nos pacotes provisionados pelo tipo7.com (baixados via /provision) - o
rem pacote generico (dist/TipPrintPrintServer.zip, download direto do site) so' tem o
rem PrintServer. "if exist" faz esse mesmo Instalar.bat servir os dois casos sem
rem duplicar script. /S = instalacao silenciosa do instalador NSIS (electron-builder).
if exist "%~dp0app-windows.exe" (
    "%~dp0app-windows.exe" /S
    ping -n 6 127.0.0.1 >nul
    start "" "%ProgramFiles%\TipPrint\TipPrint.exe"
) else (
    echo    - Pacote sem o Desktop app ^(so' o Agent^) - nada a fazer aqui.
)

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
ping -n 3 127.0.0.1 >nul
if exist "%~dp0app-windows.exe" (
    echo   2) O TipPrint Desktop ja abriu sozinho - escolha sua impressora nele.
) else (
    echo   2) O painel abre sozinho no navegador - clique na sua impressora na lista.
    start http://localhost:8080
)
echo   3) Pronto! O sistema ja vai conseguir imprimir nela.
echo.
echo   Um icone "TipPrint" foi criado na area de trabalho - abre o painel a
echo   qualquer momento. O programa inicia sozinho (minimizado) sempre que o
echo   computador liga.
echo.
pause
