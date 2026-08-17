# TipPrint PrintServer - script de build.
#
# Nao existia um script de build no repo antes (compilacao era manual, csc.exe direto).
# Criado em 2026-08-15 junto com o BluetoothRfcommTransport.
#
# BluetoothRfcommTransport usa WinRT (Windows.Devices.Bluetooth.Rfcomm +
# Windows.Networking.Sockets), o que exige o Windows SDK instalado nesta maquina (pro
# csc.exe resolver IAsyncOperation/AsTask etc.). Achado nesta sessao: referenciar dezenas
# de arquivos .winmd de contrato separados (sem o SDK) faz o csc.exe falhar de forma
# imprevisivel (CS0012). O arquivo UNIFICADO que o SDK gera
# (Windows Kits\10\UnionMetadata\<versao>\Windows.winmd) resolve isso de forma limpa e
# estavel - e' o mesmo arquivo que o Visual Studio referencia por baixo dos panos quando
# voce adiciona "Windows" como referencia num projeto .NET Framework.
#
# Uso:
#   powershell -File dist\build.ps1                 # compila pra dist\PrintServer.exe
#   powershell -File dist\build.ps1 -OutDir C:\tmp   # compila pra outro lugar (teste)

param(
    [string]$OutDir = $PSScriptRoot
)

$csc = "C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$netRuntime = "C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll"
$src = $PSScriptRoot

# Acha a versao mais recente do Windows.winmd (facade) e do
# Windows.Foundation.UniversalApiContract.winmd (contrato de verdade - o facade so'
# encaminha pra ele, precisa dos dois) instalados pelo SDK.
$unionRoot = "C:\Program Files (x86)\Windows Kits\10\UnionMetadata"
$refsRoot = "C:\Program Files (x86)\Windows Kits\10\References"
$unionWinmd = $null
$contractWinmd = $null
if (Test-Path $unionRoot) {
    $ver = Get-ChildItem $unionRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } | Sort-Object Name -Descending | Select-Object -First 1
    if ($ver) {
        $candidate = Join-Path $ver.FullName "Windows.winmd"
        if (Test-Path $candidate) { $unionWinmd = $candidate }
        $contractDir = Join-Path $refsRoot "$($ver.Name)\Windows.Foundation.UniversalApiContract"
        if (Test-Path $contractDir) {
            $contractVer = Get-ChildItem $contractDir -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
            if ($contractVer) {
                $c = Join-Path $contractVer.FullName "Windows.Foundation.UniversalApiContract.winmd"
                if (Test-Path $c) { $contractWinmd = $c }
            }
        }
    }
}
if (-not $unionWinmd -or -not $contractWinmd) {
    Write-Error "Windows.winmd/UniversalApiContract.winmd (Windows SDK) nao encontrados - instale o Windows SDK (winget install Microsoft.WindowsSDK.10.0.18362) antes de compilar."
    exit 1
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

& $csc /nologo /target:exe /platform:anycpu /out:"$OutDir\PrintServer.exe" `
    /reference:System.Management.dll /reference:System.Web.Extensions.dll `
    /reference:System.Runtime.WindowsRuntime.dll /reference:"$netRuntime" /reference:"$unionWinmd" `
    "$src\PrintServer.cs" "$src\PrintTransport.cs" "$src\ConnectionManager.cs" "$src\AdapterMonitor.cs" "$src\QrEncoder.cs"

$code = $LASTEXITCODE
if ($code -eq 0) {
    Write-Output "Build OK: $OutDir\PrintServer.exe"
} else {
    Write-Output "Build FALHOU (codigo $code)"
}
exit $code
