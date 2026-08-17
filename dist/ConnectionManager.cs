using System;
using System.Threading;

// ===========================================================================================
// TipPrint - Camada central de gerenciamento de conexao (Connection Manager).
//
// Unico dono da maquina de estados, do backoff de reconexao e das estatisticas de
// confiabilidade da impressora ativa. O modelo atual do PrintServer so' suporta UMA
// impressora conectada por vez - isso e' preservado aqui (nao e' um multi-printer manager
// ainda). Absorve o que antes eram os campos soltos Active/ActivePort/WantConnect/
// ReconnectTries de PrintServer.cs e o corpo do WatchdogLoop - a logica de reconexao/
// backoff/redescoberta por MAC e' A MESMA (TryOpen, TryReacquireByMac), so' organizada
// atras de uma maquina de estados explicita com causa de falha classificada.
//
// Quem quiser expandir pra varias impressoras simultaneas no futuro: o ponto de extensao e'
// trocar PrinterManager.Current (uma instancia) por um Dictionary<string, ConnectionManager>
// chaveado por PrinterIdentity.PreferredKey - o ConnectionManager em si ja e' independente,
// nao guarda nada em campos estaticos do PrintServer.
// ===========================================================================================

public class ReconnectPolicy
{
    // 0s, 1s, 2s, 5s, 10s, 20s, 30s, 30s... (pedido explicito do usuario: nem loop agressivo
    // infinito, nem desistir pra sempre - reconectar a impressora termica durante um evento
    // ao vivo e' o caso de uso central do TipPrint).
    static readonly int[] BackoffSeconds = { 0, 1, 2, 5, 10, 20, 30 };

    public int DelaySecondsFor(int attemptNumber)
    {
        if (attemptNumber < 0) attemptNumber = 0;
        int idx = Math.Min(attemptNumber, BackoffSeconds.Length - 1);
        return BackoffSeconds[idx];
    }
}

// Contadores cumulativos de confiabilidade (pedido do usuario, item 16: "nao precisa
// chamar de Health 92%, quero medir a confiabilidade REAL"). Numeros brutos primeiro,
// percentual derivado depois - nunca o contrario.
public class ReliabilityStats
{
    public int ConnectionAttempts;
    public int ConnectionsSuccessful;
    public int ConnectionsFailed;
    public int AutoRecoveries;
    public int UnrecoverableFailures;

    public double? ReconnectSuccessRate
    {
        get { return ConnectionAttempts > 0 ? (double)ConnectionsSuccessful / ConnectionAttempts * 100.0 : (double?)null; }
    }
}

// Snapshot de saude de UMA impressora (pedido do usuario, item 3 - formato de consulta tipo
// "Health Monitor"). Numeros brutos + um percentual derivado simples (resumo visual, nao e'
// a fonte da verdade).
public class HealthSnapshot
{
    public string PrinterName;
    public string Transport;
    public PrinterState State;
    public FailureReason LastFailureReason;
    public string LastFailureDetail;
    public DateTime? LastSuccessfulCommunication;
    public DateTime? LastSuccessfulPrint;
    public int ReconnectAttempts;
    public int FailedAttempts;
    public int PendingJobs;
    public int HealthPercent;
}

public class ConnectionManager
{
    readonly object _lock = new object();

    PrinterInfo _target;
    IPrinterTransport _transport;
    PrinterIdentity _identity;
    PrinterState _state = PrinterState.Unknown;
    FailureReason _lastFailureReason = FailureReason.None;
    string _lastFailureDetail;
    int _reconnectAttempts;
    int _consecutiveFailures;
    DateTime? _lastSuccessfulCommunication;
    DateTime? _lastSuccessfulPrint;
    bool _wantConnect;

    public readonly ReliabilityStats Stats = new ReliabilityStats();
    public readonly AdapterMonitor Adapter = new AdapterMonitor();
    readonly ReconnectPolicy _backoff = new ReconnectPolicy();

    public PrinterState State { get { lock (_lock) return _state; } }
    public IPrinterTransport Transport { get { lock (_lock) return _transport; } }
    public PrinterInfo Target { get { lock (_lock) return _target; } }
    public bool WantConnect { get { lock (_lock) return _wantConnect; } }
    public int ReconnectAttempts { get { lock (_lock) return _reconnectAttempts; } }
    public FailureReason LastFailureReason { get { lock (_lock) return _lastFailureReason; } }
    public string LastFailureDetail { get { lock (_lock) return _lastFailureDetail; } }
    public DateTime? LastSuccessfulCommunication { get { lock (_lock) return _lastSuccessfulCommunication; } }

