using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Este processo E' o TipPrint Desktop Agent: o unico dono do acesso fisico a impressora
// (COM/USB/BT/driver do Windows) nesta maquina. O app Electron (pasta desktop/) nao abre
// mais porta serial direto - ele fala com este processo via HTTP (/connect, /print) pra
// nao disputar a porta COM com ele. Nome do arquivo/porta/API mantidos como estao
// (PrintServer.exe, :8080) por compatibilidade com o tipo7.com e o auto-update ja em producao.
class PrintServer
{
    // Sobe a cada publicacao (padrao do TipPrint: X.X.X.<app>.X.X - 2 = PrintServer/PC).
    // Mude aqui e publique web/update-server.json com o mesmo numero para o auto-update disparar.
    public const string AppVersion = "1.0.5.2.0.9";
    static string UpdateCheckUrl = "https://tipprint.vercel.app/update-server.json";

    static int HttpPort = 8080;
    static int TcpPort = 9100;
    static string LogFile = null;
    static string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TipPrint", "config.txt");

    static readonly object Lock = new object();
    // Active/ActivePort/WantConnect/ReconnectTries viviam aqui como campos soltos antes desta
    // mudanca (2026-08-15, camada universal de monitoramento/reconexao). Agora moram dentro
    // de PrinterManager.Current (ConnectionManager.cs), que tambem sabe diferenciar causa de
    // falha (impressora desligada / porta sumiu / adaptador com erro / Windows precisa
    // reiniciar) em vez de so' "conectado sim/nao". Ver PrintTransport.cs/ConnectionManager.cs/
    // AdapterMonitor.cs.

    // Saude/observabilidade da impressao (exposto em /status)
    static int PrintedOk = 0;
    static int PrintedFail = 0;
    static string LastError = null;
    static DateTime? LastErrorAt = null;
    static DateTime? LastSuccessAt = null;

    // Chave de sistema (TipPrint Backend) - autorizacao por PRODUTO (ex: Tipo7), separada da
    // licenca por origem acima. So' entra em acao se o config.txt tiver uma chave configurada
    // (linha 4) - instalacoes existentes sem isso continuam em modo livre, sem chamar nada.
    // Falha de rede ao validar NUNCA bloqueia impressao (mantem o ultimo estado conhecido) -
    // so bloqueia quando o backend confirma de verdade que a chave e invalida/revogada.
    const string DefaultBackendUrl = "http://localhost:8090"; // usado so' se apiKey configurada sem backendUrl - ambiente de teste local
    static bool? SystemKeyValid = null;
    static string SystemName = null;
    static string SystemLastError = null;
    static DateTime? SystemLastCheck = null;

    // Deduplicacao de pedidos (evita imprimir o mesmo ingresso 2x em caso de retry de rede)
    static readonly object DedupLock = new object();
    static readonly Dictionary<string, DateTime> RecentRequests = new Dictionary<string, DateTime>();
    static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(25);

    // Instancia unica + auto-restart em caso de erro fatal nao tratado
    static Mutex SingleInstanceMutex;
    static string[] StartupArgs;

    static readonly object QueueLock = new object();
    static readonly Queue<PrintJob> JobQueue = new Queue<PrintJob>();
    static volatile int ActiveJobs = 0;

