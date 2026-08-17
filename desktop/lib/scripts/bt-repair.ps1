param($mac)
# TipPrint · bt-repair — desempareia e re-pareia a impressora Bluetooth para recriar a porta COM (corrige "Access denied")
# Se o Windows ja "esqueceu" o pareamento classico, faz um inquiry Bluetooth classico AO VIVO pela API
# Win32 legada (BluetoothFindFirstDevice) -- que funciona de qualquer processo, sem exigir app empacotado
# (a API nova WinRT/DeviceWatcher so retorna resultado pra apps com identidade de pacote UWP/MSIX) --
# e depois pareia pela API moderna (WinRT), sem precisar abrir Configuracoes do Windows manualmente.
$mac = (($mac -replace '[^0-9A-Fa-f]', '') -replace ':', '').ToUpper()
$result = [ordered]@{ ok = $false; unpaired = $false; paired = $false; status = 'notfound'; port = ''; ports = @(); discovery = ''; radioReset = $false }

Add-Type -AssemblyName System.Runtime.WindowsRuntime
[void][Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType=WindowsRuntime]
[void][Windows.Devices.Bluetooth.BluetoothDevice, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]
[void][Windows.Devices.Enumeration.DeviceInformationCustomPairing, Windows.Devices.Enumeration, ContentType=WindowsRuntime]
try { [void][Windows.Devices.Enumeration.DevicePairingRequestedEventArgs, Windows.Devices.Enumeration, ContentType=WindowsRuntime] } catch { }
try { [void][Windows.Devices.Radios.Radio, Windows.Devices.Radios, ContentType=WindowsRuntime] } catch { }

$btNativeSrc = @"
using System;
using System.Runtime.InteropServices;
public class TipPrintBtNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct SEARCH_PARAMS {
        public int dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEMTIME {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVICE_INFO {
        public int dwSize;
        public long Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }
    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr BluetoothFindFirstDevice(ref SEARCH_PARAMS p, ref DEVICE_INFO info);
    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool BluetoothFindNextDevice(IntPtr hFind, ref DEVICE_INFO info);
    [DllImport("bthprops.cpl")]
    public static extern bool BluetoothFindDeviceClose(IntPtr hFind);
}
"@
Add-Type -TypeDefinition $btNativeSrc -Language CSharp

# Dispara um inquiry classico real (~ cTimeoutMultiplier * 1.28s) e devolve $true se o MAC alvo apareceu.
function Invoke-LegacyInquiry($targetMac, $timeoutMultiplier) {
  $params = New-Object TipPrintBtNative+SEARCH_PARAMS
  $params.dwSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][TipPrintBtNative+SEARCH_PARAMS])
  $params.fReturnAuthenticated = $true
  $params.fReturnRemembered = $true
  $params.fReturnUnknown = $true
  $params.fReturnConnected = $true
  $params.fIssueInquiry = $true
  $params.cTimeoutMultiplier = $timeoutMultiplier
  $params.hRadio = [IntPtr]::Zero
  $info = New-Object TipPrintBtNative+DEVICE_INFO
  $info.dwSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][TipPrintBtNative+DEVICE_INFO])
  $foundTarget = $false
  try {
    $h = [TipPrintBtNative]::BluetoothFindFirstDevice([ref]$params, [ref]$info)
    if ($h -ne [IntPtr]::Zero) {
      do {
        $addrHex = "{0:X12}" -f ($info.Address -band 0xFFFFFFFFFFFF)
        if ($addrHex -eq $targetMac) { $foundTarget = $true }
        $info2 = New-Object TipPrintBtNative+DEVICE_INFO
        $info2.dwSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][TipPrintBtNative+DEVICE_INFO])
        $ok = [TipPrintBtNative]::BluetoothFindNextDevice($h, [ref]$info2)
        $info = $info2
      } while ($ok)
      [TipPrintBtNative]::BluetoothFindDeviceClose($h) | Out-Null
    }
  } catch { }
  return $foundTarget
}

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $resultType) {
  $task = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($op))
  $task.Wait()
  $task.Result
}

