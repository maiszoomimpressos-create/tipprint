using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

// ===========================================================================================
// TipPrint - Camada universal de transporte de impressoras.
//
// Objetivo (pedido explicito do usuario, 2026-08-15): o monitoramento/reconexao/recuperacao
// NUNCA deve saber se esta falando com Bluetooth, USB ou impressora do Windows - ele so
// conversa com IPrinterTransport. Cada forma de comunicacao concreta implementa essa
// interface e declara o que sabe fazer via TransportCapabilities.
//
// IMPORTANTE: a logica de abrir porta serial/gravar bytes AQUI e' a mesma que ja existia em
// PrintServer.TryOpen()/SendAndChunk() antes desta mudanca - so foi movida pra tras da
// interface, nao reescrita. Ver ConnectionManager.cs pra quem orquestra isso (maquina de
// estados, backoff, classificacao de causa).
// ===========================================================================================

public enum TransportKind
{
    Bluetooth,
    Usb,
    WindowsPrinter,
    Tcp,
    BluetoothRfcomm
}

// Estado da impressora do ponto de vista do operador/monitoramento - independente do
// transporte. Nomes sugeridos pelo usuario, mantidos como pedido.
public enum PrinterState
{
    Unknown,
    Discovering,
    Available,
    Connecting,
    Connected,
    Printing,
    Idle,
    Disconnected,
    Reconnecting,
    Degraded,
    Error,
    Recovery,
    RequiresUserAction,
    Offline
}

// Causa raiz de uma falha - e' isso que diferencia "impressora desligada" de "porta sumiu"
// de "adaptador com erro" de "Windows precisa reiniciar" (pedido explicito do usuario,
// motivado pela investigacao real desta sessao: mesma mensagem generica escondia causas
// completamente diferentes).
public enum FailureReason
{
    None,
    PrinterUnreachable,   // impressora desligada/fora de alcance/porta nao responde
    PortChanged,          // a porta COM salva nao existe mais (Windows renumerou)
    AdapterError,         // adaptador Bluetooth com erro (driver, desabilitado etc.)
    AdapterNeedsReboot,   // adaptador preso num estado que so' reinicializar o Windows resolve
    Timeout,              // operacao demorou demais (impressora ocupada/lenta)
    Busy,                 // buffer de recepcao da impressora cheio - tentar de novo, nao e' erro real
    Unknown
}

// O que um transporte sabe fazer - o monitoramento consulta isso em vez de checar o tipo.
public class TransportCapabilities
{
    public bool CanDiscover;
    public bool CanReconnect;
    public bool CanHealthCheck;
    public bool CanRecover;
    public bool RequiresWindowsDriver;
    public bool RequiresPairing;
    public bool SupportsDirectConnection;
    public bool SupportsHardwareReset;
    // Transportes seriais (BT/USB) precisam do chunking com protecao de trecho atomico
    // (SendAndChunk). WindowsPrinterTransport nao - o spooler recebe o payload inteiro.
    public bool RequiresChunking;
}

// Resultado de um heartbeat - ver cada implementacao de HealthCheck() pra saber que
// estrategia foi usada (todas PASSIVAS por padrao - nunca mandam byte novo pra impressora).
public class TransportHealth
{
    public bool Alive;
    public string Detail;
    public TimeSpan? SinceLastSuccess;
}

// Identidade persistente da impressora - usada pra reencontrar o MESMO dispositivo fisico
// mesmo quando o identificador de sessao (porta COM) muda. Generaliza o que
// LoadBtMac/FindByMac/SaveBtMac ja faziam so' pra Bluetooth. Prioridade: MAC > Device
// Instance ID > Serial > IP > nome (pedido explicito do usuario, item 11).
public class PrinterIdentity
{
    public string Mac;
    public string DeviceInstanceId;
    public string WindowsPrinterName;
    public string Ip;
    public string Serial;