    static void Main(string[] args)
    {
        StartupArgs = args;

        if (args.Length > 0) HttpPort = int.Parse(args[0]);
        if (args.Length > 1) TcpPort = int.Parse(args[1]);
        if (args.Length > 2) LogFile = args[2];
        if (args.Length > 3 && !string.IsNullOrEmpty(args[3])) UpdateCheckUrl = args[3];

        if (!AcquireSingleInstance())
        {
            Log("Outra instancia do PrintServer ja esta em execucao. Encerrando esta copia.");
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

        // Limpa resquicio de uma auto-atualizacao anterior (arquivo antigo renomeado antes da troca).
        try
        {
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string oldPath = Path.Combine(Path.GetDirectoryName(exePath), "PrintServer.old.exe");
            if (File.Exists(oldPath)) File.Delete(oldPath);
        }
        catch { }

        Log("Iniciando TipPrint PrintServer... (v" + AppVersion + ")");

        var http = new Thread(HttpLoop);
        http.IsBackground = true;
        http.Start();

        var tcp = new Thread(TcpLoop);
        tcp.IsBackground = true;
        tcp.Start();

        var watchdog = new Thread(WatchdogLoop);
        watchdog.IsBackground = true;
        watchdog.Start();

        var sender = new Thread(SenderLoop);
        sender.IsBackground = true;
        sender.Start();

        var updater = new Thread(UpdateCheckLoop);
        updater.IsBackground = true;
        updater.Start();

        var systemCheck = new Thread(SystemCheckLoop);
        systemCheck.IsBackground = true;
        systemCheck.Start();

        string saved = LoadConfig();
        if (!string.IsNullOrEmpty(saved))
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(3000);
                    Log("Reconectando a impressora salva: " + saved);
                    AutoConnect(saved);
                }
                catch (Exception ex)
                {
                    Log("Erro ao reconectar impressora salva (ignorado, servidor continua no ar): " + ex.Message);
                }
            });
        }

        Log(string.Format("Pronto. Painel: http://localhost:{0}  |  Impressao por rede TCP:{1}", HttpPort, TcpPort));
        // Console.ReadLine() ficava aqui antes pra manter o processo vivo - so' funciona com
        // console interativo de verdade. Quando o Desktop app sobe este .exe sozinho (sem
        // console, ex: fallback "Agent nao respondeu"), ReadLine() recebe EOF na hora e o
        // processo morria logo depois de logar "Pronto" (bug real, achado em producao em
        // 2026-08-14 - o Agent nunca chegava a responder ao Desktop). Loop sem depender de
        // stdin mantem o processo de pe em qualquer forma de inicializacao.
        while (true) Thread.Sleep(60000);
    }

    // Garante que so exista uma copia do PrintServer rodando por sessao do Windows.
    // Tolerante a uma instancia anterior que esteja no meio de um crash/restart (AbandonedMutexException).
    static bool AcquireSingleInstance()
    {
        for (int attempt = 0; attempt < 25; attempt++) // ate ~5s esperando a instancia anterior liberar
        {
            bool createdNew = false;
            try
            {
                SingleInstanceMutex = new Mutex(true, "Local\\TipPrint_PrintServer_SingleInstance", out createdNew);
            }
            catch (AbandonedMutexException)
            {
                createdNew = true; // conseguimos a posse mesmo com abandono da instancia anterior
            }
            if (createdNew) return true;
            try { if (SingleInstanceMutex != null) SingleInstanceMutex.Close(); } catch { }
            SingleInstanceMutex = null;
            Thread.Sleep(200);
        }
        return false;
    }

    // Ultimo recurso: se alguma excecao escapar de todos os try/catch das threads,
    // registra o erro e tenta religar o proprio processo em vez de deixar o PrintServer sumir.
    static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            Log("ERRO FATAL nao tratado - reiniciando o processo automaticamente: " + (ex != null ? ex.ToString() : Convert.ToString(e.ExceptionObject)));
        }
        catch { }
        try
        {
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var sb = new StringBuilder();
            if (StartupArgs != null)
            {
                foreach (string a in StartupArgs) sb.Append("\"" + (a ?? "").Replace("\"", "\\\"") + "\" ");
            }
            Thread.Sleep(1000); // pequena pausa para nao entrar em loop de restart instantaneo
            Process.Start(new ProcessStartInfo(exe, sb.ToString().Trim()) { UseShellExecute = false });
        }
        catch { }
    }

    // Confere periodicamente se ha uma versao nova publicada e, se houver, baixa e substitui a si mesmo.
    // Mesmo padrao usado pelo TipPrint Android/Desktop (update.json / update-windows.json).
    static void UpdateCheckLoop()
    {
        Thread.Sleep(5000); // deixa o servidor terminar de subir antes de checar
        while (true)
        {
            try
            {
                CheckAndApplyUpdate();
            }
            catch (Exception ex)
            {
                Log("Erro na checagem de atualizacao (ignorado, tenta de novo mais tarde): " + ex.Message);
            }
            Thread.Sleep(6 * 60 * 60 * 1000); // a cada 6h
        }
    }

    // Nunca aplica mais de uma auto-atualizacao dentro dessa janela. Protege contra loop infinito de
    // reinicio caso a versao publicada em update-server.json nao bata com o numero gravado no .exe
    // (ex.: esquecimento ao publicar) - sem isso, cada instancia no campo ficaria reiniciando sem parar.
    static readonly TimeSpan MinTimeBetweenSelfUpdates = TimeSpan.FromMinutes(15);

    static void CheckAndApplyUpdate()
    {
        string json;
        using (var wc = new WebClient())
        {
            wc.Headers["User-Agent"] = "TipPrint-PrintServer/" + AppVersion;
            json = wc.DownloadString(UpdateCheckUrl);
        }
        var info = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        string remoteVersion = info != null && info.ContainsKey("versionName") ? Convert.ToString(info["versionName"]) : null;
        if (string.IsNullOrEmpty(remoteVersion) || !IsNewerVersion(remoteVersion, AppVersion)) return;

        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string dir = Path.GetDirectoryName(exePath);
        string markerPath = Path.Combine(dir, "PrintServer.lastupdate");

        DateTime? lastUpdate = ReadLastUpdateMarker(markerPath);
        if (lastUpdate.HasValue && (DateTime.UtcNow - lastUpdate.Value) < MinTimeBetweenSelfUpdates)
        {
            Log(string.Format(
                "Versao {0} anunciada, mas IGNORADA por seguranca: ja houve auto-atualizacao ha menos de {1} min " +
                "(evita loop de reinicio caso a versao publicada nao bata com o binario). Confira update-server.json.",
                remoteVersion, (int)MinTimeBetweenSelfUpdates.TotalMinutes));
            return;
        }

        string downloadPath = info.ContainsKey("downloadPath") ? Convert.ToString(info["downloadPath"]) : "/downloads/PrintServer.exe";
        string url = downloadPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? downloadPath
            : new Uri(new Uri(UpdateCheckUrl), downloadPath).ToString();

        Log(string.Format("Nova versao disponivel: {0} (atual: {1}). Baixando de {2}...", remoteVersion, AppVersion, url));

        string newPath = Path.Combine(dir, "PrintServer.new.exe");
        string oldPath = Path.Combine(dir, "PrintServer.old.exe");

        using (var wc = new WebClient())
        {
            wc.DownloadFile(url, newPath);
        }
        if (new FileInfo(newPath).Length < 10000) // sanity check: nao aplica se o download veio vazio/corrompido
            throw new Exception("Arquivo baixado parece invalido (muito pequeno) - atualizacao abortada.");

        try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
        File.Move(exePath, oldPath);
        File.Move(newPath, exePath);
        try { File.WriteAllText(markerPath, DateTime.UtcNow.Ticks.ToString()); } catch { }

        Log(string.Format("Atualizacao aplicada ({0} -> {1}). Reiniciando...", AppVersion, remoteVersion));
        var sb = new StringBuilder();
        if (StartupArgs != null)
            foreach (string a in StartupArgs) sb.Append("\"" + (a ?? "").Replace("\"", "\\\"") + "\" ");
        Process.Start(new ProcessStartInfo(exePath, sb.ToString().Trim()) { UseShellExecute = false });
        Environment.Exit(0);
    }

    // A cada N minutos, se houver uma chave de sistema configurada, confirma com o TipPrint
    // Backend se ela ainda e' valida. Roda em paralelo ao servidor, nunca bloqueia o startup.
    static void SystemCheckLoop()
    {
        Thread.Sleep(4000);
        while (true)
        {
            try
            {
                string key = LoadApiKey();
                if (!string.IsNullOrEmpty(key)) ValidateSystemKey(key);
            }
            catch (Exception ex)
            {
                Log("Erro ao validar chave de sistema (ignorado, tenta de novo mais tarde): " + ex.Message);
            }
            Thread.Sleep(5 * 60 * 1000); // a cada 5 min
        }
    }

    static void ValidateSystemKey(string key)
    {
        string url = LoadBackendUrl().TrimEnd('/') + "/validate";
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 8000;
            byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new { key = key }));
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string json = reader.ReadToEnd();
                var info = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                bool valid = info != null && info.ContainsKey("valid") && Convert.ToBoolean(info["valid"]);
                string name = info != null && info.ContainsKey("system") ? Convert.ToString(info["system"]) : null;
                lock (Lock)
                {
                    SystemKeyValid = valid;
                    SystemName = name;
                    SystemLastError = null;
                    SystemLastCheck = DateTime.Now;
                }
                Log(valid ? ("Chave de sistema valida (" + name + ").") : "Chave de sistema INVALIDA ou revogada - impressao sera bloqueada ate corrigir.");
            }
        }
        catch (Exception ex)
        {
            lock (Lock)
            {
                SystemLastError = ex.Message;
                SystemLastCheck = DateTime.Now;
                // nao mexe em SystemKeyValid aqui de proposito: falha de rede/backend fora do ar
                // mantem o ultimo estado conhecido, nao pode derrubar impressao em producao.
            }
            Log("Nao foi possivel validar a chave de sistema agora (mantendo ultimo estado conhecido): " + ex.Message);
        }
    }

    // So' bloqueia quando o backend JA CONFIRMOU que a chave e' invalida/revogada. Sem chave
    // configurada (instalacoes de hoje) ou ainda sem checagem concluida = modo livre, como sempre foi.
    static bool SistemaAutorizado()
    {
        if (string.IsNullOrEmpty(LoadApiKey())) return true;
        lock (Lock) { return SystemKeyValid != false; }
    }

    static DateTime? ReadLastUpdateMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            long ticks;
            if (long.TryParse(File.ReadAllText(path).Trim(), out ticks)) return new DateTime(ticks, DateTimeKind.Utc);
        }
        catch { }
        return null;
    }

    static bool IsNewerVersion(string candidate, string current)
    {
        int[] a = ParseVersion(candidate);
        int[] b = ParseVersion(current);
        int len = Math.Max(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    static int[] ParseVersion(string v)
    {
        string[] parts = (v ?? "").Split('.');
        int[] result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            int n;
            result[i] = int.TryParse(parts[i], out n) ? n : 0;
        }
        return result;
    }

    // Evita reimprimir o mesmo pedido quando o site refaz a chamada por timeout/instabilidade de rede.
    // So conta como "ja visto" um pedido que REALMENTE foi enfileirado com sucesso (ver MarkRequestHandled) -
    // assim um retry legitimo apos uma falha (ex.: impressora ainda sem conectar) nao e descartado por engano.
    static bool WasRequestRecentlyHandled(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        lock (DedupLock)
        {
            DateTime seenAt;
            if (RecentRequests.TryGetValue(key, out seenAt) && (DateTime.UtcNow - seenAt) < DedupWindow) return true;
            return false;
        }
    }

    static void MarkRequestHandled(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (DedupLock)
        {
            if (RecentRequests.Count > 500)
            {
                DateTime now = DateTime.UtcNow;
                var expired = new List<string>();
                foreach (var kv in RecentRequests)
                    if (now - kv.Value > DedupWindow) expired.Add(kv.Key);
                foreach (var k in expired) RecentRequests.Remove(k);
            }
            RecentRequests[key] = DateTime.UtcNow;
        }
    }

    static string LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string[] lines = File.ReadAllLines(ConfigPath);
                if (lines.Length >= 1 && !string.IsNullOrEmpty(lines[0])) return lines[0].Trim();
            }
        }
        catch { }
        return null;
    }

    internal static string LoadCharset()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string[] lines = File.ReadAllLines(ConfigPath);
                if (lines.Length >= 2 && !string.IsNullOrEmpty(lines[1])) return lines[1].Trim();
            }
        }
        catch { }
        return "ascii";
    }

    internal static void SaveConfig(string id, string charset)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            // Preserva origens/chave de sistema/backendUrl/MAC BT (linhas 3-6) que ja estivessem
            // salvas - sobrescrever tudo aqui apagaria a licenca configurada a cada reconexao.
            string[] existing = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath) : new string[0];
            string origins = existing.Length > 2 ? existing[2] : "";
            string apiKey = existing.Length > 3 ? existing[3] : "";
            string backendUrl = existing.Length > 4 ? existing[4] : "";
            string btMac = existing.Length > 5 ? existing[5] : "";
            File.WriteAllText(ConfigPath, id + Environment.NewLine + charset + Environment.NewLine
                + origins + Environment.NewLine + apiKey + Environment.NewLine + backendUrl + Environment.NewLine + btMac);
            Log(string.Format("Configuracao salva: impressora={0}, charset={1}", id, charset));
        }
        catch { }
    }

    // Grava a chave de sistema (TipPrint Backend) e a URL do backend, preservando impressora/
    // charset/origens/MAC BT ja configurados.
    static void SaveSystemConfig(string apiKey, string backendUrl)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            string[] existing = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath) : new string[0];
            string id = existing.Length > 0 ? existing[0] : "";
            string charset = existing.Length > 1 ? existing[1] : "ascii";
            string origins = existing.Length > 2 ? existing[2] : "";
            string btMac = existing.Length > 5 ? existing[5] : "";
            File.WriteAllText(ConfigPath, id + Environment.NewLine + charset + Environment.NewLine
                + origins + Environment.NewLine + (apiKey ?? "") + Environment.NewLine + (backendUrl ?? "")
                + Environment.NewLine + btMac);
            Log("Chave de sistema/backend atualizada.");
        }
        catch { }
    }

    // MAC da impressora Bluetooth ativa - salvo pra sobreviver a troca de numero de porta
    // COM (o Windows recria a porta com outro numero depois de reconectar/reiniciar, mas o
    // MAC do dispositivo continua o mesmo - achado em producao em 2026-08-14).
    internal static void SaveBtMac(string mac)
    {
        if (string.IsNullOrEmpty(mac)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            string[] existing = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath) : new string[0];
            string id = existing.Length > 0 ? existing[0] : "";
            string charset = existing.Length > 1 ? existing[1] : "ascii";
            string origins = existing.Length > 2 ? existing[2] : "";
            string apiKey = existing.Length > 3 ? existing[3] : "";
            string backendUrl = existing.Length > 4 ? existing[4] : "";
            File.WriteAllText(ConfigPath, id + Environment.NewLine + charset + Environment.NewLine
                + origins + Environment.NewLine + apiKey + Environment.NewLine + backendUrl
                + Environment.NewLine + mac);
        }
        catch { }
    }

    internal static string LoadBtMac()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string[] lines = File.ReadAllLines(ConfigPath);
                if (lines.Length >= 6 && !string.IsNullOrEmpty((lines[5] ?? "").Trim())) return lines[5].Trim().ToUpperInvariant();
            }
        }
        catch { }
        return null;
    }

    // Procura a MESMA impressora fisica (mesmo MAC) em qualquer porta COM que ela esteja
    // agora, mesmo que o numero mudou desde a ultima vez. Retorna null se nao achar.
    internal static PrinterInfo FindByMac(string mac, List<PrinterInfo> found)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        foreach (var p in found)
        {
            if (p.Type == "bluetooth" && string.Equals(p.Detail, mac, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    static void AutoConnect(string id)
    {
        var found = FindPrinters();
        PrinterInfo target = null;
        foreach (var p in found)
        {
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) target = p;
        }
        if (target == null)
        {
            // Porta salva sumiu - comum com Bluetooth: o Windows recria a porta COM com outro
            // numero depois de reconectar. Tenta achar a MESMA impressora pelo MAC salvo antes
            // de desistir, em vez de exigir que alguem reconfigure na mao.
            string savedMac = LoadBtMac();
            target = FindByMac(savedMac, found);
            if (target != null)
                Log("Impressora salva (" + id + ") sumiu, mas achei ela na porta " + target.Id + " pelo MAC salvo (" + savedMac + ") - reconectando la.");
        }
        if (target == null)
        {
            Log("Impressora salva nao encontrada agora (" + id + "). Pareie-a e conecte pelo painel.");
            return;
        }
        PrinterManager.Current.Connect(target);
    }

    internal static void Log(string msg)
    {
        string line = string.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), msg);
        // Console.WriteLine pode lancar quando o processo nao tem console de verdade (ex:
        // stdio redirecionado pra NUL, caso do spawn 'ignore' do Electron) - sem o try/catch
        // aqui, isso derrubava o Log() inteiro ANTES de gravar no arquivo, entao nem o log
        // em disco sobrevivia pra explicar o problema.
        try { Console.WriteLine(line); } catch { }
        try
        {
            if (LogFile != null) File.AppendAllText(LogFile, line + Environment.NewLine);
        }
        catch { }
    }

    // Log estruturado (pedido do usuario, item 14): um cabecalho "impressora - EVENTO"
    // seguido de linhas de detalhe indentadas, todos passando pelo mesmo Log() de sempre
    // (mesmo sink: console + arquivo) - nao cria um sistema de log paralelo, so' um
    // formatador em cima do que ja existia. Linhas nulas/vazias sao ignoradas (permite
    // chamar com detalhes condicionais sem montar array na mao).
    internal static void LogEvent(string printerLabel, string kind, params string[] details)
    {
        Log(string.Format("{0} - {1}", string.IsNullOrEmpty(printerLabel) ? "?" : printerLabel, kind));
        if (details == null) return;
        foreach (string d in details)
            if (!string.IsNullOrEmpty(d)) Log("    " + d);
    }

    static string[] LoadOrigins()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            string[] lines = File.ReadAllLines(ConfigPath);
            if (lines.Length < 3) return null;
            string raw = (lines[2] ?? "").Trim();
            if (raw.Length == 0) return null;
            string[] parts = raw.Split(',');
            var list = new List<string>();
            foreach (string p in parts)
            {
                string h = p.Trim().ToLowerInvariant();
                if (h.Length > 0) list.Add(h);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }
        catch { return null; }
    }

    static string ModoLicenca()
    {
        return LoadOrigins() == null ? "livre" : "restrita";
    }

    static string LoadApiKey()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            string[] lines = File.ReadAllLines(ConfigPath);
            if (lines.Length < 4) return null;
            string k = (lines[3] ?? "").Trim();
            return k.Length > 0 ? k : null;
        }
        catch { return null; }
    }

    static string LoadBackendUrl()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string[] lines = File.ReadAllLines(ConfigPath);
                if (lines.Length >= 5 && !string.IsNullOrEmpty((lines[4] ?? "").Trim())) return lines[4].Trim();
            }
        }
        catch { }
        return DefaultBackendUrl;
    }

    static bool OriginAutorizada(HttpListenerContext ctx)
    {
        string[] allowed = LoadOrigins();
        if (allowed == null) return true;
        string origin = ctx.Request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin)) return false;
        string host;
        try { host = new Uri(origin).Host.ToLowerInvariant(); }
        catch { return false; }
        foreach (string a in allowed)
        {
            if (string.Equals(host, a, StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("*.") && host.EndsWith(a.Substring(1))) return true;
        }
        return false;
    }

    internal static List<PrinterInfo> FindPrinters()
    {
        var list = new List<PrinterInfo>();
        try
        {
            var macNames = new Dictionary<string, string>();
            var devKey = RegistryWrapper.OpenSubKey("HKLM\\SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Devices");
            if (devKey != null)
            {
                foreach (string mac in devKey.SubKeys())
                {
                    var macKey = RegistryWrapper.OpenSubKey("HKLM\\SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Devices\\" + mac);
                    if (macKey == null) continue;
                    string name = macKey.BytesName("", "Name");
                    if (!string.IsNullOrEmpty(name)) macNames[mac.ToLower()] = name;
                }
            }

            var bthenum = RegistryWrapper.OpenSubKey("HKLM\\SYSTEM\\CurrentControlSet\\Enum\\BTHENUM");
            if (bthenum != null)
            {
                foreach (string guid in bthenum.SubKeys())
                {
                    if (!guid.ToLower().Contains("00001101")) continue;
                    var guidKey = RegistryWrapper.OpenSubKey("HKLM\\SYSTEM\\CurrentControlSet\\Enum\\BTHENUM\\" + guid);
                    if (guidKey == null) continue;
                    foreach (string inst in guidKey.SubKeys())
                    {
                        string instPath = "HKLM\\SYSTEM\\CurrentControlSet\\Enum\\BTHENUM\\" + guid + "\\" + inst;
                        var instKey = RegistryWrapper.OpenSubKey(instPath);
                        if (instKey == null) continue;
                        string friendly = instKey.Value(inst, "FriendlyName");
                        if (string.IsNullOrEmpty(friendly)) friendly = instKey.Value(inst, "DeviceInstanceID");
                        var paramKey = RegistryWrapper.OpenSubKey(instPath + "\\PARAMETERS");
                        string com = paramKey != null ? paramKey.Value(instPath + "\\PARAMETERS", "Index") : null;
                        if (string.IsNullOrEmpty(com))
                        {
                            var dpKey = RegistryWrapper.OpenSubKey(instPath + "\\Device Parameters");
                            if (dpKey != null)
                            {
                                com = dpKey.Value(instPath + "\\Device Parameters", "PortName");
                                if (!string.IsNullOrEmpty(com)) com = com.Replace("COM", "");
                            }
                        }
                        string mac = null;
                        var m = System.Text.RegularExpressions.Regex.Match(inst, "[0-9A-Fa-f]{12}");
                        if (m.Success)
                        {
                            string candidate = m.Value.ToLower();
                            if (candidate != "000000000000") mac = candidate;
                        }
                        string name = null;
                        if (mac != null && macNames.ContainsKey(mac)) name = macNames[mac];
                        if (string.IsNullOrEmpty(name)) name = "Impressora Bluetooth";
                        string comPort = null;
                        if (!string.IsNullOrEmpty(com))
                        {
                            comPort = "COM" + com;
                            foreach (var sp in SerialPort.GetPortNames())
                            {
                                if (string.Equals(sp, comPort, StringComparison.OrdinalIgnoreCase)) { comPort = sp; break; }
                            }
                        }
                        bool isPrinter = guid.ToLower().Contains("00001101") &&
                                         (string.IsNullOrEmpty(friendly) || friendly.ToLower().Contains("serial") || name != "Impressora Bluetooth");
                        if (isPrinter && comPort != null)
                        {
                            list.Add(new PrinterInfo { Type = "bluetooth", Name = name, Id = comPort, Detail = mac != null ? mac.ToUpper() : "", Width = "58" });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("Erro na descoberta Bluetooth: " + ex.Message);
        }

        foreach (string com in SerialPort.GetPortNames())
        {
            bool already = false;
            foreach (var p in list) if (string.Equals(p.Id, com, StringComparison.OrdinalIgnoreCase)) already = true;
            if (!already && CommIsBluetooth(com))
            {
                list.Add(new PrinterInfo { Type = "bluetooth", Name = "Bluetooth (porta " + com + ")", Id = com, Detail = "", Width = "58" });
            }
            else if (!already && CommIsUsb(com))
            {
                list.Add(new PrinterInfo { Type = "usb", Name = "USB (porta " + com + ")", Id = com, Detail = "Impressora USB via porta serial", Width = "58" });
            }
        }

        try
        {
            var wmi = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_Printer");
            foreach (var o in wmi.Get())
            {
                string pname = Convert.ToString(o["Name"]);
                string driver = Convert.ToString(o["DriverName"]);
                if (string.IsNullOrEmpty(pname)) continue;
                string lowerName = pname.ToLower();
                if (lowerName.Contains("pdf") || lowerName.Contains("onenote") || lowerName.Contains("xps") ||
                    lowerName.Contains("fax") || lowerName.Contains("microsoft print")) continue;
                string lower = (pname + " " + driver).ToLower();
                string width = lower.Contains("80") || lower.Contains("110") || lower.Contains("3\"" ) ? "80" : "58";
                list.Add(new PrinterInfo { Type = "windows", Name = pname, Id = pname, Detail = "Impressora do Windows (driver: " + driver + ")", Width = width });
            }
        }
        catch { }
        return list;
    }

    static bool CommIsUsb(string com)
    {
        try
        {
            var wmi = new System.Management.ManagementObjectSearcher(
                "SELECT * FROM Win32_SerialPort WHERE DeviceID='" + com + "'");
            foreach (var o in wmi.Get())
            {
                string pnp = Convert.ToString(o["PNPDeviceID"]);
                if (pnp != null && pnp.Contains("VID_")) return true;
            }
        }
        catch { }
        return false;
    }

    static bool CommIsBluetooth(string com)
    {
        try
        {
            var wmi = new System.Management.ManagementObjectSearcher(
                "SELECT * FROM Win32_SerialPort WHERE DeviceID='" + com + "'");
            foreach (var o in wmi.Get())
            {
                string pnp = Convert.ToString(o["PNPDeviceID"]);
                if (pnp != null && pnp.Contains("BTHENUM")) return true;
            }
        }
        catch { }
        return false;
    }

    // ConnectPrinter/CloseActiveLocked/TryOpen/TryReacquireByMac viviam aqui como metodos
    // soltos antes desta mudanca. Agora moram em ConnectionManager (ConnectionManager.cs) -
    // mesma logica (conecta, fecha, reabre, redescobre por MAC), so' atras da maquina de
    // estados/interface de transporte. Chamadas: PrinterManager.Current.Connect/TryOpen/
    // Disconnect/TryReacquireByIdentity.

    // Laco fino: toda a decisao de "precisa reabrir? qual backoff? redescobre por
    // identidade? consulta o adaptador Bluetooth?" mora em ConnectionManager.Tick() agora -
    // o watchdog so' chama isso a cada 4s, preservando o comportamento de antes (mesmo
    // intervalo de checagem).
    static void WatchdogLoop()
    {
        while (true)
        {
            try
            {
                Thread.Sleep(4000);
                PrinterManager.Current.Tick();
            }
            catch (Exception ex)
            {
                Log("Erro inesperado no watchdog (ignorado, continua rodando): " + ex.Message);
            }
        }
    }

    static void EnqueueJob(PrinterInfo pi, byte[] data, string label)
    {
        EnqueueJob(pi, data, label, -1, 0);
    }

    // atomicStart/atomicLen marcam um trecho (ex: o comando ESC/POS do QR Code) que NUNCA
    // pode ser cortado no meio entre dois pedacos do chunking - ver SendAndChunk.
    static void EnqueueJob(PrinterInfo pi, byte[] data, string label, int atomicStart, int atomicLen)
    {
        if (data == null || data.Length == 0) throw new Exception("Dados vazios.");
        if (pi == null) throw new Exception("Nenhuma impressora conectada. Use /connect primeiro.");
        lock (QueueLock)
        {
            JobQueue.Enqueue(new PrintJob { Printer = pi, Data = data, Label = label, AtomicStart = atomicStart, AtomicLen = atomicLen });
            Monitor.PulseAll(QueueLock);
        }
        Log(string.Format("Job \"{0}\" enfileirado ({1} bytes). Fila: {2}", label, data.Length, QueuedCount()));
    }

    static int QueuedCount()
    {
        lock (QueueLock) return JobQueue.Count;
    }

    static void SenderLoop()
    {
        while (true)
        {
            PrintJob job = null;
            try
            {
                lock (QueueLock)
                {
                    while (JobQueue.Count == 0) Monitor.Wait(QueueLock);
                    job = JobQueue.Dequeue();
                    ActiveJobs++;
                }
            }
            catch (Exception ex)
            {
                Log("Erro inesperado na fila de impressao (ignorado, continua rodando): " + ex.Message);
                continue;
            }
            try
            {
                SendAndChunk(job);
                PrintedOk++;
                LastSuccessAt = DateTime.Now;
            }
            catch (Exception ex)
            {
                Log(string.Format("Falha ao imprimir job \"{0}\": {1}", job.Label, ex.Message));
                PrintedFail++;
                LastError = ex.Message;
                LastErrorAt = DateTime.Now;
            }
            finally
            {
                lock (QueueLock) ActiveJobs--;
            }
            try
            {
                PrintProfile prof = ProfileFor(job.Printer);
                if (prof != null && prof.PauseBetweenJobsMs > 0) Thread.Sleep(prof.PauseBetweenJobsMs);
            }
            catch (Exception ex)
            {
                Log("Erro inesperado ao pausar entre jobs (ignorado): " + ex.Message);
            }
        }
    }

    static void SendAndChunk(PrintJob job)
    {
        PrinterInfo pi = job.Printer;
        if (pi == null) throw new Exception("Job sem impressora definida.");
        PrintProfile prof = ProfileFor(pi);

        if (pi.Type == "windows")
        {
            WinPrinter.RawPrint(pi.Id, job.Data);
            job.State = JobState.Completed;
            PrinterManager.Current.NotifyPrintSuccess();
            Log(string.Format("Impressao \"{0}\" enviada ({1} bytes) para \"{2}\".", job.Label, job.Data.Length, pi.Name));
            return;
        }

        byte[] data = job.Data;
        int offset = 0;
        int totalAttempts = 0;
        job.State = JobState.Sending;

        while (offset < data.Length)
        {
            IPrinterTransport transport = PrinterManager.Current.Transport;
            if (transport == null || !PrinterManager.Current.IsReady)
            {
                if (totalAttempts++ >= 12)
                {
                    // Estado INDETERMINADO (pedido explicito do usuario): se ja saiu byte
                    // pra fora deste processo (offset > 0), NAO sabemos se a impressora
                    // recebeu tudo - nunca reenviamos esse job sozinhos, so' registramos e
                    // deixamos claro no log/estado. So' e' "seguro reenviar" (Failed) quando
                    // nada saiu daqui ainda.
                    job.State = offset > 0 ? JobState.SentIndeterminate : JobState.Failed;
                    if (job.State == JobState.SentIndeterminate)
                        Log(string.Format("Job \"{0}\" em estado INDETERMINADO: {1}/{2} bytes ja tinham sido entregues ao sistema quando a conexao caiu de vez - resultado fisico desconhecido, NAO sera reenviado automaticamente.", job.Label, offset, data.Length));
                    throw new Exception(job.State == JobState.SentIndeterminate
                        ? string.Format("Impressora sem conexao apos 12 tentativas - job INDETERMINADO ({0}/{1} bytes enviados).", offset, data.Length)
                        : "Impressora sem conexao apos 12 tentativas (job abortado, nada foi enviado).");
                }
                Log(string.Format("Job \"{0}\" aguardando reconexao da impressora...", job.Label));
                if (!PrinterManager.Current.TryOpen()) Thread.Sleep(2000);
                continue;
            }
            int len = Math.Min(prof.ChunkBytes, data.Length - offset);
            // Nunca corta o trecho protegido (ex: comando ESC/POS do QR Code) no meio entre
            // dois pedacos - impressoras baratas perdem a sincronia do comando se ele chegar
            // partido em duas escritas com pausa no meio, e imprimem os bytes do comando como
            // texto puro em vez de executar (achado real em producao, 2026-08-14: linha
            // "P0<hash>" aparecendo crua no cupom, antes do QR). Se o corte cairia dentro do
            // trecho protegido, encurta ESTE pedaco pra terminar exatamente antes dele (o
            // proximo pedaco entao comeca exatamente no inicio do trecho protegido, com o
            // trecho inteiro cabendo - ele e' bem menor que ChunkBytes).
            if (job.AtomicStart >= 0)
            {
                int atomicEnd = job.AtomicStart + job.AtomicLen;
                if (offset < job.AtomicStart && offset + len > job.AtomicStart)
                {
                    len = job.AtomicStart - offset;
                }
                else if (offset >= job.AtomicStart && offset < atomicEnd)
                {
                    len = Math.Max(len, atomicEnd - offset);
                    // Respiro extra antes do comando do QR: a pausa normal entre chunks
                    // (prof.PauseMs, abaixo) existe pra nao estourar o buffer de recepcao da
                    // impressora, nao pra esperar o cabecote terminar de imprimir fisicamente
                    // as linhas de texto anteriores. Em impressora fraca isso pode ainda estar
                    // rolando quando o comando do QR chega, mesmo ele saindo inteiro num unico
                    // Write() sem ser cortado - ela perde a sincronia e imprime um pedaco do
                    // comando como texto cru antes de retomar e renderizar o QR (achado real,
                    // 15/08/2026: linha "P0<hash>" aparecendo antes do QR mesmo com a protecao
                    // de nao cortar o comando). Dobra a pausa so nesse ponto de transicao.
                    if (prof.PauseMs > 0) Thread.Sleep(prof.PauseMs);
                }
            }
            try
            {
                transport.Write(data, offset, len);
            }
            catch (Exception ex)
            {
                totalAttempts++;
                FailureReason reason = transport.Classify(ex);
                if (totalAttempts >= 12)
                {
                    job.State = offset > 0 ? JobState.SentIndeterminate : JobState.Failed;
                    if (job.State == JobState.SentIndeterminate)
                        Log(string.Format("Job \"{0}\" em estado INDETERMINADO: {1}/{2} bytes ja tinham sido entregues ao sistema quando as tentativas de escrita esgotaram - resultado fisico desconhecido, NAO sera reenviado automaticamente.", job.Label, offset, data.Length));
                    throw new Exception("Falha persistente ao gravar: " + ex.Message);
                }
                if (reason == FailureReason.Busy)
                {
                    Log(string.Format("Impressora ocupada no job \"{0}\" ({1} bytes pendentes): aguardando drenar o buffer...", job.Label, data.Length - offset));
                    Thread.Sleep(2000);
                    continue;
                }
                Log(string.Format("Erro ao gravar \"{0}\": {1} (causa: {2}) - reconectando ({3}/12)...", job.Label, ex.Message, reason, totalAttempts));
                PrinterManager.Current.ForceCloseTransport();
                Thread.Sleep(1500);
                continue;
            }
            offset += len;
            PrinterManager.Current.NotifyCommunication();
            if (offset < data.Length && prof.PauseMs > 0) Thread.Sleep(prof.PauseMs);
        }
        job.State = JobState.Completed;
        PrinterManager.Current.NotifyPrintSuccess();
        Log(string.Format("Impressao \"{0}\" concluida ({1} bytes, chunks de {2} B) em {3} ({4}) - perfil {5}.",
            job.Label, data.Length, prof.ChunkBytes, pi.Name, pi.Id, prof.Name));
    }

    // Teste de estresse controlado (pedido do usuario, item 17): conectar -> imprimir teste
    // -> desconectar NOSSO PROPRIO transporte (nunca a impressora fisica - isso exigiria
    // autorizacao/acao do operador) -> reconectar -> repetir. Mede a confiabilidade REAL do
    // ciclo de reconexao em condicoes controladas, sem risco pro hardware.
    static object RunStressTest(int cycles)
    {
        PrinterInfo target = PrinterManager.Current.Target;
        IPrinterTransport transport = PrinterManager.Current.Transport;
        if (target == null || transport == null)
            throw new Exception("Nenhuma impressora conectada. Conecte uma impressora antes de rodar o teste de estresse.");
        if (!transport.Capabilities.CanReconnect)
            throw new Exception("Este transporte (" + transport.Kind + ") nao suporta ciclos de reconexao controlada - o teste de estresse se aplica a Bluetooth/USB.");
        // Nao roda por cima de impressao real em andamento - evita duas escritas
        // concorrentes no mesmo transporte (o job de teste NAO passa pela fila normal,
        // pra medir o ciclo conectar/desconectar sem depender do SenderLoop).
        if (QueuedCount() > 0 || ActiveJobs > 0)
            throw new Exception("Ha impressoes reais em andamento/na fila agora - espere esvaziar antes de rodar o teste de estresse.");

        int successfulReconnects = 0;
        int failedReconnects = 0;
        int printFailures = 0;

        Log(string.Format("Printer Stress Test iniciado: {0} ciclos em {1} ({2}).", cycles, target.Name, target.Id));

        for (int i = 1; i <= cycles; i++)
        {
            if (!PrinterManager.Current.IsReady)
            {
                if (!PrinterManager.Current.TryOpen()) { failedReconnects++; continue; }
            }
            try
            {
                var testJob = new PrintJob { Printer = target, Data = BuildSample("TESTE DE ESTRESSE " + i + "/" + cycles), Label = "stress-test-" + i };
                SendAndChunk(testJob);
            }
            catch (Exception ex)
            {
                printFailures++;
                Log(string.Format("Stress test ciclo {0}: falha ao imprimir - {1}", i, ex.Message));
            }

            // Desconecta SO' o nosso lado (fecha o handle da porta) - a impressora fisica
            // nunca e' desligada sozinha (pedido explicito do usuario). Isso e' o suficiente
            // pra forcar o Windows a tratar a proxima abertura como uma reconexao de verdade.
            PrinterManager.Current.ForceCloseTransport();
            Thread.Sleep(300);

            bool reconnected = false;
            for (int attempt = 0; attempt < 5 && !reconnected; attempt++)
            {
                if (attempt > 0) Thread.Sleep(1000);
                reconnected = PrinterManager.Current.TryOpen();
            }
            if (reconnected) successfulReconnects++; else failedReconnects++;
        }

        double successRate = cycles > 0 ? Math.Round((double)successfulReconnects / cycles * 100.0, 1) : 0;
        Log(string.Format("Printer Stress Test concluido: {0}/{1} reconexoes OK ({2}%), {3} falhas de impressao.",
            successfulReconnects, cycles, successRate, printFailures));

        return new
        {
            ok = true,
            cycles = cycles,
            successfulReconnects = successfulReconnects,
            failedReconnects = failedReconnects,
            successRate = successRate,
            printFailures = printFailures
        };
    }

    static readonly string[] STRONG_MARKS = { "bematech", "elgin", "daruma", "epson", "tm-", "tm t", "mp-4200", "mp-20", "mp20", "mpp-20", "kmex", "sweda", "vkp", "nkp", "80mm", "80 mm", "gr80", "hs-380", "gprinter", "3x3", "italtec", "c3tech" };
    static readonly string[] WEAK_MARKS = { "kp-", "kp ", "im-5", "im-6", "pos5", "m58", "58mm", "58 mm", "58b", "qs-", "xs-", "portatil", "portable", "58z", "58i" };

    static PrintProfile ProfileFor(PrinterInfo pi)
    {
        string hay = ((pi.Name ?? "") + " " + (pi.Detail ?? "")).ToLowerInvariant();
        foreach (string m in STRONG_MARKS)
            if (hay.Contains(m)) return new PrintProfile("forte", 8192, 60, 150, true);
        foreach (string m in WEAK_MARKS)
            if (hay.Contains(m)) return new PrintProfile("fraca", 512, 300, 900, true);
        return new PrintProfile("seguro", 1024, 250, 600, true);
    }

    // Tenta abrir uma porta (HTTP/TCP) indefinidamente com backoff, em vez de desistir para sempre na
    // primeira falha - importante porque logo apos um auto-restart a porta as vezes ainda esta sendo
    // liberada pelo processo anterior por um instante.
    static void StartListenerWithRetry(Action start, string label)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                start();
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                int delaySec = Math.Min(30, attempt * 2);
                Log(string.Format("Nao foi possivel iniciar {0} (tentativa {1}): {2}. Tentando de novo em {3}s...", label, attempt, ex.Message, delaySec));
                Thread.Sleep(delaySec * 1000);
            }
        }
    }

    static void HttpLoop()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(string.Format("http://localhost:{0}/", HttpPort));
        listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", HttpPort));
        StartListenerWithRetry(delegate { listener.Start(); }, "servidor HTTP na porta " + HttpPort);
        Log("Servidor HTTP no ar.");
        while (true)
        {
            try
            {
                var ctx = listener.GetContext();
                ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
            }
            catch { Thread.Sleep(100); }
        }
    }

    static void Handle(HttpListenerContext ctx)
    {
        try
        {
            var res = ctx.Response;
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type";

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            if (path == "" || path == "/") { ServeHtml(ctx); return; }

            if (path == "/printers")
            {
                var printers = FindPrinters();
                PrinterInfo activeTarget = PrinterManager.Current.Target;
                bool activeReady = PrinterManager.Current.IsReady;
                foreach (var p in printers)
                {
                    p.Status = (activeTarget != null && string.Equals(activeTarget.Id, p.Id, StringComparison.OrdinalIgnoreCase) && activeReady)
                        ? "conectado" : "disponivel";
                }
                Json(ctx, new { ok = true, printers = printers });
                return;
            }

            if (path == "/status")
            {
                PrinterInfo active = PrinterManager.Current.Target;
                string profile = active != null ? ProfileFor(active).Name : null;
                HealthSnapshot health = PrinterManager.Current.Snapshot(QueuedCount());
                ReliabilityStats stats = PrinterManager.Current.Stats;
                Json(ctx, new
                {
                    ok = true,
                    version = AppVersion,
                    connected = PrinterManager.Current.IsReady,
                    printer = active != null ? active.Name : null,
                    port = active != null ? active.Id : null,
                    type = active != null ? active.Type : null,
                    width = active != null ? active.Width : "58",
                    tries = PrinterManager.Current.ReconnectAttempts,
                    charset = LoadCharset(),
                    profile = profile,
                    queue = QueuedCount(),
                    printing = ActiveJobs,
                    licenca = ModoLicenca(),
                    printedOk = PrintedOk,
                    printedFail = PrintedFail,
                    lastError = LastError,
                    lastErrorAt = LastErrorAt.HasValue ? LastErrorAt.Value.ToString("HH:mm:ss") : null,
                    lastSuccessAt = LastSuccessAt.HasValue ? LastSuccessAt.Value.ToString("HH:mm:ss") : null,
                    // Bloco novo (camada universal de monitoramento, 2026-08-15) - so' ADITIVO,
                    // nenhum campo acima mudou de nome/tipo/semantica pra nao quebrar quem ja
                    // consome esta rota (Tipo7, Desktop, Android).
                    state = health.State.ToString(),
                    lastFailureReason = health.LastFailureReason.ToString(),
                    health = new
                    {
                        percent = health.HealthPercent,
                        lastCommunication = health.LastSuccessfulCommunication.HasValue ? health.LastSuccessfulCommunication.Value.ToString("HH:mm:ss") : null,
                        lastPrint = health.LastSuccessfulPrint.HasValue ? health.LastSuccessfulPrint.Value.ToString("HH:mm:ss") : null,
                        reconnectAttempts = health.ReconnectAttempts,
                        failedAttempts = health.FailedAttempts,
                        pendingJobs = health.PendingJobs
                    },
                    reliability = new
                    {
                        connectionAttempts = stats.ConnectionAttempts,
                        connectionsSuccessful = stats.ConnectionsSuccessful,
                        connectionsFailed = stats.ConnectionsFailed,
                        autoRecoveries = stats.AutoRecoveries,
                        unrecoverableFailures = stats.UnrecoverableFailures,
                        reconnectSuccessRate = stats.ReconnectSuccessRate
                    },
                    sistema = LoadApiKey() == null ? null : new
                    {
                        nome = SystemName,
                        valida = SystemKeyValid,
                        checadoEm = SystemLastCheck.HasValue ? SystemLastCheck.Value.ToString("HH:mm:ss") : null,
                        erro = SystemLastError
                    }
                });
                return;
            }

            if (path == "/licenca")
            {
                string[] allowed = LoadOrigins();
                Json(ctx, new
                {
                    ok = true,
                    modo = ModoLicenca(),
                    licenca = allowed == null ? "gratis" : "licenciada",
                    origens = allowed ?? new string[0],
                    sistema = LoadApiKey() == null ? null : new
                    {
                        nome = SystemName,
                        valida = SystemKeyValid,
                        checadoEm = SystemLastCheck.HasValue ? SystemLastCheck.Value.ToString("HH:mm:ss") : null,
                        erro = SystemLastError
                    }
                });
                return;
            }

            // Visao completa de diagnostico (pedido do usuario, item 15): impressora +
            // adaptador Bluetooth (quando aplicavel) + confiabilidade. Formato pra um painel,
            // nao pro fluxo de impressao normal - so' faz a consulta WMI detalhada do
            // adaptador aqui, nunca no caminho quente de impressao.
            if (path == "/diagnostics")
            {
                PrinterInfo target = PrinterManager.Current.Target;
                IPrinterTransport transport = PrinterManager.Current.Transport;
                HealthSnapshot health = PrinterManager.Current.Snapshot(QueuedCount());
                ReliabilityStats stats = PrinterManager.Current.Stats;

                object adapterInfo = null;
                if (transport != null && transport.Kind == TransportKind.Bluetooth)
                {
                    var a = PrinterManager.Current.Adapter.Check(true);
                    adapterInfo = new
                    {
                        name = a.AdapterName,
                        instanceId = a.InstanceId,
                        status = a.Status,
                        enabled = a.Enabled,
                        problemCode = a.ProblemCode,
                        problemDescription = a.Detail,
                        driverVersion = a.DriverVersion,
                        driverProvider = a.DriverProvider,
                        adapterCount = a.AdapterCount,
                        needsReboot = a.NeedsReboot
                    };
                }

                Json(ctx, new
                {
                    ok = true,
                    printer = target == null ? null : new
                    {
                        name = target.Name,
                        connection = target.Type,
                        transport = transport != null ? transport.Kind.ToString() : null,
                        status = health.State.ToString(),
                        healthPercent = health.HealthPercent,
                        mac = target.Type == "bluetooth" ? target.Detail : null,
                        endpoint = target.Id,
                        lastCommunication = health.LastSuccessfulCommunication.HasValue ? health.LastSuccessfulCommunication.Value.ToString("HH:mm:ss") : null,
                        lastPrint = health.LastSuccessfulPrint.HasValue ? health.LastSuccessfulPrint.Value.ToString("HH:mm:ss") : null,
                        reconnects = health.ReconnectAttempts,
                        errors = health.FailedAttempts,
                        lastError = health.LastFailureDetail,
                        lastFailureReason = health.LastFailureReason.ToString(),
                        queue = health.PendingJobs
                    },
                    adapter = adapterInfo,
                    reliability = new
                    {
                        connectionAttempts = stats.ConnectionAttempts,
                        connectionsSuccessful = stats.ConnectionsSuccessful,
                        connectionsFailed = stats.ConnectionsFailed,
                        autoRecoveries = stats.AutoRecoveries,
                        unrecoverableFailures = stats.UnrecoverableFailures,
                        reconnectSuccessRate = stats.ReconnectSuccessRate,
                        printJobsTotal = PrintedOk + PrintedFail,
                        printJobsSuccessful = PrintedOk,
                        printJobsFailed = PrintedFail
                    }
                });
                return;
            }

            if (path == "/diagnostics/stress-test" && ctx.Request.HttpMethod == "POST")
            {
                var body = ReadBody(ctx.Request);
                var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                int cycles = 5;
                try { if (doc != null && doc.ContainsKey("cycles")) cycles = Convert.ToInt32(doc["cycles"]); } catch { }
                if (cycles < 1) cycles = 1;
                if (cycles > 50) cycles = 50; // limite de seguranca (pedido do usuario: teste controlado, nao um stress infinito)

                object result;
                try
                {
                    result = RunStressTest(cycles);
                }
                catch (Exception ex)
                {
                    Json(ctx, new { ok = false, error = ex.Message });
                    return;
                }
                Json(ctx, result);
                return;
            }

            if (path == "/capabilities")
            {
                PrinterInfo pi = PrinterManager.Current.Target;
                if (pi == null) { Json(ctx, new { ok = false, error = "Nenhuma impressora conectada" }); return; }
                PrintProfile prof = ProfileFor(pi);
                Json(ctx, new
                {
                    ok = true,
                    model = pi.Name,
                    type = pi.Type,
                    width = pi.Width,
                    profile = prof.Name,
                    chunkBytes = prof.ChunkBytes,
                    pauseMs = prof.PauseMs,
                    pauseBetweenJobsMs = prof.PauseBetweenJobsMs,
                    qrSupport = prof.QrSupport,
                    charset = LoadCharset()
                });
                return;
            }

            if (path == "/connect" && ctx.Request.HttpMethod == "POST")
            {
                var body = ReadBody(ctx.Request);
                var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                string id = Convert.ToString(doc["printer"]);
                PrinterInfo target = null;
                foreach (var p in FindPrinters())
                {
                    if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) target = p;
                }
                if (target == null) { Json(ctx, new { ok = false, error = "Impressora nao encontrada" }); return; }
                bool connected = PrinterManager.Current.Connect(target);
                SaveConfig(target.Id, LoadCharset());
                if (!connected)
                {
                    Json(ctx, new { ok = false, error = "Impressora selecionada, mas nao respondeu ao conectar agora. Confira se esta ligada e por perto." });
                    return;
                }
                Json(ctx, new { ok = true });
                return;
            }

            if (path == "/config" && ctx.Request.HttpMethod == "POST")
            {
                var body = ReadBody(ctx.Request);
                var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                string charset = Convert.ToString(doc["charset"]);
                if (charset != "ascii" && charset != "cp850") { Json(ctx, new { ok = false, error = "charset deve ser ascii ou cp850" }); return; }
                SaveConfig(PrinterManager.Current.Target != null ? PrinterManager.Current.Target.Id : (LoadConfig() ?? ""), charset);
                if (doc.ContainsKey("apiKey"))
                {
                    string newKey = Convert.ToString(doc["apiKey"]);
                    string newBackend = doc.ContainsKey("backendUrl") ? Convert.ToString(doc["backendUrl"]) : LoadBackendUrl();
                    SaveSystemConfig(newKey, newBackend);
                    lock (Lock) { SystemKeyValid = null; SystemName = null; SystemLastError = null; SystemLastCheck = null; }
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { if (!string.IsNullOrEmpty(newKey)) ValidateSystemKey(newKey); }
                        catch (Exception ex) { Log("Erro ao validar chave de sistema recem-configurada: " + ex.Message); }
                    });
                }
                Json(ctx, new { ok = true, charset = charset });
                return;
            }

            if (path == "/ticket" && ctx.Request.HttpMethod == "POST")
            {
                if (!OriginAutorizada(ctx)) { Json(ctx, new { ok = false, licenca = "bloqueada", error = "Uso nao autorizado: este servidor atende apenas origens licenciadas." }, 403); return; }
                if (!SistemaAutorizado()) { Json(ctx, new { ok = false, licenca = "bloqueada", error = "Chave de sistema invalida ou revogada." }, 403); return; }
                var body = ReadBody(ctx.Request);
                var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);

                string dedupKey = doc.ContainsKey("requestId") ? "req:" + Convert.ToString(doc["requestId"])
                                 : (doc.ContainsKey("code") && doc["code"] != null ? "ticket:" + Convert.ToString(doc["code"]) : null);
                if (dedupKey != null && WasRequestRecentlyHandled(dedupKey))
                {
                    Log("Ticket duplicado ignorado (reenvio dentro de " + (int)DedupWindow.TotalSeconds + "s): " + dedupKey);
                    Json(ctx, new { ok = true, duplicate = true, queue = QueuedCount() });
                    return;
                }

                if (doc.ContainsKey("printer"))
                {
                    string wanted = Convert.ToString(doc["printer"]);
                    PrinterInfo currentTarget = PrinterManager.Current.Target;
                    bool already = currentTarget != null && string.Equals(currentTarget.Id, wanted, StringComparison.OrdinalIgnoreCase);
                    if (!already)
                    {
                        PrinterInfo target = null;
                        foreach (var p in FindPrinters())
                        {
                            if (string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase)) target = p;
                        }
                        if (target == null) { Json(ctx, new { ok = false, error = "Impressora nao encontrada: " + wanted }); return; }
                        PrinterManager.Current.Connect(target);
                    }
                }
                string charset = doc.ContainsKey("charset") ? Convert.ToString(doc["charset"]) : LoadCharset();
                if (charset != "cp850") charset = "ascii";
                int qrStart, qrLen;
                byte[] data = BuildTicket(doc, charset, out qrStart, out qrLen);
                PrinterInfo pi = PrinterManager.Current.Target;
                EnqueueJob(pi, data, "ticket", qrStart, qrLen);
                if (dedupKey != null) MarkRequestHandled(dedupKey);
                Json(ctx, new { ok = true, bytes = data.Length, queue = QueuedCount() });
                return;
            }

            if (path == "/print" && ctx.Request.HttpMethod == "POST")
            {
                if (!OriginAutorizada(ctx)) { Json(ctx, new { ok = false, licenca = "bloqueada", error = "Uso nao autorizado: este servidor atende apenas origens licenciadas." }, 403); return; }
                if (!SistemaAutorizado()) { Json(ctx, new { ok = false, licenca = "bloqueada", error = "Chave de sistema invalida ou revogada." }, 403); return; }
                var body = ReadBody(ctx.Request);
                var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);

                string printDedupKey = doc.ContainsKey("requestId") ? "req:" + Convert.ToString(doc["requestId"]) : null;
                if (printDedupKey != null && WasRequestRecentlyHandled(printDedupKey))
                {
                    Log("Impressao duplicada ignorada (reenvio dentro de " + (int)DedupWindow.TotalSeconds + "s): " + printDedupKey);
                    Json(ctx, new { ok = true, duplicate = true, queue = QueuedCount() });
                    return;
                }

                if (doc.ContainsKey("printer"))
                {
                    string wanted = Convert.ToString(doc["printer"]);
                    PrinterInfo currentTarget = PrinterManager.Current.Target;
                    bool already = currentTarget != null && string.Equals(currentTarget.Id, wanted, StringComparison.OrdinalIgnoreCase);
                    if (!already)
                    {
                        PrinterInfo target = null;
                        foreach (var p in FindPrinters())
                        {
                            if (string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase)) target = p;
                        }
                        if (target == null) { Json(ctx, new { ok = false, error = "Impressora nao encontrada: " + wanted }); return; }
                        PrinterManager.Current.Connect(target);
                    }
                }
                string mode = Convert.ToString(doc.ContainsKey("mode") ? doc["mode"] : "escpos");
                byte[] data = null;
                if (mode == "raw")
                {
                    string b64 = Convert.ToString(doc["data"]);
                    data = Convert.FromBase64String(b64);
                }
                else if (mode == "text")
                {
                    string text = Convert.ToString(doc["data"]);
                    string charset = doc.ContainsKey("charset") ? Convert.ToString(doc["charset"]) : LoadCharset();
                    if (charset != "cp850") charset = "ascii";
                    var enc = charset == "cp850" ? Encoding.GetEncoding(850) : Encoding.GetEncoding("ISO-8859-1");
                    if (charset == "ascii") text = NormalizeText(text);
                    var ms = new MemoryStream();
                    byte[] init = { 0x1B, 0x40, 0x1B, 0x74, 0x00 };
                    ms.Write(init, 0, init.Length);
                    if (charset == "cp850")
                    {
                        byte[] cp = { 0x1B, 0x74, 0x02 };
                        ms.Write(cp, 0, cp.Length);
                    }
                    string[] lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
                    foreach (string line in lines)
                    {
                        byte[] lb = enc.GetBytes(line);
                        ms.Write(lb, 0, lb.Length);
                        ms.WriteByte(0x0A);
                    }
                    byte[] feed = { 0x1B, 0x64, 0x03 };
                    ms.Write(feed, 0, feed.Length);
                    data = ms.ToArray();
                }
                else if (mode == "escpos")
                {
                    data = BuildSample(doc.ContainsKey("data") ? Convert.ToString(doc["data"]) : null);
                }
                if (data == null || data.Length == 0) { Json(ctx, new { ok = false, error = "Dados vazios" }); return; }
                PrinterInfo pi = PrinterManager.Current.Target;
                EnqueueJob(pi, data, "print");
                if (printDedupKey != null) MarkRequestHandled(printDedupKey);
                Json(ctx, new { ok = true, bytes = data.Length, queue = QueuedCount() });
                return;
            }

            Json(ctx, new { ok = false, error = "Rota desconhecida: " + path }, 404);
        }
        catch (Exception ex)
        {
            try { Json(ctx, new { ok = false, error = ex.Message }, 500); }
            catch { }
            Log("Erro no HTTP: " + ex.Message);
        }
    }

    static string NormalizeText(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var map = new Dictionary<char, string>
        {
            { 'á', "a" }, { 'à', "a" }, { 'â', "a" }, { 'ã', "a" }, { 'ä', "a" },
            { 'é', "e" }, { 'è', "e" }, { 'ê', "e" }, { 'ë', "e" },
            { 'í', "i" }, { 'ì', "i" }, { 'î', "i" }, { 'ï', "i" },
            { 'ó', "o" }, { 'ò', "o" }, { 'ô', "o" }, { 'õ', "o" }, { 'ö', "o" },
            { 'ú', "u" }, { 'ù', "u" }, { 'û', "u" }, { 'ü', "u" },
            { 'ç', "c" }, { 'ñ', "n" }, { 'ß', "ss" }, { 'ª', "a" }, { 'º', "o" },
            { 'Á', "A" }, { 'À', "A" }, { 'Â', "A" }, { 'Ã', "A" }, { 'Ä', "A" },
            { 'É', "E" }, { 'È', "E" }, { 'Ê', "E" }, { 'Ë', "E" },
            { 'Í', "I" }, { 'Ì', "I" }, { 'Î', "I" }, { 'Ï', "I" },
            { 'Ó', "O" }, { 'Ò', "O" }, { 'Ô', "O" }, { 'Õ', "O" }, { 'Ö', "O" },
            { 'Ú', "U" }, { 'Ù', "U" }, { 'Û', "U" }, { 'Ü', "U" },
            { 'Ç', "C" }, { 'Ñ', "N" }
        };
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            string r;
            if (map.TryGetValue(c, out r)) sb.Append(r);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static byte[] BuildTicket(Dictionary<string, object> doc, string charset)
    {
        int qrStart, qrLen;
        return BuildTicket(doc, charset, out qrStart, out qrLen);
    }

    static byte[] BuildTicket(Dictionary<string, object> doc, string charset, out int qrCommandStart, out int qrCommandLen)
    {
        string title = Get(doc, "title", "INGRESSO");
        string eventName = Get(doc, "event", null);
        string date = Get(doc, "date", null);
        string sector = Get(doc, "sector", null);
        string local = Get(doc, "local", null);
        string buyer = Get(doc, "buyer", null);
        string code = Get(doc, "code", null);
        string price = Get(doc, "price", null);
        string qr = Get(doc, "qr", code != null ? code : "TIPO7");

        if (charset == "ascii")
        {
            title = NormalizeText(title);
            eventName = NormalizeText(eventName);
            date = NormalizeText(date);
            sector = NormalizeText(sector);
            local = NormalizeText(local);
            buyer = NormalizeText(buyer);
            price = NormalizeText(price);
        }
        var enc = charset == "cp850" ? Encoding.GetEncoding(850) : Encoding.GetEncoding("ISO-8859-1");
        var ms = new MemoryStream();
        byte[] init = { 0x1B, 0x40 };
        ms.Write(init, 0, init.Length);
        if (charset == "cp850")
        {
            byte[] cp = { 0x1B, 0x74, 0x02 };
            ms.Write(cp, 0, cp.Length);
        }
        byte[] center = { 0x1B, 0x61, 0x01 };
        byte[] left = { 0x1B, 0x61, 0x00 };
        byte[] boldOn = { 0x1B, 0x45, 0x01 };
        byte[] boldOff = { 0x1B, 0x45, 0x00 };
        byte[] tall = { 0x1B, 0x21, 0x10 };
        byte[] normal = { 0x1B, 0x21, 0x00 };

        ms.Write(center, 0, center.Length);
        ms.Write(tall, 0, tall.Length);
        WriteLine(ms, enc, title);
        ms.Write(normal, 0, normal.Length);
        WriteLine(ms, enc, "");
        if (!string.IsNullOrEmpty(eventName))
        {
            ms.Write(boldOn, 0, boldOn.Length);
            WriteWrapped(ms, enc, eventName);
            ms.Write(boldOff, 0, boldOff.Length);
        }
        if (!string.IsNullOrEmpty(date)) WriteWrapped(ms, enc, "Data: " + date);
        if (!string.IsNullOrEmpty(local)) WriteWrapped(ms, enc, "Local: " + local);
        if (!string.IsNullOrEmpty(sector)) WriteWrapped(ms, enc, "Setor: " + sector);
        WriteLine(ms, enc, "");
        ms.Write(left, 0, left.Length);
        WriteLine(ms, enc, "--------------------------------");
        if (!string.IsNullOrEmpty(buyer)) WriteWrapped(ms, enc, "Nome: " + buyer);
        if (!string.IsNullOrEmpty(code)) WriteLine(ms, enc, "Codigo: " + code);
        if (!string.IsNullOrEmpty(price)) WriteLine(ms, enc, "Valor: " + price);
        WriteLine(ms, enc, "--------------------------------");
        ms.Write(center, 0, center.Length);
        WriteLine(ms, enc, "");
        // O comando ESC/POS do QR Code (guarda os dados + manda imprimir) nao pode ser
        // cortado no meio pelo chunking (SendAndChunk) - impressoras baratas perdem a
        // sincronia e imprimem os bytes do comando como texto cru em vez de executar
        // (achado real em producao, 2026-08-14). Guarda o intervalo exato pra proteger.
        qrCommandStart = (int)ms.Position;
        WriteQr(ms, NormalizeText(qr), 8);
        qrCommandLen = (int)ms.Position - qrCommandStart;
        WriteLine(ms, enc, "");
        WriteLine(ms, enc, "Aponte a camera para validar");
        WriteLine(ms, enc, "na portaria.");
        WriteLine(ms, enc, "");
        byte[] feed = { 0x1B, 0x64, 0x05 };
        ms.Write(feed, 0, feed.Length);
        return ms.ToArray();
    }

    static string Get(Dictionary<string, object> doc, string key, string def)
    {
        if (doc != null && doc.ContainsKey(key) && doc[key] != null) return Convert.ToString(doc[key]);
        return def;
    }

    static void WriteQr(MemoryStream ms, string content, int size)
    {
        byte[] data = Encoding.GetEncoding("ISO-8859-1").GetBytes(content);
        ms.WriteByte(0x1D);
        ms.WriteByte(0x28);
        ms.WriteByte(0x6B);
        ms.WriteByte(0x03);
        ms.WriteByte(0x00);
        ms.WriteByte(0x31);
        ms.WriteByte(0x43);
        ms.WriteByte((byte)size);
        ms.WriteByte(0x1D);
        ms.WriteByte(0x28);
        ms.WriteByte(0x6B);
        ms.WriteByte(0x03);
        ms.WriteByte(0x00);
        ms.WriteByte(0x31);
        ms.WriteByte(0x45);
        ms.WriteByte(0x32);
        ms.WriteByte(0x1D);
        ms.WriteByte(0x28);
        ms.WriteByte(0x6B);
        ms.WriteByte((byte)((data.Length + 3) & 0xFF));
        ms.WriteByte((byte)(((data.Length + 3) >> 8) & 0xFF));
        ms.WriteByte(0x00);
        ms.WriteByte(0x31);
        ms.WriteByte(0x50);
        ms.WriteByte(0x30);
        ms.Write(data, 0, data.Length);
        ms.WriteByte(0x1D);
        ms.WriteByte(0x28);
        ms.WriteByte(0x6B);
        ms.WriteByte(0x03);
        ms.WriteByte(0x00);
        ms.WriteByte(0x31);
        ms.WriteByte(0x51);
        ms.WriteByte(0x30);
    }

    static byte[] BuildSample(string title)
    {
        var enc = Encoding.GetEncoding("ISO-8859-1");
        var ms = new MemoryStream();
        byte[] init = { 0x1B, 0x40, 0x1B, 0x74, 0x00 };
        ms.Write(init, 0, init.Length);
        byte[] center = { 0x1B, 0x61, 0x01 };
        byte[] left = { 0x1B, 0x61, 0x00 };
        byte[] boldOn = { 0x1B, 0x45, 0x01 };
        byte[] boldOff = { 0x1B, 0x45, 0x00 };
        ms.Write(boldOn, 0, boldOn.Length);
        ms.Write(center, 0, center.Length);
        WriteLine(ms, enc, string.IsNullOrEmpty(title) ? "RAW BTS" : title);
        ms.Write(boldOff, 0, boldOff.Length);
        WriteLine(ms, enc, "Impressao via site");
        WriteLine(ms, enc, "");
        ms.Write(left, 0, left.Length);
        WriteLine(ms, enc, "--------------------------------");
        WriteLine(ms, enc, "Impressionado em: " + DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
        WriteLine(ms, enc, "--------------------------------");
        byte[] feed = { 0x1B, 0x64, 0x04 };
        ms.Write(feed, 0, feed.Length);
        return ms.ToArray();
    }

    static void WriteLine(MemoryStream ms, Encoding enc, string line)
    {
        byte[] b = enc.GetBytes(line);
        ms.Write(b, 0, b.Length);
        ms.WriteByte(0x0A);
    }

    // Quebra a linha por ESPACO antes de mandar pra impressora, em vez de deixar o
    // hardware dela quebrar sozinho no meio de uma palavra/valor por contagem de
    // caractere - achado real em producao (2026-08-14): "As 21:00" virava "As 21" numa
    // linha e ":00" sozinho na proxima, feio e confuso. 32 colunas = largura padrao 58mm
    // (a maioria das impressoras portateis baratas, incluindo a KP-1025 ja testada).
    static void WriteWrapped(MemoryStream ms, Encoding enc, string line, int width = 32)
    {
        if (string.IsNullOrEmpty(line)) { WriteLine(ms, enc, line); return; }
        while (line.Length > width)
        {
            int cut = line.LastIndexOf(' ', Math.Min(width, line.Length - 1));
            if (cut <= 0) cut = width; // palavra unica maior que a largura - corta mesmo
            WriteLine(ms, enc, line.Substring(0, cut));
            line = line.Substring(cut).TrimStart(' ');
        }
        WriteLine(ms, enc, line);
    }

    static string ReadBody(HttpListenerRequest req)
    {
        // Sempre UTF-8, nunca req.ContentEncoding: quando o cliente (Tipo7 etc) manda
        // "Content-Type: application/json" sem "; charset=utf-8" explicito (o normal - a
        // maioria dos fetch() nao especifica, e JSON e' UTF-8 por padrao no RFC 8259),
        // req.ContentEncoding do HttpListener NAO cai pra UTF-8 sozinho - ele usa o
        // codepage ANSI do Windows. Bug real, achado em producao 2026-08-14: acentos
        // vinham corrompidos no cupom impresso ("nao" virava "nAfO", "espaco" virava
        // "espaAo") porque os bytes UTF-8 de cada acento eram lidos um a um como se fossem
        // caracteres ANSI separados.
        using (var r = new StreamReader(req.InputStream, Encoding.UTF8))
        {
            return r.ReadToEnd();
        }
    }

    static void Json(HttpListenerContext ctx, object obj, int code = 200)
    {
        string json = new JavaScriptSerializer().Serialize(obj);
        byte[] buf = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.Close();
    }

    static void ServeHtml(HttpListenerContext ctx)
    {
        string html = Properties.SafeHtml;
        byte[] buf = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.Close();
    }

    static void TcpLoop()
    {
        var listener = new TcpListener(IPAddress.Any, TcpPort);
        StartListenerWithRetry(delegate { listener.Start(); }, "ponte TCP na porta " + TcpPort);
        Log(string.Format("Ponte TCP {0} no ar (para apps antigos).", TcpPort));
        while (true)
        {
            try
            {
                var client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        var net = client.GetStream();
                        byte[] buf = new byte[4096];
                        var ms = new MemoryStream();
                        int n;
                        while ((n = net.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                        }
                        if (ms.Length > 0)
                        {
                            PrinterInfo pi = PrinterManager.Current.Target;
                            if (pi != null) EnqueueJob(pi, ms.ToArray(), "tcp");
                            else Log("TCP: dados chegando sem impressora conectada - ignorado.");
                        }
                    }
                    catch { }
                    finally { try { client.Close(); } catch { } }
                });
            }
            catch { Thread.Sleep(100); }
        }
    }
}