function Find-Printer-Known {
  try {
    $found = Await ([Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync([Windows.Devices.Bluetooth.BluetoothDevice]::GetDeviceSelector())) ([Windows.Devices.Enumeration.DeviceInformationCollection])
    foreach ($d in $found) {
      if ($d.Id -like 'Bluetooth#*' -and ($d.Id -replace '[^0-9A-Fa-f]', '').ToUpper().Contains($mac)) { return $d }
    }
  } catch { }
  return $null
}

# Conecta direto pelo endereco MAC (igual o Windows faz ao "lembrar" um endereco) sem precisar de
# descoberta/inquiry -- inquiry via DeviceWatcher exige capacidade "bluetooth" de app empacotado (UWP/MSIX)
# que um processo Win32/PowerShell comum nao tem, entao a rota abaixo e a que realmente funciona aqui.
function Find-Printer-ByAddress {
  try {
    $addr = [Convert]::ToUInt64($mac, 16)
    $bd = Await ([Windows.Devices.Bluetooth.BluetoothDevice]::FromBluetoothAddressAsync($addr)) ([Windows.Devices.Bluetooth.BluetoothDevice])
    if ($bd) { return $bd.DeviceInformation }
  } catch { }
  return $null
}

function Find-Printer {
  $d = Find-Printer-Known
  if ($d) { $result.discovery = 'known'; return $d }
  $seen = Invoke-LegacyInquiry $mac 8   # ~10s de inquiry classico real
  $d = Find-Printer-ByAddress
  if ($d) { $result.discovery = if ($seen) { 'inquiry+address' } else { 'by-address' } }
  return $d
}

function Get-ComPortForMac {
  $port = Get-PnpDevice -Class Ports -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId.ToUpper().Contains($mac) } | ForEach-Object {
    if ($_.FriendlyName -match '\(COM\d+\)') { ($Matches[0] -replace '[()]', '') }
  } | Select-Object -First 1
  return $port
}

function Wait-PortState($mac, $wantPresent, $seconds) {
  for ($i = 0; $i -lt $seconds; $i++) {
    $port = Get-ComPortForMac
    if (($wantPresent -and $port) -or (-not $wantPresent -and -not $port)) { return $port }
    Start-Sleep -Seconds 1
  }
  return ''
}

# Tenta parear (pareamento custom, com fallback pro pareamento simples) e devolve
# {paired, status} -- extraido do corpo principal pra poder chamar de novo depois
# de um reset de radio, sem duplicar a logica.
function Invoke-PairAttempt($diForPairing) {
  $out = [ordered]@{ paired = $false; status = '' }
  try {
    $custom = $diForPairing.Pairing.Custom
    [void]$custom.add_PairingRequested([Windows.Foundation.TypedEventHandler[Windows.Devices.Enumeration.DeviceInformationCustomPairing, Windows.Devices.Enumeration.DevicePairingRequestedEventArgs]]{
      param($sender, $args)
      switch ($args.PairingKind.ToString()) {
        'ProvidePin' { $args.Accept('0000') }
        default { $args.Accept() }
      }
    })
    $kinds = [Windows.Devices.Enumeration.DevicePairingKinds]'ConfirmOnly, DisplayPin, ProvidePin, ConfirmPinMatch'
    $r2 = Await ($custom.PairAsync($kinds, [Windows.Devices.Enumeration.DevicePairingProtectionLevel]::Default)) ([Windows.Devices.Enumeration.DevicePairingResult])
    $out.paired = ($r2.Status.ToString() -in @('Paired', 'AlreadyPaired'))
    $out.status = $r2.Status.ToString()
  } catch {
    $out.status = 'pair-error: ' + $_.Exception.Message
    try {
      $r2 = Await ($diForPairing.Pairing.PairAsync()) ([Windows.Devices.Enumeration.DevicePairingResult])
      $out.paired = ($r2.Status.ToString() -in @('Paired', 'AlreadyPaired'))
      $out.status = $r2.Status.ToString()
    } catch { $out.status = 'pair-error2: ' + $_.Exception.Message }
  }
  return $out
}

