using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

// ===========================================================================================
// TipPrint - Monitoramento do adaptador Bluetooth do Windows.
//
// Descobre o(s) adaptador(es) Bluetooth presentes DINAMICAMENTE via WMI (Win32_PnPEntity,
// PNPClass='Bluetooth') - nunca hardcoda um Device Instance ID/VID/PID especifico (pedido
// explicito do usuario, item 13). Mesma familia de consulta WMI que ja existia em
// PrintServer.cs pra Win32_Printer/Win32_SerialPort - nao introduz dependencia nova.
//
// Escalada de recuperacao, deliberadamente limitada (decisao de design registrada no plano,
// motivada pela investigacao real desta sessao - ver memoria tipprint-bt-adapter-reboot-test):
//   1) redescoberta por MAC/porta nova - feito em ConnectionManager.TryReacquireByIdentity()
//   2) toggle de radio Bluetooth via WinRT (Off -> On) - nao exige elevacao, nao mexe no
//      estado "habilitado/desabilitado" do dispositivo no Gerenciador de Dispositivos
//   3) se persistir com sinal de "precisa reiniciar o Windows" -> RequiresUserAction,
//      PARA de tentar recuperar o adaptador (fila continua preservada)
//
// NUNCA chama Disable-PnpDevice/Enable-PnpDevice/pnputil automaticamente - foi exatamente
// essa acao (manual, autorizada, mas fora deste monitoramento) que deixou o adaptador
// TP-Link preso em "aguardando reinicializacao" na investigacao desta sessao (2026-08-15).
// ===========================================================================================

public class AdapterStatus
{
    public bool Ok = true;
    public bool Present;
    public bool NeedsReboot;
    public string AdapterName;
    public string InstanceId;
    public string Status = "Unknown";
    public int ProblemCode;
    public string Detail;
    public int AdapterCount;
    // So' preenchidos quando Check(detailed: true) e' chamado (rota /diagnostics, sob
    // demanda) - a checagem periodica do watchdog (Check() simples) nao precisa disso.
    public string DriverVersion;
    public string DriverProvider;
    public bool Enabled;
}

public class AdapterMonitor
{
    static readonly object Lock = new object();
    DateTime? _lastRadioResetAt;
    static readonly TimeSpan RadioResetCooldown = TimeSpan.FromMinutes(2);
    int _consecutiveUnhealthyChecks;
    static readonly TimeSpan RebootCheckCooldown = TimeSpan.FromSeconds(30);
    DateTime? _lastRebootCheckAt;
    bool _rebootConfirmedThisEpisode;