public class PrinterInfo
{
    public string Type { get; set; }
    public string Name { get; set; }
    public string Id { get; set; }
    public string Detail { get; set; }
    public string Status { get; set; }
    public string Width { get; set; }
}

// WinPrinter mudou pra PrintTransport.cs em 2026-08-15 (junto com o BluetoothRfcommTransport)
// - a camada de transporte compila numa DLL separada agora (TipPrint.Transport.dll, ver
// dist/build.ps1), e WindowsPrinterTransport precisa dele. Ver PrintTransport.cs.

// Estado de entrega de um job - "Sending" cobre desde o primeiro byte escrito.
// "SentIndeterminate" e' o caso critico (pedido explicito do usuario, item 6): a conexao
// morreu depois de PARTE dos bytes ja terem sido entregues ao SO, entao nao sabemos se a
// impressora recebeu tudo, so' parte, ou nada - por isso nunca reenviamos esse job sozinhos.
// "Failed" so' acontece quando NADA saiu daqui ainda (offset==0), esse sim seria seguro pro
// chamador reenviar se quiser.
public enum JobState
{
    Queued,
    Sending,
    SentIndeterminate,
    Failed,
    Completed
}

class PrintJob
{
    public PrinterInfo Printer;
    public byte[] Data;
    public string Label;
    // Trecho que o chunking nao pode cortar no meio (ex: comando ESC/POS do QR Code) -
    // -1 = sem trecho protegido. Ver SendAndChunk.
    public int AtomicStart = -1;
    public int AtomicLen = 0;
    public JobState State = JobState.Queued;
    public DateTime EnqueuedAt = DateTime.Now;
}