    // true quando a impressora esta pronta pra receber dados agora (equivalente ao antigo
    // "ActivePort != null && ActivePort.IsOpen", generalizado pra qualquer transporte).
    public bool IsReady
    {
        get
        {
            lock (_lock)
            {
                if (_transport == null) return false;
                if (_transport.Kind == TransportKind.WindowsPrinter) return true;
                return _transport.IsOpen;
            }
        }
    }

    // Chamado pelas rotas /connect, /ticket, /print quando o operador/site escolhe uma
    // impressora explicitamente. Espelha o antigo PrintServer.ConnectPrinter() - mesma
    // logica (fecha o que tinha antes, seleciona a nova, tenta abrir), so' atras da
    // maquina de estados + interface de transporte.
    public bool Connect(PrinterInfo pi)
    {
        IPrinterTransport transport = TransportFactory.Create(pi.Type, pi.Id, pi.Detail);
        if (transport == null)
        {
            PrintServer.Log("Tipo de impressora desconhecido: " + pi.Type);
            return false;
        }

        lock (_lock)
        {
            CloseLocked();
            _target = pi;
            _identity = PrinterIdentity.Create(pi.Type, pi.Id, pi.Detail);
            _transport = transport;
            _wantConnect = true;
            _reconnectAttempts = 0;
            _consecutiveFailures = 0;
            _state = PrinterState.Connecting;
        }

        if (transport.Kind == TransportKind.WindowsPrinter)
        {
            // Impressora do Windows: nao ha sessao fisica pra abrir, so' selecionar - o
            // spooler e' quem gerencia a fila de verdade (igual ao comportamento anterior).
            SetState(PrinterState.Idle);
            PrintServer.LogEvent(pi.Name, "CONNECTED", "Transport: " + transport.Kind);
            return true;
        }

        PrintServer.Log(string.Format("Conectando a {0} ({1})...", pi.Name, pi.Id));
        bool ok = TryOpen();
        if (ok && transport.Kind == TransportKind.Bluetooth && !string.IsNullOrEmpty(pi.Detail))
            PrintServer.SaveBtMac(pi.Detail);
        return ok;
    }

    void SetState(PrinterState s)
    {
        lock (_lock) _state = s;
    }

    void CloseLocked()
    {
        _wantConnect = false;
        if (_transport != null) { try { _transport.Close(); } catch { } }
    }

    public void Disconnect()
    {
        lock (_lock) CloseLocked();
        SetState(PrinterState.Disconnected);
    }

    // Fecha so' o transporte (ex: apos erro de escrita no meio de um job), SEM desistir de
    // reconectar (nao mexe em _wantConnect) - o proximo Tick()/TryOpen() reabre sozinho.
    // Equivalente ao antigo "ActivePort.Close(); ActivePort = null;" inline em SendAndChunk.
    public void ForceCloseTransport()
    {
        lock (_lock)
        {
            if (_transport != null) { try { _transport.Close(); } catch { } }
        }
    }

    // Impede duas reconexoes rodando ao mesmo tempo (pedido explicito do usuario) - o
    // watchdog (Tick, a cada 4s) e o retry embutido no envio de um job (SendAndChunk, ver
    // PrintServer.cs) podem chamar TryOpen() quase simultaneamente. So' uma thread por vez
    // efetivamente tenta abrir a conexao - a outra desiste na hora (retorna false) e deixa
    // o proprio loop de quem chamou tentar de novo no proximo ciclo, sem duplicar trabalho
    // nem arriscar duas sequencias de SDP/connect concorrentes no mesmo transporte.
    readonly object _reconnectGate = new object();

    // Espelha o antigo PrintServer.TryOpen() - mesma logica (abre, conta tentativa, loga),
    // agora atraves da interface e com estado/causa de falha classificados.
    public bool TryOpen()
    {
        if (!Monitor.TryEnter(_reconnectGate)) return false;
        try
        {
            return TryOpenLocked();
        }
        finally
        {
            Monitor.Exit(_reconnectGate);
        }
    }

    bool TryOpenLocked()
    {
        IPrinterTransport transport;
        PrinterInfo pi;
        lock (_lock)
        {
            if (!_wantConnect || _target == null || _transport == null) return false;
            transport = _transport;
            pi = _target;
        }

        Stats.ConnectionAttempts++;
        try
        {
            transport.Open();
            lock (_lock)
            {
                _reconnectAttempts = 0;
                _consecutiveFailures = 0;
                _lastSuccessfulCommunication = DateTime.Now;
                _lastFailureReason = FailureReason.None;
                _lastFailureDetail = null;
                _state = PrinterState.Connected;
            }
            Stats.ConnectionsSuccessful++;
            PrintServer.Log(string.Format("Conectado: {0} na {1}.", pi.Name, pi.Id));
            return true;
        }
        catch (Exception ex)
        {
            FailureReason reason = transport.Classify(ex);
            int attempts;
            lock (_lock)
            {
                _reconnectAttempts++;
                _consecutiveFailures++;
                _lastFailureReason = reason;
                _lastFailureDetail = ex.Message;
                _state = _reconnectAttempts == 1 ? PrinterState.Disconnected : PrinterState.Reconnecting;
                attempts = _reconnectAttempts;
            }
            Stats.ConnectionsFailed++;
            PrintServer.Log(string.Format("Falha ao conectar ({0}x, causa: {1}): {2}", attempts, reason, ex.Message));
            return false;
        }
    }

