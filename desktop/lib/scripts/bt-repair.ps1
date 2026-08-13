param($mac)
# TipPrint · bt-repair — desempareia e re-pareia a impressora Bluetooth para recriar a porta COM (corrige "Access denied")
$mac = (($mac -replace '[^0-9A-Fa-f]', '') -replace ':', '').ToUpper()
$result = [ordered]@{ ok = $false; unpaired = $false; paired = $false; status = 'notfound'; port = ''; ports = @() }

Add-Type -AssemblyName System.Runtime.WindowsRuntime
[void][Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType=WindowsRuntime]
[void][Windows.Devices.Bluetooth.BluetoothDevice, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]
try { [void][Windows.Devices.Enumeration.DevicePairingRequestedEventArgs, Windows.Devices.Enumeration, ContentType=WindowsRuntime] } catch { }

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $resultType) {
  $task = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($op))
  $task.Wait()
  $task.Result
}

function Find-Printer {
  try {
    $found = Await ([Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync([Windows.Devices.Bluetooth.BluetoothDevice]::GetDeviceSelector())) ([Windows.Devices.Enumeration.DeviceInformationCollection])
    foreach ($d in $found) {
      if ($d.Id -like 'Bluetooth#*' -and ($d.Id -replace '[^0-9A-Fa-f]', '').ToUpper().Contains($mac)) { return $d }
    }
  } catch { }
  return $null
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

$di = Find-Printer
if ($di) {
  try {
    $r = Await ($di.Pairing.UnpairAsync()) ([Windows.Devices.Enumeration.DeviceUnpairingResult])
    $result.unpaired = ($r.Status.ToString() -in @('Unpaired', 'AlreadyUnpaired'))
  } catch { $result.status = 'unpair-error: ' + $_.Exception.Message }
  Wait-PortState $mac $false 15 | Out-Null
  try {
    $pairing = $di.Pairing
    try {
      $pairing.add_PairingRequested([Windows.Devices.Enumeration.TypedEventHandler[Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration.DevicePairingRequestedEventArgs]]{
        param($sender, $args) $args.AcceptWithPin('0000')
      })
    } catch { }
    $r2 = Await ($pairing.PairAsync()) ([Windows.Devices.Enumeration.DevicePairingResult])
    $result.paired = ($r2.Status.ToString() -in @('Paired', 'AlreadyPaired'))
    $result.status = $r2.Status.ToString()
  } catch {
    $result.status = 'pair-error: ' + $_.Exception.Message
    try {
      $pairing = $di.Pairing
      $r2 = Await ($pairing.PairAsync()) ([Windows.Devices.Enumeration.DevicePairingResult])
      $result.paired = ($r2.Status.ToString() -in @('Paired', 'AlreadyPaired'))
      $result.status = $r2.Status.ToString()
    } catch { $result.status = 'pair-error2: ' + $_.Exception.Message }
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