class PrintProfile
{
    public string Name;
    public int ChunkBytes;
    public int PauseMs;
    public int PauseBetweenJobsMs;
    public bool QrSupport;

    public PrintProfile(string name, int chunk, int pause, int between, bool qr)
    {
        Name = name;
        ChunkBytes = chunk;
        PauseMs = pause;
        PauseBetweenJobsMs = between;
        QrSupport = qr;
    }
}

class RegistryWrapper
{
    public static RegKey OpenSubKey(string path)
    {
        try
        {
            Microsoft.Win32.RegistryKey k = null;
            if (path.StartsWith("HKLM"))
                k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path.Substring(5));
            else if (path.StartsWith("HKCU"))
                k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path.Substring(5));
            return k != null ? new RegKey(k) : null;
        }
        catch { return null; }
    }
}

class RegKey
{
    private Microsoft.Win32.RegistryKey _k;
    public RegKey(Microsoft.Win32.RegistryKey k) { _k = k; }

    public string[] SubKeys()
    {
        try { return _k.GetSubKeyNames(); }
        catch { return new string[0]; }
    }

    public string Value(string altPath, string name)
    {
        try
        {
            object v = _k.GetValue(name);
            if (v != null) return Convert.ToString(v);
        }
        catch { }
        return null;
    }