    public string PreferredKey
    {
        get
        {
            if (!string.IsNullOrEmpty(Mac)) return "mac:" + Mac.ToUpperInvariant();
            if (!string.IsNullOrEmpty(DeviceInstanceId)) return "dev:" + DeviceInstanceId.ToUpperInvariant();
            if (!string.IsNullOrEmpty(Serial)) return "serial:" + Serial;
            if (!string.IsNullOrEmpty(Ip)) return "ip:" + Ip;
            if (!string.IsNullOrEmpty(WindowsPrinterName)) return "win:" + WindowsPrinterName;
            return null;
        }
    }

    // Recebe campos primitivos (nao o tipo PrinterInfo em si) de proposito: PrintTransport.cs
    // compila numa DLL separada (TipPrint.Transport.dll, ver dist/build.ps1) por causa das
    // referencias WinRT que o BluetoothRfcommTransport precisa - depender do tipo
    // PrinterInfo (definido em PrintServer.cs, no .exe) criaria uma referencia circular
    // entre as duas assemblies, o que o .NET nao permite.
    public static PrinterIdentity Create(string type, string id, string detail)
    {
        var identity = new PrinterIdentity();
        if (type == "bluetooth" && !string.IsNullOrEmpty(detail)) identity.Mac = detail.ToUpperInvariant();
        else if (type == "windows") identity.WindowsPrinterName = id;
        // usb/tcp: sem identidade persistente ainda hoje (a descoberta atual nao expoe
        // Device Instance ID/IP separado do Id de sessao) - fica pronto para quando
        // UsbSerialTransport/TcpPrinterTransport ganharem descoberta propria.
        return identity;
    }
}

// Contrato universal que todo meio de comunicacao com impressora implementa. O
// ConnectionManager e o HealthMonitor conversam SOMENTE com isso - nunca com SerialPort,
// WinPrinter ou qualquer API especifica de transporte diretamente.
public interface IPrinterTransport
{
    TransportKind Kind { get; }
    TransportCapabilities Capabilities { get; }
    // Identificador de sessao atual (porta COM, nome da impressora Windows, IP...) -
    // equivalente ao antigo PrinterInfo.Id.
    string EndpointId { get; }
    bool IsOpen { get; }

    // Abre a conexao. Lanca excecao em caso de falha (igual SerialPort.Open() hoje) -
    // quem chama classifica a excecao via Classify().
    void Open();
    void Close();

    // Escreve um pedaco de bytes (usado pelo loop de chunking quando
    // Capabilities.RequiresChunking = true).
    void Write(byte[] data, int offset, int length);

    // Envia o payload inteiro de uma vez (usado quando RequiresChunking = false).
    void SendWhole(byte[] data);

    // Heartbeat seguro - ver contrato: NUNCA deve imprimir, avancar papel, cortar ou abrir
    // gaveta. Cada implementacao documenta a estrategia usada.
    TransportHealth HealthCheck();

    // Traduz uma excecao capturada durante Open/Write numa causa raiz classificada.
    FailureReason Classify(Exception ex);
}

// -------------------------------------------------------------------------------------------
// Bluetooth SPP/COM e USB serial - mesma mecanica (SerialPort sobre uma porta COM), so'
// mudam as capabilities. Extraido literalmente da logica que estava em
// PrintServer.TryOpen()/CloseActiveLocked()/SendAndChunk() antes desta mudanca.
// -------------------------------------------------------------------------------------------
public abstract class SerialPortTransportBase : IPrinterTransport
{
    protected SerialPort Port;
    protected readonly object PortLock = new object();
    // Timestamp da ultima escrita bem-sucedida - usado pelo HealthCheck passivo (nunca
    // manda byte novo so' pra testar, ver decisao 4 do plano).
    protected DateTime? LastSuccessfulWrite;

    public string EndpointId { get; private set; }
    public abstract TransportKind Kind { get; }
    public abstract TransportCapabilities Capabilities { get; }

    protected SerialPortTransportBase(string endpointId)
    {
        EndpointId = endpointId;
    }

    public bool IsOpen
    {
        get { lock (PortLock) return Port != null && Port.IsOpen; }
    }