# Desliga e religa so o radio Bluetooth (equivalente a apertar o botao de aviao
# so pro BT nas Configuracoes) -- nao exige admin/UAC, ao contrario de
# Disable-PnpDevice. Usado como escalada quando o re-pareamento falha mesmo
# depois de redescobrir o dispositivo: o sintoma real visto em producao
# (15/08/2026) era o radio USB Bluetooth sumindo/reaparecendo sozinho (pnpId
# pai mudando entre tentativas), nao so a sessao de pareamento — religar o
# radio forca o Windows a reconstruir a pilha BT inteira, inclusive a porta
# COM, sem precisar trocar a porta USB fisica na mao.
# Extraido pra bt-radio-reset.ps1 (script standalone) em 15/08/2026 pra tambem
# ser reaproveitado pelo AdapterMonitor do PrintServer.cs, sem duplicar a logica.
function Reset-BluetoothRadio {
  try {
    $out = & (Join-Path $PSScriptRoot 'bt-radio-reset.ps1')
    return ($out -join '') -match '^OK$'
  } catch { return $false }
}

$di = Find-Printer
if ($di) {
  try {
    $r = Await ($di.Pairing.UnpairAsync()) ([Windows.Devices.Enumeration.DeviceUnpairingResult])
    $result.unpaired = ($r.Status.ToString() -in @('Unpaired', 'AlreadyUnpaired'))
  } catch { $result.status = 'unpair-error: ' + $_.Exception.Message }
  Wait-PortState $mac $false 15 | Out-Null

  # Redescobre o dispositivo depois do Unpair -- o $di antigo pode carregar
  # estado de pareamento obsoleto pro WinRT (a sessao de pairing anterior),
  # fazendo o PairAsync() de baixo retornar "Failed" mesmo com o unpair tendo
  # funcionado. Achado real (15/08/2026): bt-repair falhando repetidamente em
  # producao com "unpaired": true mas "status": "Failed" no re-pareamento,
  # deixando a impressora sem porta COM e o Windows ainda mostrando "Paired"
  # (estado ambiguo, pior que antes de rodar o reparo). So troca $di se achou
  # de novo -- se nao achou (ainda nao reapareceu no discovery), segue com o
  # $di antigo em vez de falhar cedo.
  $diFresh = Find-Printer
  if ($diFresh) { $di = $diFresh }

  $attempt = Invoke-PairAttempt $di
  $result.paired = $attempt.paired
  $result.status = $attempt.status

  # Escalada: se nem redescobrindo o dispositivo o pareamento colou, tenta
  # religar o radio Bluetooth e repetir o ciclo (descoberta + pareamento) mais
  # uma vez antes de desistir. Ver comentario da Reset-BluetoothRadio acima.
  if (-not $result.paired) {
    $result.radioReset = Reset-BluetoothRadio
    if ($result.radioReset) {
      $diAfterReset = Find-Printer
      if ($diAfterReset) { $di = $diAfterReset }
      $attempt2 = Invoke-PairAttempt $di
      $result.paired = $attempt2.paired
      $result.status = if ($attempt2.paired) { $attempt2.status + ' (apos reset do radio)' } else { $result.status + ' | apos reset do radio: ' + $attempt2.status }
    }
  }

  if ($result.paired) {
    $result.port = Wait-PortState $mac $true 25
    $result.ok = [bool]$result.port
  }
} else {
  $result.status = 'notfound'
}

$result.ports = @(Get-PnpDevice -Class Ports -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match '\(COM\d+\)' } | ForEach-Object { if ($_.FriendlyName -match '\(COM\d+\)') { $Matches[0].Trim('()') } })
$result | ConvertTo-Json -Compress