    public string BytesName(string altPath, string name)
    {
        try
        {
            object v = _k.GetValue(name);
            if (v is byte[])
            {
                byte[] b = (byte[])v;
                int len = Array.IndexOf(b, (byte)0);
                if (len < 0) len = b.Length;
                return System.Text.Encoding.ASCII.GetString(b, 0, len);
            }
            if (v != null) return Convert.ToString(v);
        }
        catch { }
        return null;
    }
}

class Properties
{
    public static string SafeHtml
    {
        get
        {
            return "<!DOCTYPE html><html lang='pt-br'><head><meta charset='utf-8'>" +
            "<title>TipPrint PrintServer</title>" +
            "<style>body{font-family:Segoe UI,Arial;max-width:760px;margin:32px auto;padding:0 16px;color:#212121}" +
            "h2{color:#1565C0;margin-bottom:4px} .sub{color:#616161;margin-top:0}" +
            ".card{background:#fff;border:1px solid #e0e0e0;border-radius:10px;padding:16px;margin:14px 0}" +
            "button{width:100%;padding:14px;margin:6px 0;font-size:16px;border:none;border-radius:8px;cursor:pointer}" +
            ".ok{background:#1565C0;color:#fff} .ok:hover{background:#0D47A1}" +
            ".sel{background:#E3F2FD;color:#1565C0;border:1px solid #1565C0;text-align:left}" +
            ".sel.conectada{background:#C8E6C9;color:#1B5E20;border-color:#1B5E20}" +
            ".st{font-weight:bold;padding:10px;border-radius:8px} .st.on{background:#C8E6C9;color:#1B5E20}" +
            ".st.off{background:#FFEBEE;color:#B71C1C}" +
            ".step{background:#F5F5F5;padding:12px;border-radius:8px;line-height:1.7;font-size:15px}" +
            "pre{background:#263238;color:#ECEFF1;padding:12px;overflow:auto;border-radius:8px;font-size:12px}" +
            "input{width:100%;padding:12px;margin:8px 0;box-sizing:border-box;border:1px solid #ccc;border-radius:8px;font-size:16px}" +
            "</style></head><body>" +
            "<h2>TipPrint PrintServer</h2>" +
            "<p class='sub'>Impressora do sistema conectada neste computador. Versao " + PrintServer.AppVersion + "</p>" +
            "<div id='status' class='st off'>Procurando impressora...</div>" +
            "<div id='health' class='st off' style='display:none;margin-top:8px'></div>" +
            "<div class='card'><h3>Como conectar a impressora (1a vez)</h3>" +
            "<div class='step'><b>1.</b> Ligue a impressora termica e ligue o Bluetooth do computador.<br>" +
            "<b>2.</b> Pareie a impressora no Windows (Configuracoes &gt; Bluetooth).<br>" +
            "<b>3.</b> Clique na impressora na lista abaixo.<br>" +
            "<b>4.</b> Pronto! Seu sistema ja imprime nela. O app lembra da impressora e reconecta sozinho.</div></div>" +
            "<div class='card'><h3>Impressoras encontradas</h3><div id='printers'></div></div>" +
            "<div class='card'><h3>Tabela de caracteres</h3>" +
            "<div class='step'><b>Impressora simples (58mm portatil):</b> use ASCII - acentos viram letras normais (nao, voce).<br>" +
            "<b>Impressora de loja (Bematech, Elgin, Epson):</b> use Latina CP850 - acentos reais (nao, voce).</div>" +
            "<button class='sel' onclick='setCharset(\"ascii\")'>ASCII - normaliza acentos (recomendado p/ portateis)</button>" +
            "<button class='sel' onclick='setCharset(\"cp850\")'>Latina CP850 - acentos reais (p/ impressoras de loja)</button>" +
            "<div id='charsetInfo' class='st off' style='margin-top:8px'></div></div>" +
            "<div class='card'><h3>Testar impressao</h3>" +
            "<button class='ok' onclick='testPrint()'>Imprimir cupom de teste</button>" +
            "<input id='title' placeholder='Titulo do cupom (opcional)'>" +
            "<button class='ok' onclick='testCustom()'>Imprimir com este titulo</button></div>" +
            "<div class='card'><h3>Integracao no seu sistema</h3>" +
            "<pre id='code'></pre></div>" +
            "<script>" +
            "async function refresh(){" +
            "var s=await (await fetch('/status')).json();" +
            "var st=document.getElementById('status');" +
            "if(s.connected){st.className='st on';st.textContent='Conectado: '+s.printer+' ('+s.port+') · Perfil '+s.profile+' · '+s.queue+' na fila'+(s.printing?' · imprimindo...':'');}" +
            "else{st.className='st off';st.textContent='Nenhuma impressora conectada. Escolha uma abaixo.';}" +
            "document.getElementById('charsetInfo').textContent='Tabela ativa: '+(s.charset=='cp850'?'Latina CP850 (acentos reais)':'ASCII (acentos normalizados)');" +
            "var h=document.getElementById('health');" +
            "if(s.printedFail>0){h.style.display='block';h.className='st off';h.textContent='Falhas de impressao: '+s.printedFail+(s.lastError?(' · Ultimo erro: '+s.lastError+(s.lastErrorAt?(' as '+s.lastErrorAt):'')):'');}" +
            "else{h.style.display='none';}" +
            "var p=await (await fetch('/printers')).json();" +
            "var d=document.getElementById('printers');d.innerHTML='';" +
            "if(p.printers.length==0){" +
            "var b=document.createElement('div');b.className='step';" +
            "b.textContent='Nenhuma impressora Bluetooth pareada encontrada. Siga o passo 1 e 2 acima.';d.appendChild(b);}" +
            "p.printers.forEach(function(x){" +
            "var b=document.createElement('button');b.className='sel';" +
            "b.classList.toggle('conectada',x.Status=='conectado');" +
            "var tipo={bluetooth:'Bt',usb:'USB',windows:'Windows'}[x.Type]||x.Type;" +
            "b.textContent=(x.Status=='conectado'?'CONECTADA - ':'')+x.Name+' ['+tipo+' · '+x.Width+'mm]';" +
            "b.title='Modelo/Tipo: '+x.Name+' ('+x.Type+') - largura estimada '+x.Width+'mm';" +
            "b.onclick=function(){fetch('/connect',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({printer:x.Id})}).then(function(){setTimeout(refresh,800);});};" +
            "d.appendChild(b);});" +
            "}" +
            "async function testPrint(){var r=await(await fetch('/print',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})).json();alert(r.ok?'Impressao enviada!':'Erro: '+r.error);}" +
            "async function testCustom(){var t=document.getElementById('title').value;var r=await(await fetch('/print',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({data:t})})).json();alert(r.ok?'Impressao enviada!':'Erro: '+r.error);}" +
            "async function setCharset(c){var r=await(await fetch('/config',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({charset:c})})).json();if(r.ok)refresh();else alert('Erro: '+r.error);}" +
            "document.getElementById('code').textContent=" +
            "'// No seu site:\\n'+" +
            "'const res = await fetch(\\\"http://localhost:8080/print\\\", {\\n'+" +
            "'  method: \\\"POST\\\",\\n'+" +
            "'  headers: { \\\"Content-Type\\\": \\\"application/json\\\" },\\n'+" +
            "'  body: JSON.stringify({ mode: \\\"text\\\",\\n'+" +
            "'    data: \\\"Cupom do sistema\\\\nLinha 2\\\\nValor: R$ 10,00\\\" })\\n'+" +
            "'});\\n'+" +
            "'const json = await res.json(); // { ok: true, bytes: 123 }';" +
            "refresh();setInterval(refresh,4000);" +
            "</scr" + "ipt></body></html>";
        }
    }
}