    // Espelha o antigo PrintServer.TryReacquireByMac() - procura a MESMA impressora fisica
    // (mesmo identificador persistente) em qualquer endpoint novo (ex: porta COM mudou),
    // sem exigir reconfiguracao manual. Generalizado pra PrinterIdentity em vez de so' MAC.
    public bool TryReacquireByIdentity()
    {
        PrinterIdentity identity;
        string currentId;
        lock (_lock)
        {
            if (_target == null || _identity == null) return false;
            identity = _identity;
            currentId = _target.Id;
        }
        string mac = !string.IsNullOrEmpty(identity.Mac) ? identity.Mac : PrintServer.LoadBtMac();
        if (string.IsNullOrEmpty(mac)) return false;

        var found = PrintServer.FindPrinters();
        var match = PrintServer.FindByMac(mac, found);
        if (match == null || string.Equals(match.Id, currentId, StringComparison.OrdinalIgnoreCase)) return false;

        PrintServer.Log("Porta da impressora mudou (" + currentId + " -> " + match.Id + ", mesmo MAC " + mac + ") - tentando reconectar na porta nova.");
        PrintServer.LogEvent(match.Name, "PORT_CHANGED", "De: " + currentId, "Para: " + match.Id, "MAC: " + mac);

        lock (_lock)
        {
            _target = match;
            _transport = TransportFactory.Create(match.Type, match.Id, match.Detail);
            _reconnectAttempts = 0;
        }
        PrintServer.SaveConfig(match.Id, PrintServer.LoadCharset());
        Stats.AutoRecoveries++;
        return true;
    }

    // Chamado pelo laco fino do watchdog a cada tick (ver PrintServer.WatchdogLoop). Toda a
    // decisao de "precisa reabrir? respeita o backoff? tenta redescoberta por identidade?
    // consulta o adaptador?" mora aqui - o watchdog so' chama isto.
    public void Tick()
    {
        bool needReopen;
        bool needHealthCheck;
        TransportKind? kind;
        IPrinterTransport transport;
        int consecutiveFailures;
        int attempts;
        lock (_lock)
        {
            transport = _transport;
            if (transport != null && transport.Kind == TransportKind.WindowsPrinter)
            {
                needReopen = false;
                needHealthCheck = false;
            }
            else if (_wantConnect && transport != null)
            {
                needReopen = !transport.IsOpen;
                // So' vale a pena checar saude de verdade se a checagem barata (IsOpen) ja
                // disse que "parece" aberto - se ja sabemos que precisa reabrir, no' pula
                // direto pra reconexao.
                needHealthCheck = !needReopen && transport.Capabilities.CanHealthCheck;
            }
            else
            {
                needReopen = false;
                needHealthCheck = false;
            }
            kind = transport != null ? transport.Kind : (TransportKind?)null;
            consecutiveFailures = _consecutiveFailures;
            attempts = _reconnectAttempts;
        }

        // Deteccao PROATIVA de queda (pedido do usuario apos o teste de 3 ciclos desta
        // sessao: o /status continuava mostrando "connected" mesmo com a impressora
        // fisicamente desligada, ate' alguem tentar imprimir de verdade). IsOpen sozinho
        // e' uma checagem barata (so confirma que o objeto de sessao existe, nao que o
        // link ainda esta vivo) - HealthCheck() e' PASSIVO por contrato (nunca manda byte
        // pra impressora, so consulta o estado que o proprio Windows ja mantem), entao dá
        // pra chamar a cada tick (4s) sem risco pro hardware nem custo real.
        if (needHealthCheck)
        {
            var health = transport.HealthCheck();
            if (!health.Alive)
            {
                PrintServer.LogEvent(TargetName(), "CONNECTION_LOST",
                    "Transport: " + kind,
                    "Reason: " + health.Detail,
                    "Fila preservada, iniciando reconexao.");
                // Marca o transporte como desconectado imediatamente (fecha a sessao
                // velha agora, nao so' na proxima tentativa de Open()) - deixa o estado
                // consistente pra qualquer outra thread que consulte IsReady enquanto o
                // backoff ainda nao terminou.
                ForceCloseTransport();
                needReopen = true;
            }
        }

        if (!needReopen) return;

        SetState(PrinterState.Reconnecting);

        // So' consulta o adaptador Bluetooth quando o transporte atual e' Bluetooth E ja
        // ha sinal de problema (2+ falhas seguidas) - nao gasta WMI a cada tick por nada.
        if (kind == TransportKind.Bluetooth && consecutiveFailures >= 2)
        {
            var adapterStatus = Adapter.Check();
            if (adapterStatus.NeedsReboot)
            {
                SetState(PrinterState.RequiresUserAction);
                lock (_lock) { _lastFailureReason = FailureReason.AdapterNeedsReboot; _lastFailureDetail = adapterStatus.Detail; }
                PrintServer.LogEvent(TargetName(), "RECOVERY_UNAVAILABLE",
                    "Windows restart required",
                    adapterStatus.Detail,
                    "Fila preservada. Recuperacao automatica do adaptador pausada ate o Windows ser reiniciado.");
                // Continua tentando reabrir a PORTA (pode ser que o operador reinicie o
                // Windows sozinho e volte a funcionar) - so' para' de insistir na
                // recuperacao ativa do adaptador em si (nao adianta, ver AdapterMonitor).
            }
            else if (!adapterStatus.Ok)
            {
                SetState(PrinterState.Error);
                lock (_lock) { _lastFailureReason = FailureReason.AdapterError; _lastFailureDetail = adapterStatus.Detail; }
                PrintServer.LogEvent(TargetName(), "ADAPTER_UNHEALTHY",
                    "Device status: " + adapterStatus.Status,
                    "ProblemCode: " + adapterStatus.ProblemCode,
                    "Adapter: " + adapterStatus.AdapterName,
                    adapterStatus.AdapterCount > 1 ? ("Adaptadores presentes: " + adapterStatus.AdapterCount) : null);
                SetState(PrinterState.Recovery);
                bool recovered = Adapter.TryRecover(adapterStatus);
                if (recovered) Stats.AutoRecoveries++;
            }
        }

        int delay = _backoff.DelaySecondsFor(attempts);
        if (delay > 0) Thread.Sleep(delay * 1000);

        if (!TryOpen())
        {
            if (attempts > 0 && attempts % 3 == 0)
            {
                if (TryReacquireByIdentity()) TryOpen();
            }
        }
        else
        {
            PrintServer.LogEvent(TargetName(), "RECONNECTED", "Queue resumed");
        }
    }