    // Consulta rapida (WMI puro, sem spawnar processo) - o ConnectionManager so' chama
    // isto quando ja ha sinal de problema na porta (nao em todo tick do watchdog).
    // detailed=true (usado pela rota /diagnostics, sob demanda) tambem busca driver/enabled.
    public AdapterStatus Check(bool detailed = false)
    {
        var status = new AdapterStatus();
        try
        {
            var searcher = new ManagementObjectSearcher("SELECT Name, DeviceID, ConfigManagerErrorCode, Status, PNPClass FROM Win32_PnPEntity");
            AdapterStatus best = null;
            int count = 0;
            foreach (var o in searcher.Get())
            {
                string pnpClass = Convert.ToString(o["PNPClass"]);
                if (!string.Equals(pnpClass, "Bluetooth", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Convert.ToString(o["Name"]);
                // So' o adaptador fisico em si (nome contem "Adapter") - ignora os
                // dispositivos logicos filhos (enumeradores, RFCOMM, radio SWD). Mesmo
                // criterio ja usado e' comprovado em desktop/lib/scripts/bt-adapter-check.ps1.
                if (string.IsNullOrEmpty(name) || name.IndexOf("Adapter", StringComparison.OrdinalIgnoreCase) < 0) continue;
                count++;
                int errCode = 0;
                try { errCode = Convert.ToInt32(o["ConfigManagerErrorCode"]); } catch { }
                string wmiStatus = Convert.ToString(o["Status"]);
                var candidate = new AdapterStatus
                {
                    Present = true,
                    AdapterName = name,
                    InstanceId = Convert.ToString(o["DeviceID"]),
                    Status = wmiStatus,
                    ProblemCode = errCode,
                    Ok = errCode == 0 && string.Equals(wmiStatus, "OK", StringComparison.OrdinalIgnoreCase),
                    Enabled = errCode != 22 // CM_PROB_DISABLED especificamente - validado na investigacao desta sessao (2026-08-15)
                };
                // Se houver mais de um adaptador (ex: onboard + USB), reporta o pior estado -
                // o operador precisa saber do que estiver com problema, nao do que estiver OK.
                if (best == null || (!candidate.Ok && best.Ok)) best = candidate;
            }
            if (best != null)
            {
                best.AdapterCount = count;
                status = best;
            }
            else
            {
                status.Present = false;
                status.Detail = "Nenhum adaptador Bluetooth encontrado via WMI.";
            }
        }
        catch (Exception ex)
        {
            // Falha ao CONSULTAR nao pode virar "adaptador com erro" - fica sem
            // informacao, o monitoramento so' volta a agir quando conseguir consultar de novo.
            status.Ok = true;
            status.Detail = "Erro ao consultar adaptador Bluetooth (ignorado): " + ex.Message;
            return status;
        }

        if (!status.Ok && status.Present)
        {
            status.Detail = string.Format("{0} (ConfigManagerErrorCode={1})", DescribeProblemCode(status.ProblemCode), status.ProblemCode);
            lock (Lock) _consecutiveUnhealthyChecks++;
            status.NeedsReboot = CheckNeedsReboot(status);
        }
        else
        {
            lock (Lock) { _consecutiveUnhealthyChecks = 0; _rebootConfirmedThisEpisode = false; }
        }

        if (detailed && status.Present) FillDriverInfo(status);
        return status;
    }

    // So' chamado sob demanda (rota /diagnostics) - consulta separada porque
    // Win32_PnPEntity nao expoe versao/fabricante do driver diretamente.
    static void FillDriverInfo(AdapterStatus status)
    {
        try
        {
            string escapedId = (status.InstanceId ?? "").Replace("'", "''");
            var searcher = new ManagementObjectSearcher(
                "SELECT DriverVersion, DriverProviderName FROM Win32_PnPSignedDriver WHERE DeviceID='" + escapedId + "'");
            foreach (var o in searcher.Get())
            {
                status.DriverVersion = Convert.ToString(o["DriverVersion"]);
                status.DriverProvider = Convert.ToString(o["DriverProviderName"]);
                break;
            }
        }
        catch { }
    }

    // Mapeamento dos ConfigManagerErrorCode mais comuns vistos em campo (validado na
    // investigacao desta sessao: 22 = desabilitado, 45 = fantasma/nao presente).
    static string DescribeProblemCode(int code)
    {
        switch (code)
        {
            case 0: return "Sem problema";
            case 10: return "O dispositivo nao consegue iniciar (driver com falha)";
            case 22: return "Dispositivo desabilitado";
            case 24: return "Dispositivo nao presente/configurado corretamente";
            case 28: return "Driver nao instalado";
            case 45: return "Dispositivo nao esta conectado (fantasma)";
            default: return "Codigo de problema " + code;
        }
    }

    // Sinal DIRETO de "precisa reiniciar o Windows": reaproveita a tecnica validada na
    // investigacao desta sessao (2026-08-15) - o canal
    // Microsoft-Windows-Kernel-PnP/Configuration registra o evento 1065 ("requer que o
    // sistema seja reinicializado") quando uma operacao no devnode fica pendente. So'
    // consulta isso quando ja ha 2+ checagens seguidas com problema, com cooldown -
    // e' mais pesado que a consulta WMI simples (spawna powershell.exe/Get-WinEvent).
    bool CheckNeedsReboot(AdapterStatus status)
    {
        lock (Lock)
        {
            if (_rebootConfirmedThisEpisode) return true;
            if (_consecutiveUnhealthyChecks < 2) return false;
            if (_lastRebootCheckAt.HasValue && (DateTime.Now - _lastRebootCheckAt.Value) < RebootCheckCooldown) return false;
            _lastRebootCheckAt = DateTime.Now;
        }
        try
        {
            string escapedId = (status.InstanceId ?? "").Replace("'", "''");
            string script =
                "$id = [Regex]::Escape('" + escapedId + "'); " +
                "$e = Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Kernel-PnP/Configuration'; Id=1065; StartTime=(Get-Date).AddMinutes(-10)} -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.Message -match $id }; " +
                "if ($e) { Write-Output 'YES' } else { Write-Output 'NO' }";
            string output = RunPowerShell(script, 6000);
            bool needsReboot = output != null && output.Trim() == "YES";
            lock (Lock) _rebootConfirmedThisEpisode = needsReboot;
            return needsReboot;
        }
        catch (Exception ex)
        {
            PrintServer.Log("Nao foi possivel confirmar 'precisa reiniciar' via Event Log (ignorado): " + ex.Message);
            return false;
        }
    }

    // Unica acao de recuperacao automatica sobre o adaptador: toggle de radio via WinRT
    // (Off -> On), sem exigir elevacao. NUNCA chama Disable-PnpDevice/Enable-PnpDevice/
    // pnputil (ver cabecalho do arquivo). Limitado por cooldown pra nao ficar
    // religando o radio repetidamente em cada tick.
    public bool TryRecover(AdapterStatus status)
    {
        lock (Lock)
        {
            if (_lastRadioResetAt.HasValue && (DateTime.Now - _lastRadioResetAt.Value) < RadioResetCooldown) return false;
            _lastRadioResetAt = DateTime.Now;
        }
        PrintServer.Log("Recovery attempt started (toggle de radio Bluetooth via WinRT)...");
        try
        {
            string output = RunPowerShell(ReadRadioResetScript(), 12000);
            bool ok = output != null && output.Trim() == "OK";
            PrintServer.Log(ok ? "Radio Bluetooth religado com sucesso." : "Nao foi possivel religar o radio Bluetooth (" + output + ").");
            return ok;
        }
        catch (Exception ex)
        {
            PrintServer.Log("Recovery attempt falhou (ignorado): " + ex.Message);
            return false;
        }
    }

    static string _radioResetScriptCache;

    // Procura desktop/lib/scripts/bt-radio-reset.ps1 ao lado do executavel (mesmo padrao
    // de instalacao do TipPrint Desktop). Se nao achar (ex: PrintServer.exe rodando
    // isolado, sem o pacote do Desktop do lado), usa um fallback minimo embutido com a
    // mesma logica - nao trava a funcionalidade por causa de arquivo faltando.
    static string ReadRadioResetScript()
    {
        if (_radioResetScriptCache != null) return _radioResetScriptCache;
        try
        {
            string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string[] candidates =
            {
                Path.Combine(exeDir, "scripts", "bt-radio-reset.ps1"),
                Path.Combine(exeDir, "lib", "scripts", "bt-radio-reset.ps1")
            };
            foreach (string c in candidates)
            {
                if (File.Exists(c)) { _radioResetScriptCache = File.ReadAllText(c); return _radioResetScriptCache; }
            }
        }
        catch { }

        _radioResetScriptCache =
            "Add-Type -AssemblyName System.Runtime.WindowsRuntime\n" +
            "try { [void][Windows.Devices.Radios.Radio, Windows.Devices.Radios, ContentType=WindowsRuntime] } catch { Write-Output ('ERROR: ' + $_.Exception.Message); exit }\n" +
            "$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]\n" +
            "function Await($op, $t) { $task = $asTaskGeneric.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(); $task.Result }\n" +
            "try {\n" +
            "  $radios = Await ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) ([System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]])\n" +
            "  $bt = $radios | Where-Object { $_.Kind.ToString() -eq 'Bluetooth' } | Select-Object -First 1\n" +
            "  if (-not $bt) { Write-Output 'NO_RADIO'; exit }\n" +
            "  Await ($bt.SetStateAsync([Windows.Devices.Radios.RadioState]::Off)) ([Windows.Devices.Radios.RadioAccessStatus]) | Out-Null\n" +
            "  Start-Sleep -Seconds 2\n" +
            "  Await ($bt.SetStateAsync([Windows.Devices.Radios.RadioState]::On)) ([Windows.Devices.Radios.RadioAccessStatus]) | Out-Null\n" +
            "  Start-Sleep -Seconds 3\n" +
            "  Write-Output 'OK'\n" +
            "} catch { Write-Output ('ERROR: ' + $_.Exception.Message) }\n";
        return _radioResetScriptCache;
    }

    static string RunPowerShell(string script, int timeoutMs)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "tipprint-adapter-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(tempFile, script, Encoding.UTF8);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + tempFile + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var proc = Process.Start(psi))
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(timeoutMs);
                return stdout;
            }
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
}