    public void Open()
    {
        lock (PortLock)
        {
            CloseLocked();
            var sp = new SerialPort(EndpointId, 115200, Parity.None, 8, StopBits.One)
            {
                WriteTimeout = 3000,
                ReadTimeout = 300
            };
            sp.Open();
            Port = sp;
        }
    }

    public void Close()
    {
        lock (PortLock) CloseLocked();
    }

    void CloseLocked()
    {
        if (Port != null)
        {
            try { Port.Close(); } catch { }
            Port = null;
        }
    }

    public void Write(byte[] data, int offset, int length)
    {
        SerialPort sp;
        lock (PortLock) sp = Port;
        if (sp == null || !sp.IsOpen) throw new InvalidOperationException("Porta nao esta aberta.");
        sp.Write(data, offset, length);
        LastSuccessfulWrite = DateTime.Now;
    }

    public void SendWhole(byte[] data)
    {
        Write(data, 0, data.Length);
    }

    // PASSIVO por design: so observa porta aberta + tempo desde a ultima escrita
    // bem-sucedida. Nao manda nenhum comando novo pra impressora (pedido explicito do
    // usuario: "MUITO CUIDADO... nao envie comandos ESC/POS aleatorios durante o
    // monitoramento"). Upgrade futuro possivel e documentado, mas desligado por padrao:
    // o comando ESC/POS "DLE EOT n" (consulta de status em tempo real) e' seguro pela
    // especificacao - nao imprime/avanca/corta/abre gaveta - mas nem toda impressora
    // clone (KP-1025 e afins) confirmadamente responde a ele, entao fica de fora ate
    // ser testado em campo.
    public TransportHealth HealthCheck()
    {
        bool open = IsOpen;
        return new TransportHealth
        {
            Alive = open,
            Detail = open ? "porta aberta" : "porta fechada",
            SinceLastSuccess = LastSuccessfulWrite.HasValue ? (TimeSpan?)(DateTime.Now - LastSuccessfulWrite.Value) : null
        };
    }

    public FailureReason Classify(Exception ex)
    {
        if (ex == null) return FailureReason.Unknown;
        string msg = (ex.Message ?? "").ToLowerInvariant();
        if (ex is UnauthorizedAccessException) return FailureReason.PrinterUnreachable; // porta ocupada por outro processo, ou dispositivo sumiu do barramento
        if (ex is FileNotFoundException) return FailureReason.PortChanged; // a porta COM em si nao existe mais
        if (msg.Contains("nao pode encontrar") || msg.Contains("cannot find") || msg.Contains("does not exist") || msg.Contains("inexistente"))
            return FailureReason.PortChanged;
        if (msg.Contains("tempo") || msg.Contains("timeout") || msg.Contains("exced") || msg.Contains("timed"))
            return FailureReason.Busy;
        return FailureReason.PrinterUnreachable;
    }
}

public class BluetoothSerialTransport : SerialPortTransportBase
{
    public BluetoothSerialTransport(string comPort) : base(comPort) { }
    public override TransportKind Kind { get { return TransportKind.Bluetooth; } }
    public override TransportCapabilities Capabilities
    {
        get
        {
            return new TransportCapabilities
            {
                CanDiscover = true,
                CanReconnect = true,
                CanHealthCheck = true,
                CanRecover = true,
                RequiresWindowsDriver = true,
                RequiresPairing = true,
                SupportsDirectConnection = false,
                SupportsHardwareReset = false,
                RequiresChunking = true
            };
        }
    }
}

public class UsbSerialTransport : SerialPortTransportBase
{
    public UsbSerialTransport(string comPort) : base(comPort) { }
    public override TransportKind Kind { get { return TransportKind.Usb; } }
    public override TransportCapabilities Capabilities
    {
        get
        {
            return new TransportCapabilities
            {
                CanDiscover = true,
                CanReconnect = true,
                CanHealthCheck = true,
                CanRecover = true,
                RequiresWindowsDriver = true,
                RequiresPairing = false,
                SupportsDirectConnection = true,
                SupportsHardwareReset = true, // replugar o cabo e' uma recuperacao valida e segura pro operador
                RequiresChunking = true
            };
        }
    }
}