    string TargetName()
    {
        lock (_lock) return _target != null ? _target.Name : "?";
    }

    public void NotifyPrintSuccess()
    {
        lock (_lock) _lastSuccessfulPrint = DateTime.Now;
    }

    public void NotifyCommunication()
    {
        lock (_lock) _lastSuccessfulCommunication = DateTime.Now;
    }

    // Snapshot pra /status, /diagnostics e pro painel local.
    public HealthSnapshot Snapshot(int pendingJobs)
    {
        lock (_lock)
        {
            return new HealthSnapshot
            {
                PrinterName = _target != null ? _target.Name : null,
                Transport = _transport != null ? _transport.Kind.ToString() : null,
                State = _state,
                LastFailureReason = _lastFailureReason,
                LastFailureDetail = _lastFailureDetail,
                LastSuccessfulCommunication = _lastSuccessfulCommunication,
                LastSuccessfulPrint = _lastSuccessfulPrint,
                ReconnectAttempts = _reconnectAttempts,
                FailedAttempts = Stats.ConnectionsFailed,
                PendingJobs = pendingJobs,
                HealthPercent = ComputeHealthPercent()
            };
        }
    }

    // Percentual DERIVADO, deliberadamente simples (fracao de conexoes bem-sucedidas quando
    // o estado atual e' saudavel). NAO e' a metrica principal - ver ReliabilityStats (item
    // 16 do pedido do usuario: os contadores brutos sao a fonte da verdade, isto e' so' um
    // resumo visual pro painel).
    int ComputeHealthPercent()
    {
        if (_state == PrinterState.Connected || _state == PrinterState.Idle || _state == PrinterState.Printing)
        {
            if (Stats.ConnectionAttempts == 0) return 100;
            int score = (int)Math.Round(Stats.ReconnectSuccessRate ?? 100);
            return Math.Max(0, Math.Min(100, score));
        }
        if (_state == PrinterState.RequiresUserAction || _state == PrinterState.Offline) return 0;
        return 40; // reconectando/degradado/em recuperacao - nem 0 nem saudavel
    }
}

// Registro de impressoras conhecidas. Hoje so' guarda a impressora ATIVA (mesmo modelo do
// PrintServer atual - uma por vez), mas ja fica no formato certo pra virar multi-impressora
// no futuro sem reescrever ConnectionManager nem PrintServer.cs de novo.
public static class PrinterManager
{
    public static readonly ConnectionManager Current = new ConnectionManager();
}
