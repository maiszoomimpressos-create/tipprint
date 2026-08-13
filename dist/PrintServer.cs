using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

class PrintServer
{
    static int HttpPort = 8080;
    static int TcpPort = 9100;
    static string LogFile = null;
    static string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TipPrint", "config.txt");

    static readonly object Lock = new object();
    static PrinterInfo Active = null;
    static SerialPort ActivePort = null;
    static bool WantConnect = false;
    static int ReconnectTries = 0;

    static readonly object QueueLock = new object();
    static readonly Queue<PrintJob> JobQueue = new Queue<PrintJob>();
    static volatile int ActiveJobs = 0;

    static void Main(string[] args)
    {
        if (args.Length > 0) HttpPort = int.Parse(args[0]);
        if (args.Length > 1) TcpPort = int.Parse(args[1]);
        if (args.Length > 2) LogFile = args[2];

        Log("Iniciando TipPrint PrintServer...");

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

        string saved = LoadConfig();
        if (!string.IsNullOrEmpty(saved))
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(3000);
                Log("Reconectando a impressora salva: " + saved);
                AutoConnect(saved);
            });
        }

        Log(string.Format("Pronto. Painel: http://localhost:{0}  |  Impressao por rede TCP:{1}", HttpPort, TcpPort));
        Console.ReadLine();
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

    static string LoadCharset()
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

    static void SaveConfig(string id, string charset)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, id + Environment.NewLine + charset);
            Log(string.Format("Configuracao salva: impressora={0}, charset={1}", id, charset));
        }
        catch { }
    }

    static void AutoConnect(string id)
    {
        PrinterInfo target = null;
        foreach (var p in FindPrinters())
        {
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) target = p;
        }
        if (target == null)
        {
            Log("Impressora salva nao encontrada agora (" + id + "). Pareie-a e conecte pelo painel.");
            return;
        }
        ConnectPrinter(target);
    }

    static void Log(string msg)
    {
        string line = string.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), msg);
        Console.WriteLine(line);
        try
        {
            if (LogFile != null) File.AppendAllText(LogFile, line + Environment.NewLine);
        }
        catch { }
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

    static List<PrinterInfo> FindPrinters()
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

    static void ConnectPrinter(PrinterInfo pi)
    {
        lock (Lock)
        {
            CloseActiveLocked();
            Active = pi;
            WantConnect = true;
            ReconnectTries = 0;
        }
        if (pi.Type == "windows")
        {
            Log(string.Format("Impressora do Windows selecionada: {0}.", pi.Name));
            return;
        }
        Log(string.Format("Conectando a {0} ({1})...", pi.Name, pi.Id));
        TryOpen();
    }

    static void CloseActiveLocked()
    {
        WantConnect = false;
        if (ActivePort != null)
        {
            try { ActivePort.Close(); } catch { }
            ActivePort = null;
        }
    }

    static bool TryOpen()
    {
        PrinterInfo pi;
        lock (Lock)
        {
            if (!WantConnect || Active == null) return false;
            pi = Active;
        }
        try
        {
            var sp = new SerialPort(pi.Id, 115200, Parity.None, 8, StopBits.One)
            {
                WriteTimeout = 3000,
                ReadTimeout = 300
            };
            sp.Open();
            lock (Lock) ActivePort = sp;
            ReconnectTries = 0;
            Log(string.Format("Conectado: {0} na {1}.", pi.Name, pi.Id));
            return true;
        }
        catch (Exception ex)
        {
            ReconnectTries++;
            Log(string.Format("Falha ao conectar ({0}x): {1}", ReconnectTries, ex.Message));
            return false;
        }
    }

    static void WatchdogLoop()
    {
        while (true)
        {
            Thread.Sleep(4000);
            bool needReopen = false;
            lock (Lock)
            {
                if (WantConnect && ActivePort != null)
                {
                    if (!ActivePort.IsOpen) needReopen = true;
                }
                else if (WantConnect && ActivePort == null)
                {
                    needReopen = true;
                }
                if (Active != null && Active.Type == "windows") needReopen = false;
            }
            if (needReopen)
            {
                if (!TryOpen())
                {
                    int delay = Math.Min(30, 2 * ReconnectTries);
                    Thread.Sleep(delay * 1000);
                }
            }
        }
    }

    static void EnqueueJob(PrinterInfo pi, byte[] data, string label)
    {
        if (data == null || data.Length == 0) throw new Exception("Dados vazios.");
        if (pi == null) throw new Exception("Nenhuma impressora conectada. Use /connect primeiro.");
        lock (QueueLock)
        {
            JobQueue.Enqueue(new PrintJob { Printer = pi, Data = data, Label = label });
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
            lock (QueueLock)
            {
                while (JobQueue.Count == 0) Monitor.Wait(QueueLock);
                job = JobQueue.Dequeue();
                ActiveJobs++;
            }
            try
            {
                SendAndChunk(job);
            }
            catch (Exception ex)
            {
                Log(string.Format("Falha ao imprimir job \"{0}\": {1}", job.Label, ex.Message));
            }
            finally
            {
                lock (QueueLock) ActiveJobs--;
            }
            PrintProfile prof = ProfileFor(job.Printer);
            if (prof != null && prof.PauseBetweenJobsMs > 0) Thread.Sleep(prof.PauseBetweenJobsMs);
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
            Log(string.Format("Impressao \"{0}\" enviada ({1} bytes) para \"{2}\".", job.Label, job.Data.Length, pi.Name));
            return;
        }

        byte[] data = job.Data;
        int offset = 0;
        int totalAttempts = 0;

        while (offset < data.Length)
        {
            SerialPort sp;
            lock (Lock) sp = ActivePort;
            if (sp == null || !sp.IsOpen)
            {
                if (totalAttempts++ >= 12) throw new Exception("Impressora sem conexao apos 12 tentativas (job abortado).");
                Log(string.Format("Job \"{0}\" aguardando reconexao da impressora...", job.Label));
                if (!TryOpen()) Thread.Sleep(2000);
                continue;
            }
            int len = Math.Min(prof.ChunkBytes, data.Length - offset);
            try
            {
                sp.Write(data, offset, len);
            }
            catch (Exception ex)
            {
                totalAttempts++;
                if (totalAttempts >= 12) throw new Exception("Falha persistente ao gravar: " + ex.Message);
                bool busy = ex.Message != null && (ex.Message.ToLower().Contains("tempo") ||
                                                   ex.Message.ToLower().Contains("timeout") ||
                                                   ex.Message.ToLower().Contains("exced") ||
                                                   ex.Message.ToLower().Contains("timed"));
                if (busy)
                {
                    Log(string.Format("Impressora ocupada no job \"{0}\" ({1} bytes pendentes): aguardando drenar o buffer...", job.Label, data.Length - offset));
                    Thread.Sleep(2000);
                    continue;
                }
                Log(string.Format("Erro ao gravar \"{0}\": {1} - reconectando ({2}/12)...", job.Label, ex.Message, totalAttempts));
                lock (Lock)
                {
                    if (ActivePort != null) { try { ActivePort.Close(); } catch { } ActivePort = null; }
                }
                Thread.Sleep(1500);
                continue;
            }
            offset += len;
            if (offset < data.Length && prof.PauseMs > 0) Thread.Sleep(prof.PauseMs);
        }
        Log(string.Format("Impressao \"{0}\" concluida ({1} bytes, chunks de {2} B) em {3} ({4}) - perfil {5}.",
            job.Label, data.Length, prof.ChunkBytes, pi.Name, pi.Id, prof.Name));
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

    static void HttpLoop()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(string.Format("http://localhost:{0}/", HttpPort));
        listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", HttpPort));
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Log("Nao foi possivel iniciar o servidor HTTP: " + ex.Message);
            return;
        }
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
                lock (Lock)
                {
                    foreach (var p in printers)
                    {
                        p.Status = (Active != null && string.Equals(Active.Id, p.Id, StringComparison.OrdinalIgnoreCase) && (Active.Type == "windows" || (ActivePort != null && ActivePort.IsOpen)))
                            ? "conectado" : "disponivel";
                    }
                }
                Json(ctx, new { ok = true, printers = printers });
                return;
            }

            if (path == "/status")
            {
                string activeId = Active != null ? Active.Id : (LoadConfig() ?? "");
                string profile = Active != null ? ProfileFor(Active).Name : null;
                Json(ctx, new
                {
                    ok = true,
                    connected = (ActivePort != null && ActivePort.IsOpen) || (Active != null && Active.Type == "windows"),
                    printer = Active != null ? Active.Name : null,
                    port = Active != null ? Active.Id : null,
                    type = Active != null ? Active.Type : null,
                    width = Active != null ? Active.Width : "58",
                    tries = ReconnectTries,
                    charset = LoadCharset(),
                    profile = profile,
                    queue = QueuedCount(),
                    printing = ActiveJobs,
                    licenca = ModoLicenca()
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
                    origens = allowed ?? new string[0]
                });
                return;
            }

            if (path == "/capabilities")
            {
                PrinterInfo pi;
                lock (Lock) pi = Active;
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
                ConnectPrinter(target);
                SaveConfig(target.Id, LoadCharset());
                Json(ctx, new { ok = true });
                return;
            }

            if (path == "/config" && ctx.Request.HttpMethod == "POST")
            {
                var body = ReadBody(ctx.Request);
                var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                string charset = Convert.ToString(doc["charset"]);
                if (charset != "ascii" && charset != "cp850") { Json(ctx, new { ok = false, error = "charset deve ser ascii ou cp850" }); return; }
                SaveConfig(Active != null ? Active.Id : (LoadConfig() ?? ""), charset);
                Json(ctx, new { ok = true, charset = charset });
                return;
            }

            if (path == "/ticket" && ctx.Request.HttpMethod == "POST")
            {
                if (!OriginAutorizada(ctx)) { Json(ctx, new { ok = false, licenca = "bloqueada", error = "Uso nao autorizado: este servidor atende apenas origens licenciadas." }, 403); return; }
                var body = ReadBody(ctx.Request);
                var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                if (doc.ContainsKey("printer"))
                {
                    string wanted = Convert.ToString(doc["printer"]);
                    bool already = false;
                    lock (Lock)
                    {
                        if (Active != null && string.Equals(Active.Id, wanted, StringComparison.OrdinalIgnoreCase)) already = true;
                    }
                    if (!already)
                    {
                        PrinterInfo target = null;
                        foreach (var p in FindPrinters())
                        {
                            if (string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase)) target = p;
                        }
                        if (target == null) { Json(ctx, new { ok = false, error = "Impressora nao encontrada: " + wanted }); return; }
                        ConnectPrinter(target);
                    }
                }
                string charset = doc.ContainsKey("charset") ? Convert.ToString(doc["charset"]) : LoadCharset();
                if (charset != "cp850") charset = "ascii";
                byte[] data = BuildTicket(doc, charset);
                PrinterInfo pi;
                lock (Lock) pi = Active;
                EnqueueJob(pi, data, "ticket");
                Json(ctx, new { ok = true, bytes = data.Length, queue = QueuedCount() });
                return;
            }

            if (path == "/print" && ctx.Request.HttpMethod == "POST")
            {
                if (!OriginAutorizada(ctx)) { Json(ctx, new { ok = false, licenca = "bloqueada", error = "Uso nao autorizado: este servidor atende apenas origens licenciadas." }, 403); return; }
                var body = ReadBody(ctx.Request);
                var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                if (doc.ContainsKey("printer"))
                {
                    string wanted = Convert.ToString(doc["printer"]);
                    bool already = false;
                    lock (Lock)
                    {
                        if (Active != null && string.Equals(Active.Id, wanted, StringComparison.OrdinalIgnoreCase)) already = true;
                    }
                    if (!already)
                    {
                        PrinterInfo target = null;
                        foreach (var p in FindPrinters())
                        {
                            if (string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase)) target = p;
                        }
                        if (target == null) { Json(ctx, new { ok = false, error = "Impressora nao encontrada: " + wanted }); return; }
                        ConnectPrinter(target);
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
                PrinterInfo pi;
                lock (Lock) pi = Active;
                EnqueueJob(pi, data, "print");
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
            WriteLine(ms, enc, eventName);
            ms.Write(boldOff, 0, boldOff.Length);
        }
        if (!string.IsNullOrEmpty(date)) WriteLine(ms, enc, "Data: " + date);
        if (!string.IsNullOrEmpty(local)) WriteLine(ms, enc, "Local: " + local);
        if (!string.IsNullOrEmpty(sector)) WriteLine(ms, enc, "Setor: " + sector);
        WriteLine(ms, enc, "");
        ms.Write(left, 0, left.Length);
        WriteLine(ms, enc, "--------------------------------");
        if (!string.IsNullOrEmpty(buyer)) WriteLine(ms, enc, "Nome: " + buyer);
        if (!string.IsNullOrEmpty(code)) WriteLine(ms, enc, "Codigo: " + code);
        if (!string.IsNullOrEmpty(price)) WriteLine(ms, enc, "Valor: " + price);
        WriteLine(ms, enc, "--------------------------------");
        ms.Write(center, 0, center.Length);
        WriteLine(ms, enc, "");
        WriteQr(ms, NormalizeText(qr), 8);
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

    static string ReadBody(HttpListenerRequest req)
    {
        using (var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
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
        try { listener.Start(); }
        catch (Exception ex) { Log("TCP 9100 nao iniciou: " + ex.Message); return; }
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
                            PrinterInfo pi;
                            lock (Lock) pi = Active;
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

class PrinterInfo
{
    public string Type { get; set; }
    public string Name { get; set; }
    public string Id { get; set; }
    public string Detail { get; set; }
    public string Status { get; set; }
    public string Width { get; set; }
}

class WinPrinter
{
    [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
    static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
    static extern bool ClosePrinter(IntPtr hPrinter);

    [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
    static extern bool StartDocPrinter(IntPtr hPrinter, int level, [System.Runtime.InteropServices.In] DOC_INFO_1 di);

    [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
    static extern bool EndDocPrinter(IntPtr hPrinter);

    [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
    static extern bool StartPagePrinter(IntPtr hPrinter);

    [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
    static extern bool EndPagePrinter(IntPtr hPrinter);

    [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
    static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct DOC_INFO_1
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string pDocName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string pOutputFile;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string pDatatype;
    }

    public static void RawPrint(string printerName, byte[] data)
    {
        IntPtr hprinter;
        if (!OpenPrinter(printerName, out hprinter, IntPtr.Zero))
            throw new Exception(string.Format("Nao foi possivel abrir a impressora \"{0}\".", printerName));
        try
        {
            DOC_INFO_1 di = new DOC_INFO_1
            {
                pDocName = "TipPrint",
                pOutputFile = null,
                pDatatype = "RAW"
            };
            if (!StartDocPrinter(hprinter, 1, di))
                throw new Exception(string.Format("Nao foi possivel iniciar o documento na impressora \"{0}\".", printerName));
            try
            {
                StartPagePrinter(hprinter);
                int written;
                int offset = 0;
                int page = 8192;
                while (offset < data.Length)
                {
                    int len = Math.Min(page, data.Length - offset);
                    byte[] chunk = new byte[len];
                    Array.Copy(data, offset, chunk, 0, len);
                    if (!WritePrinter(hprinter, chunk, len, out written))
                        throw new Exception("Falha ao gravar dados na impressora.");
                    offset += written;
                }
                EndPagePrinter(hprinter);
            }
            finally
            {
                EndDocPrinter(hprinter);
            }
        }
        finally
        {
            ClosePrinter(hprinter);
        }
    }
}

class PrintJob
{
    public PrinterInfo Printer;
    public byte[] Data;
    public string Label;
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
            "<p class='sub'>Impressora do sistema conectada neste computador.</p>" +
            "<div id='status' class='st off'>Procurando impressora...</div>" +
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
            "var p=await (await fetch('/printers')).json();" +
            "var d=document.getElementById('printers');d.innerHTML='';" +
            "if(p.printers.length==0){" +
            "var b=document.createElement('div');b.className='step';" +
            "b.textContent='Nenhuma impressora Bluetooth pareada encontrada. Siga o passo 1 e 2 acima.';d.appendChild(b);}" +
            "p.printers.forEach(function(x){" +
            "var b=document.createElement('button');b.className='sel';" +
            "b.classList.toggle('conectada',x.status=='conectado');" +
            "var tipo={bluetooth:'Bt',usb:'USB',windows:'Windows'}[x.Type]||x.Type;" +
            "b.textContent=(x.status=='conectado'?'CONECTADA - ':'')+x.name+' ['+tipo+' · '+x.Width+'mm]';" +
            "b.title='Modelo/Tipo: '+x.Name+' ('+x.Type+') - largura estimada '+x.Width+'mm';" +
            "b.onclick=function(){fetch('/connect',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({printer:x.id})}).then(function(){setTimeout(refresh,800);});};" +
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