// Movido de PrintServer.cs em 2026-08-15 (a camada de transporte agora compila numa DLL
// separada, TipPrint.Transport.dll - ver dist/build.ps1 - e WindowsPrinterTransport precisa
// deste P/Invoke). Comportamento identico ao de antes, nada mudou aqui.
class WinPrinter
{
    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOC_INFO_1 di);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
    }

    // So' abre e fecha o handle da impressora - nunca inicia documento nem escreve nada,
    // entao e' seguro pra usar como heartbeat (ver WindowsPrinterTransport.HealthCheck).
    public static bool PrinterExists(string printerName)
    {
        IntPtr hprinter;
        bool ok = OpenPrinter(printerName, out hprinter, IntPtr.Zero);
        if (ok) { try { ClosePrinter(hprinter); } catch { } }
        return ok;
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

// -------------------------------------------------------------------------------------------
// Impressora do Windows (spooler) - encapsula WinPrinter.RawPrint (winspool.drv), que ja
// existia em PrintServer.cs. Nao precisa chunking proprio: o spooler e' quem gerencia a fila
// de verdade, WinPrinter.RawPrint ja pagina internamente em blocos de 8192 bytes (mecanismo
// separado, preservado como estava).
// -------------------------------------------------------------------------------------------
public class WindowsPrinterTransport : IPrinterTransport
{
    public string EndpointId { get; private set; }

    public WindowsPrinterTransport(string printerName)
    {
        EndpointId = printerName;
    }

    public TransportKind Kind { get { return TransportKind.WindowsPrinter; } }

    public TransportCapabilities Capabilities
    {
        get
        {
            return new TransportCapabilities
            {
                CanDiscover = true,
                CanReconnect = false, // nao ha "conexao" de sessao pra cair - e' o spooler do Windows
                CanHealthCheck = true,
                CanRecover = false,
                RequiresWindowsDriver = true,
                RequiresPairing = false,
                SupportsDirectConnection = true,
                SupportsHardwareReset = false,
                RequiresChunking = false
            };
        }
    }

    // "Aberto" pra impressora do Windows e' conceitual - nao ha um handle de sessao pra
    // manter vivo entre jobs (era assim mesmo antes: PrintServer so' checava
    // Active.Type == "windows" e considerava sempre conectado). HealthCheck() e' quem
    // confirma de verdade que o spooler enxerga a impressora.
    public bool IsOpen { get { return true; } }
    public void Open() { }
    public void Close() { }

    public void Write(byte[] data, int offset, int length)
    {
        byte[] chunk = new byte[length];
        Array.Copy(data, offset, chunk, 0, length);
        WinPrinter.RawPrint(EndpointId, chunk);
    }

    public void SendWhole(byte[] data)
    {
        WinPrinter.RawPrint(EndpointId, data);
    }

    // Seguro: so' abre/fecha o handle da impressora (OpenPrinter/ClosePrinter), sem
    // iniciar documento nem escrever nada - nao imprime.
    public TransportHealth HealthCheck()
    {
        bool ok = WinPrinter.PrinterExists(EndpointId);
        return new TransportHealth
        {
            Alive = ok,
            Detail = ok ? "spooler OK" : "impressora nao encontrada no Windows",
            SinceLastSuccess = null
        };
    }

    public FailureReason Classify(Exception ex)
    {
        return FailureReason.PrinterUnreachable;
    }
}

// -------------------------------------------------------------------------------------------
// STUBS - infraestrutura pronta, implementacao real fica pra depois (pedido explicito do
// usuario: "nao implemente RFCOMM necessariamente nesta etapa... primeiro crie a
// infraestrutura universal"). Nao sao instanciados por nenhum fluxo hoje (FindPrinters nao
// produz PrinterInfo do tipo "tcp"/"bluetooth-rfcomm") - existem so' pra o proximo passo nao
// precisar mexer no resto do sistema.
// -------------------------------------------------------------------------------------------
public class TcpPrinterTransport : IPrinterTransport
{
    public string EndpointId { get; private set; }
    private readonly int _port;

    public TcpPrinterTransport(string ip, int port)
    {
        EndpointId = ip;
        _port = port;
    }

    public TransportKind Kind { get { return TransportKind.Tcp; } }

    public TransportCapabilities Capabilities
    {
        get
        {
            return new TransportCapabilities
            {
                CanDiscover = false, // descoberta de impressora de rede fica pra quando isso for implementado (scan de subnet ou IP configurado manualmente)
                CanReconnect = true,
                CanHealthCheck = true,
                CanRecover = true,
                RequiresWindowsDriver = false,
                RequiresPairing = false,
                SupportsDirectConnection = true,
                SupportsHardwareReset = false,
                RequiresChunking = true
            };
        }
    }

    public bool IsOpen { get { return false; } }

    // Quando for implementar: TcpClient.Connect(EndpointId, _port) + NetworkStream.Write,
    // sem pareamento nem redescoberta por MAC (a identidade aqui e' o IP).
    public void Open() { throw new NotImplementedException("TcpPrinterTransport ainda nao implementado - infraestrutura pronta, ver PrintTransport.cs."); }
    public void Close() { }
    public void Write(byte[] data, int offset, int length) { throw new NotImplementedException(); }
    public void SendWhole(byte[] data) { throw new NotImplementedException(); }
    public TransportHealth HealthCheck() { return new TransportHealth { Alive = false, Detail = "transporte nao implementado" }; }
    public FailureReason Classify(Exception ex) { return FailureReason.Unknown; }
}

// -------------------------------------------------------------------------------------------
// Bluetooth Classic RFCOMM direto - conecta na KP-1025 (ou qualquer impressora SPP) pelo MAC,
// via WinRT (Windows.Devices.Bluetooth.Rfcomm + Windows.Networking.Sockets), SEM passar pela
// porta COM/SPP/BTHMODEM do Windows. Investigado e testado extensivamente em 2026-08-15:
//   - SDP ao vivo (Uncached) confirmado: a KP-1025 anuncia UUID 0x1101 (Serial Port Profile).
//   - RFCOMM direto conecta de forma confiavel (dezenas de conexoes de teste bem-sucedidas,
//     inclusive no exato momento em que COM3/COM4 reportavam "dispositivo nao disponivel").
//   - 61 minutos de conexao continua sem nenhuma queda espontanea.
//   - 6/6 power-cycles (desligar/ligar a impressora) reconectaram - nenhuma vez de primeira,
//     entre 1 e 4 tentativas, 1 a 30s - por isso o RETRY/BACKOFF fica por conta de quem chama
//     (ConnectionManager), NAO existe loop de retry aqui dentro. Erro transitorio comum logo
//     apos o power-cycle: WinRT devolve uma excecao ("ponteiro invalido"/"valor nao pode ser
//     nulo") que se resolve sozinha em 1-3 tentativas.
//   - Winsock cru (P/Invoke AF_BTH) NAO funciona nesta maquina (WSAEINVAL/WSAEADDRNOTAVAIL em
//     8 variacoes testadas) - por isso o transporte usa WinRT, nao sockets crus.
//
// Compilar contra Windows.Devices.Bluetooth.Rfcomm/Windows.Networking.Sockets exige o
// Windows SDK instalado (pro csc.exe resolver IAsyncOperation/AsTask etc. via o arquivo
// unificado Windows Kits\10\UnionMetadata\<versao>\Windows.winmd - ver dist/build.ps1).
// Achado nesta sessao: referenciar dezenas de arquivos .winmd separados (contratos
// individuais, sem o SDK) faz o csc.exe falhar de forma imprevisivel (erro CS0012,
// sensivel a mudancas triviais no codigo) - o arquivo UNIFICADO do SDK resolve isso de
// forma limpa e estavel, exatamente como o Visual Studio faz por baixo dos panos.
// -------------------------------------------------------------------------------------------
public class BluetoothRfcommTransport : IPrinterTransport
{
    public string EndpointId { get; private set; } // MAC, formato "AABBCCDDEEFF" (sem separador)

    BluetoothDevice _device;
    StreamSocket _socket;
    DataWriter _writer;
    readonly object _lock = new object();
    DateTime? _lastSuccessfulWrite;

    public BluetoothRfcommTransport(string mac)
    {
        EndpointId = (mac ?? "").Replace(":", "").Replace("-", "").ToUpperInvariant();
    }

    public TransportKind Kind { get { return TransportKind.BluetoothRfcomm; } }

    public TransportCapabilities Capabilities
    {
        get
        {
            return new TransportCapabilities
            {
                CanDiscover = true,
                CanReconnect = true,
                CanHealthCheck = true,
                CanRecover = true,
                RequiresWindowsDriver = true, // ainda depende da pilha/adaptador Bluetooth do Windows, so' pula a camada COM/SPP/BTHMODEM
                RequiresPairing = true,
                SupportsDirectConnection = true,
                SupportsHardwareReset = false,
                RequiresChunking = true
            };
        }
    }

    public bool IsOpen { get { lock (_lock) return _socket != null; } }

    // Cada operacao assincrona WinRT e' bloqueada inline (.AsTask().Wait()/.Result) - sem
    // async/await (o resto do PrintServer e' 100% sincrono/threaded, mantem o mesmo estilo).
    public void Open()
    {
        lock (_lock)
        {
            CloseLocked();

            ulong addr;
            if (!ulong.TryParse(EndpointId, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out addr))
                throw new Exception("MAC invalido: " + EndpointId);

            // Redescobre/valida o dispositivo a cada tentativa - nunca reaproveita um handle
            // antigo (pode estar obsoleto depois de um power-cycle).
            var deviceTask = BluetoothDevice.FromBluetoothAddressAsync(addr).AsTask();
            deviceTask.Wait();
            var device = deviceTask.Result;
            if (device == null) throw new Exception("Dispositivo Bluetooth nao encontrado (fora de alcance ou desligado).");

            // SDP AO VIVO (Uncached) - nunca presume o canal/servico, confirma de verdade
            // toda vez que conecta (igual aos testes desta sessao).
            var svcTask = device.GetRfcommServicesForIdAsync(RfcommServiceId.SerialPort, BluetoothCacheMode.Uncached).AsTask();
            svcTask.Wait();
            var svcResult = svcTask.Result;
            if (svcResult.Services.Count == 0)
                throw new Exception("Servico Serial Port Profile (0x1101) nao encontrado via SDP (Status=" + svcResult.Error + ").");
            var svc = svcResult.Services[0];

            var socket = new StreamSocket();
            var connectTask = socket.ConnectAsync(svc.ConnectionHostName, svc.ConnectionServiceName).AsTask();
            connectTask.Wait();

            _device = device;
            _socket = socket;
            _writer = new DataWriter(socket.OutputStream);
        }
    }

    public void Close()
    {
        lock (_lock) CloseLocked();
    }

    void CloseLocked()
    {
        if (_writer != null) { try { _writer.DetachStream(); } catch { } try { _writer.Dispose(); } catch { } _writer = null; }
        if (_socket != null) { try { _socket.Dispose(); } catch { } _socket = null; }
        _device = null;
    }

    public void Write(byte[] data, int offset, int length)
    {
        DataWriter writer;
        lock (_lock)
        {
            if (_writer == null || _socket == null) throw new InvalidOperationException("RFCOMM nao esta conectado.");
            writer = _writer;
        }
        byte[] chunk = data;
        if (offset != 0 || length != data.Length)
        {
            chunk = new byte[length];
            Array.Copy(data, offset, chunk, 0, length);
        }
        writer.WriteBytes(chunk);
        var storeTask = writer.StoreAsync().AsTask();
        storeTask.Wait();
        lock (_lock) _lastSuccessfulWrite = DateTime.Now;
    }

    public void SendWhole(byte[] data)
    {
        Write(data, 0, data.Length);
    }

    // PASSIVO por design (mesma decisao dos outros transportes seriais): so' consulta
    // BluetoothDevice.ConnectionStatus (propriedade do SO, nao gera trafego no link RFCOMM)
    // - nunca escreve nada novo pra impressora so' pra testar.
    public TransportHealth HealthCheck()
    {
        bool alive;
        string detail;
        BluetoothDevice device;
        lock (_lock) device = _device;
        if (device == null)
        {
            alive = false;
            detail = "nao conectado";
        }
        else
        {
            try
            {
                var status = device.ConnectionStatus;
                alive = status == BluetoothConnectionStatus.Connected;
                detail = "ConnectionStatus=" + status;
            }
            catch (Exception ex)
            {
                alive = false;
                detail = "erro ao consultar status: " + ex.Message;
            }
        }
        DateTime? last;
        lock (_lock) last = _lastSuccessfulWrite;
        return new TransportHealth
        {
            Alive = alive,
            Detail = detail,
            SinceLastSuccess = last.HasValue ? (TimeSpan?)(DateTime.Now - last.Value) : null
        };
    }

    // Classificacao informada diretamente pelos testes desta sessao: logo apos um
    // power-cycle da impressora, o Windows recusa a conexao por alguns segundos com uma
    // excecao transitoria (mensagem tipo "ponteiro invalido"/"valor nao pode ser nulo")
    // antes de aceitar de novo - trata como Busy (motivo pra tentar de novo em breve), nao
    // como "impressora inalcancavel" definitivo.
    public FailureReason Classify(Exception ex)
    {
        if (ex == null) return FailureReason.Unknown;
        string msg = (ex.Message ?? "").ToLowerInvariant();
        if (msg.Contains("ponteiro") || msg.Contains("pointer") || msg.Contains("nao pode ser nulo") || msg.Contains("null"))
            return FailureReason.Busy;
        if (msg.Contains("nao encontrado") || msg.Contains("not found") || msg.Contains("fora de alcance") || msg.Contains("desligad"))
            return FailureReason.PrinterUnreachable;
        if (msg.Contains("sdp") || msg.Contains("serial port profile"))
            return FailureReason.PrinterUnreachable;
        if (msg.Contains("timeout") || msg.Contains("tempo"))
            return FailureReason.Timeout;
        return FailureReason.PrinterUnreachable;
    }
}

// Fabrica: unico lugar que traduz PrinterInfo.Type (string, formato de descoberta atual) pro
// IPrinterTransport correspondente. Se um novo Type aparecer no futuro (ex: "tcp"), so' esse
// metodo muda - nada mais no sistema precisa saber.
public static class TransportFactory
{
    // Recebe campos primitivos, nao PrinterInfo - ver comentario em PrinterIdentity.Create
    // sobre por que (DLL separada, referencia circular).
    public static IPrinterTransport Create(string type, string id, string detail)
    {
        if (type == null) return null;
        switch (type)
        {
            // RFCOMM direto (2026-08-15) - substitui o caminho por COM/BTHMODEM como
            // transporte padrao pra impressoras Bluetooth, por pedido explicito do usuario
            // apos os testes desta sessao (COM3/COM4 mostraram "dispositivo nao disponivel"
            // enquanto RFCOMM direto conectou/reconectou/ESCREVEU de forma confiavel -
            // validado com escrita real na KP-1025, apos instalar o Windows SDK). Precisa do
            // MAC (detail) - se por algum motivo a descoberta nao trouxer o MAC, cai pro
            // caminho antigo via porta COM (BluetoothSerialTransport, ainda no codigo, nao
            // removido) em vez de falhar.
            case "bluetooth":
                if (!string.IsNullOrEmpty(detail)) return new BluetoothRfcommTransport(detail);
                return new BluetoothSerialTransport(id);
            case "usb": return new UsbSerialTransport(id);
            case "windows": return new WindowsPrinterTransport(id);
            default: return null;
        }
    }
}
