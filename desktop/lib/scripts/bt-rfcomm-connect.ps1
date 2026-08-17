# TipPrint - bt-rfcomm-connect: helper de conexao RFCOMM direta (Bluetooth Classic/SPP),
# sem passar por COM3/COM4/BTHMODEM. Chamado pelo BluetoothRfcommTransport (PrintTransport.cs)
# como subprocesso de vida longa (fica aberto enquanto a conexao existir).
#
# Por que subprocesso PowerShell em vez de WinRT direto no PrintServer.exe: o PrintServer e'
# compilado com csc.exe puro (.NET Framework 4.x, sem projeto MSBuild/Visual Studio) e essa
# maquina nao tem o Windows SDK instalado (sem os .winmd de contrato em
# Windows Kits\...\References) - sem eles, consumir Windows.Devices.Bluetooth.Rfcomm direto
# em C# compilado da erro de resolucao de tipo (CS0012, testado e documentado nesta sessao).
# O PowerShell resolve isso em runtime via [Type]::GetType com ContentType=WindowsRuntime,
# sem precisar desses .winmd - e' o MESMO mecanismo ja usado e comprovado em
# bt-scan.ps1/bt-repair.ps1/bt-radio-reset.ps1 deste repo.
#
# Baseado diretamente nos testes desta sessao (2026-08-15) contra a KP-1025
# (86:67:7A:B6:30:57): SDP via WinRT confirmado, RFCOMM direto confirmado (multiplas
# conexoes bem-sucedidas), 6/6 power-cycles reconectados (com erro transitorio
# "ConnectAsync: Ponteiro invalido" logo apos a impressora voltar - por isso o RETRY fica
# a cargo de quem chama este script, nao daqui: cada chamada e' UMA tentativa, sem loop
# interno de retry).
#
# Protocolo (stdin/stdout, uma linha por comando/resposta):
#   Ao iniciar: "READY" (conectou) ou "ERROR: <motivo>" + exit 1 (falhou)
#   "WRITE <base64>"  -> escreve os bytes no RFCOMM -> responde "OK" ou "ERROR: <motivo>"
#   "PING"            -> checa BluetoothDevice.ConnectionStatus -> "OK" ou "DISCONNECTED: <motivo>"
#   "CLOSE"           -> fecha o socket e encerra (exit 0)
#   Se o processo cair sozinho (pipe fecha / EOF), quem chama trata como desconexao.
#
# NUNCA escreve nada por conta propria - so' os bytes que vierem via "WRITE" (o TipPrint
# quem monta o ESC/POS). Nao remove pareamento, nao mexe em driver/adaptador.

param(
    [Parameter(Mandatory = $true)][string]$Mac
)

function Out($line) {
    [Console]::Out.WriteLine($line)
    [Console]::Out.Flush()
}

try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    [void][Windows.Devices.Bluetooth.BluetoothDevice, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]
    [void][Windows.Devices.Bluetooth.Rfcomm.RfcommDeviceService, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]
    [void][Windows.Devices.Bluetooth.Rfcomm.RfcommServiceId, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]
    [void][Windows.Networking.Sockets.StreamSocket, Windows.Networking.Sockets, ContentType=WindowsRuntime]
    [void][Windows.Storage.Streams.Buffer, Windows.Storage.Streams, ContentType=WindowsRuntime]
    [void][Windows.Security.Cryptography.CryptographicBuffer, Windows.Security.Cryptography, ContentType=WindowsRuntime]
} catch {
    Out ("ERROR: falha ao carregar API Bluetooth do Windows - " + $_.Exception.Message)
    exit 1
}

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
$asTaskAction = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]
function Await($op, $resultType) { $t = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($op)); $t.Wait(); $t.Result }
function AwaitAction($op) { $t = $asTaskAction.Invoke($null, @($op)); $t.Wait() }

$macHex = ($Mac -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
$addr = 0
try { $addr = [Convert]::ToUInt64($macHex, 16) } catch { Out "ERROR: MAC invalido"; exit 1 }

$sock = $null
try {
    # Redescobre/valida o dispositivo a cada tentativa de conexao (nunca usa um handle
    # velho) - e' a mesma logica provada nos testes desta sessao.
    $bd = Await ([Windows.Devices.Bluetooth.BluetoothDevice]::FromBluetoothAddressAsync($addr)) ([Windows.Devices.Bluetooth.BluetoothDevice])
    if (-not $bd) { Out "ERROR: dispositivo nao encontrado (fora de alcance ou desligado)"; exit 1 }

    # SDP AO VIVO (Uncached) - nunca presume o canal/servico, confirma de verdade toda vez.
    $spp = [Windows.Devices.Bluetooth.Rfcomm.RfcommServiceId]::SerialPort
    $svcResult = Await ($bd.GetRfcommServicesForIdAsync($spp, [Windows.Devices.Bluetooth.BluetoothCacheMode]::Uncached)) ([Windows.Devices.Bluetooth.Rfcomm.RfcommDeviceServicesResult])
    if ($svcResult.Services.Count -eq 0) {
        Out ("ERROR: servico Serial Port Profile (0x1101) nao encontrado via SDP (Status=" + $svcResult.Error + ")")
        exit 1
    }
    $svc = $svcResult.Services[0]

    $sock = New-Object Windows.Networking.Sockets.StreamSocket
    AwaitAction ($sock.ConnectAsync($svc.ConnectionHostName, $svc.ConnectionServiceName))
} catch {
    Out ("ERROR: " + $_.Exception.Message)
    exit 1
}

Out "READY"

while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }

    if ($line -eq "CLOSE") {
        break
    }
    elseif ($line -eq "PING") {
        try {
            $status = $bd.ConnectionStatus.ToString()
            if ($status -eq "Connected") { Out "OK" }
            else { Out ("DISCONNECTED: BluetoothDevice.ConnectionStatus = " + $status) }
        } catch {
            Out ("DISCONNECTED: " + $_.Exception.Message)
        }
    }
    elseif ($line.StartsWith("WRITE ")) {
        try {
            $bytes = [Convert]::FromBase64String($line.Substring(6))
            # CryptographicBuffer.CreateFromByteArray devolve um IBuffer ja' tipado pelo
            # WinRT (sem precisar de cast manual) - evita o construtor de DataWriter, que
            # nao resolve bem a partir do PowerShell (testado e documentado nesta sessao:
            # New-Object DataWriter($sock.OutputStream) falha com erro de overload).
            $buffer = [Windows.Security.Cryptography.CryptographicBuffer]::CreateFromByteArray($bytes)
            $outStream = [Windows.Storage.Streams.IOutputStream]$sock.OutputStream
            Await ($outStream.WriteAsync($buffer)) ([uint32]) | Out-Null
            Out "OK"
        } catch {
            Out ("ERROR: " + $_.Exception.Message)
        }
    }
    else {
        Out "ERROR: comando desconhecido"
    }
}

try { if ($sock) { $sock.Dispose() } } catch { }
exit 0
