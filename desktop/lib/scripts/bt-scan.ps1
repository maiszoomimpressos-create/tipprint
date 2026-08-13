Add-Type -AssemblyName System.Runtime.WindowsRuntime
[void][Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType=WindowsRuntime]
[void][Windows.Devices.Bluetooth.BluetoothDevice, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $resultType) {
  $task = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($op))
  $task.Wait()
  $task.Result
}

$devices = @{}
try {
  $found = Await ([Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync([Windows.Devices.Bluetooth.BluetoothDevice]::GetDeviceSelector())) ([Windows.Devices.Enumeration.DeviceInformationCollection])
  foreach ($d in $found) {
    $parts = ($d.Id -split '#')[-1] -split '-'
    $mac = $parts[$parts.Count - 1]
    $key = $mac.ToUpper().Replace(':', '')
    if ($key) { $devices[$key] = [PSCustomObject]@{ Name = $d.Name; Paired = $d.Pairing.IsPaired } }
  }
} catch { }

try {
  Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices' -ErrorAction SilentlyContinue | ForEach-Object {
    $key = $_.PSChildName.ToUpper()
    $bin = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).Name
    $name = ''
    if ($bin -is [byte[]]) {
      $ascii = [System.Text.Encoding]::ASCII.GetString($bin).TrimEnd([char]0)
      if ($ascii -notmatch '\?') { $name = $ascii.Trim() }
      else { $name = [System.Text.Encoding]::Unicode.GetString($bin).TrimEnd([char]0).Trim() }
    }
    if (-not $devices.ContainsKey($key)) { $devices[$key] = [PSCustomObject]@{ Name = $name; Paired = $true } }
    elseif ($name -and -not $devices[$key].Name) { $devices[$key].Name = $name }
  }
} catch { }

try {
  Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'BTHENUM' } | ForEach-Object {
    if ($_.InstanceId -match 'BLUETOOTHDEVICE_([0-9A-F]{12})') {
      $key = $Matches[1]
      if ($devices.ContainsKey($key) -and -not $devices[$key].Name -and $_.FriendlyName) { $devices[$key].Name = $_.FriendlyName }
    }
  }
} catch { }

$out = @()
foreach ($entry in $devices.GetEnumerator()) {
  $mac = $entry.Key
  $macFmt = if ($mac.Length -eq 12) { ($mac -replace '(.{2})(?=.)', '$1:') } else { $mac }
  $out += [PSCustomObject]@{ Name = $entry.Value.Name; Address = $macFmt; Paired = $entry.Value.Paired }
}
$out | ConvertTo-Json -Compress
