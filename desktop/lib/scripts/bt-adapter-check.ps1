# Diagnostico do(s) adaptador(es) Bluetooth do Windows. Criado depois de um caso real (2026-08-14):
# a impressora KP-1025 parava de conectar (SEMPRE com "dispositivo inexistente"), sobrevivendo a
# religar a impressora e a reparar/re-parear pelo Windows - a causa de verdade era o driver RFCOMM
# (Bluetooth classico/SPP, o que vira porta COM) do adaptador USB com "Codigo 10: nao pode iniciar".
# Reparar pareamento nunca resolveria isso, porque o problema nao e' o pareamento.
#
# Saida: JSON com os problemas achados nos dispositivos de classe Bluetooth (adaptador em si ou
# qualquer sub-dispositivo dele, como o RFCOMM) e quantos adaptadores estao presentes (mais de um
# ativo ao mesmo tempo e' fonte conhecida de instabilidade).

$problems = @()
Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.Class -eq 'Bluetooth' -and $_.Status -eq 'Error' } | ForEach-Object {
  $parentName = ''
  try {
    $parentId = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_Parent -ErrorAction SilentlyContinue).Data
    if ($parentId) {
      $parentDev = Get-PnpDevice -InstanceId $parentId -ErrorAction SilentlyContinue
      if ($parentDev) { $parentName = $parentDev.FriendlyName }
    }
  } catch {}
  $problems += [PSCustomObject]@{
    Device        = $_.FriendlyName
    InstanceId    = $_.InstanceId
    Problem       = [string]$_.ConfigManagerErrorCode
    ParentAdapter = $parentName
  }
}

# Anda pela arvore de dispositivos-pai (USB) ate achar um "Root Hub" e ver se e' USB 3.x -
# adaptadores Bluetooth USB sao conhecidos por cair/nao segurar o pareamento em portas 3.0
# por interferencia de radio (a banda 3.0 SuperSpeed sobrepoe a frequencia do Bluetooth
# 2.4GHz). Caso real (2026-08-14): KP-1025 pareava e o Windows desfazia o vinculo sozinho
# em segundos; trocar o adaptador pra uma porta USB 2.0 resolveu.
function Test-OnUsb3 ($instanceId) {
  $cur = $instanceId
  for ($i = 0; $i -lt 6; $i++) {
    $dev = Get-PnpDevice -InstanceId $cur -ErrorAction SilentlyContinue
    if (-not $dev) { return $false }
    if ($dev.FriendlyName -match 'Root Hub' -and $dev.FriendlyName -match '3\.\d') { return $true }
    if ($dev.FriendlyName -match 'Root Hub' -and $dev.FriendlyName -notmatch '3\.\d') { return $false }
    $parent = (Get-PnpDeviceProperty -InstanceId $cur -KeyName DEVPKEY_Device_Parent -ErrorAction SilentlyContinue).Data
    if (-not $parent -or $parent -eq $cur) { return $false }
    $cur = $parent
  }
  return $false
}

$adapters = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
  $_.Class -eq 'Bluetooth' -and $_.FriendlyName -match 'Adapter' -and $_.Status -ne 'Unknown'
} | ForEach-Object {
  [PSCustomObject]@{
    FriendlyName = $_.FriendlyName
    Status       = [string]$_.Status
    InstanceId   = $_.InstanceId
    OnUsb3       = if ($_.InstanceId -match '^USB\\') { Test-OnUsb3 $_.InstanceId } else { $false }
  }
})

[PSCustomObject]@{
  Problems     = $problems
  AdapterCount = $adapters.Count
  Adapters     = $adapters
} | ConvertTo-Json -Compress -Depth 4
