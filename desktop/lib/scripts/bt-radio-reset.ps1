# TipPrint · bt-radio-reset — desliga e religa SO' o radio Bluetooth (equivalente a apertar
# o botao de aviao so' pro BT nas Configuracoes do Windows). Nao exige admin/UAC, ao
# contrario de Disable-PnpDevice/Enable-PnpDevice/pnputil.
#
# Extraido da funcao Reset-BluetoothRadio que ja existia embutida em bt-repair.ps1
# (adicionada em 15/08/2026 como escalada do reparo de pareamento) - agora fica como
# script standalone pra tambem ser chamado pelo AdapterMonitor do PrintServer.cs (camada
# de recuperacao automatica do adaptador), sem duplicar a logica.
#
# Por que so' isso e nao Disable/Enable do dispositivo: investigacao real nesta sessao
# (2026-08-15) mostrou que Disable-PnpDevice num adaptador com o driver travado pode ficar
# preso em "aguardando reinicializacao do Windows" - ou seja, a "cura" pode piorar o
# problema. O toggle de radio via WinRT nao mexe no estado habilitado/desabilitado do
# dispositivo no Gerenciador de Dispositivos, so' liga/desliga o radio - risco bem menor.
#
# Saida (uma linha, stdout): "OK" | "NO_RADIO" (nenhum radio Bluetooth encontrado) |
# "ERROR: <mensagem>".

Add-Type -AssemblyName System.Runtime.WindowsRuntime
try { [void][Windows.Devices.Radios.Radio, Windows.Devices.Radios, ContentType=WindowsRuntime] } catch { Write-Output "ERROR: WinRT Radio API indisponivel ($($_.Exception.Message))"; exit }

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $resultType) {
  $task = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($op))
  $task.Wait()
  $task.Result
}

try {
  $radios = Await ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) ([System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]])
  $bt = $radios | Where-Object { $_.Kind.ToString() -eq 'Bluetooth' } | Select-Object -First 1
  if (-not $bt) { Write-Output "NO_RADIO"; exit }
  Await ($bt.SetStateAsync([Windows.Devices.Radios.RadioState]::Off)) ([Windows.Devices.Radios.RadioAccessStatus]) | Out-Null
  Start-Sleep -Seconds 2
  Await ($bt.SetStateAsync([Windows.Devices.Radios.RadioState]::On)) ([Windows.Devices.Radios.RadioAccessStatus]) | Out-Null
  Start-Sleep -Seconds 3
  Write-Output "OK"
} catch {
  Write-Output "ERROR: $($_.Exception.Message)"
}
