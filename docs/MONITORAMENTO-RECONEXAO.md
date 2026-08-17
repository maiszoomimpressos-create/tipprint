# Camada universal de monitoramento/reconexão/recuperação de impressoras

Implementado em 2026-08-15 (ver plano completo salvo na sessão). Objetivo: o
`PrintServer` (Agent) percebe quando uma impressora ou seu meio de comunicação
apresenta problema, tenta recuperar automaticamente e, quando não consegue,
avisa claramente — sem hardcode de Bluetooth/USB/Windows espalhado pelo código.

## Arquivos

| Arquivo | O que tem |
|---|---|
| `dist/PrintServer.cs` | Entry point, HTTP/TCP, fila, ESC/POS — **preservado**, só troca chamadas diretas a `SerialPort`/campos soltos por `PrinterManager.Current`. |
| `dist/PrintTransport.cs` | `IPrinterTransport`, `TransportCapabilities`, `PrinterState`, `FailureReason`, `PrinterIdentity`, os transportes concretos e os stubs de TCP/RFCOMM. |
| `dist/ConnectionManager.cs` | `ConnectionManager` (máquina de estados, backoff, stats), `PrinterManager` (registro — hoje 1 impressora ativa). |
| `dist/AdapterMonitor.cs` | Diagnóstico dinâmico do adaptador Bluetooth (WMI) + escalada de recuperação. |
| `desktop/lib/scripts/bt-radio-reset.ps1` | Toggle de rádio Bluetooth via WinRT, extraído de `bt-repair.ps1`, reaproveitado pelo `AdapterMonitor`. |

## Arquitetura

```
PrintServer (rotas HTTP, fila, ESC/POS)
   -> PrinterManager.Current (ConnectionManager)
        -> IPrinterTransport
             BluetoothSerialTransport | UsbSerialTransport | WindowsPrinterTransport
             TcpPrinterTransport (stub) | BluetoothRfcommTransport (stub)
        -> AdapterMonitor (só quando Kind == Bluetooth)
```

Nenhuma rota, nem `SendAndChunk`, nem o watchdog fala com `SerialPort`/`WinPrinter`
diretamente mais — tudo passa por `IPrinterTransport`. Quem decide "o que da pra
tentar" é `TransportCapabilities` (`CanReconnect`, `CanHealthCheck`, `RequiresPairing`,
`RequiresChunking` etc.), não um `if (tipo == "bluetooth")`.

## Estados (`PrinterState`)

`Unknown, Discovering, Available, Connecting, Connected, Printing, Idle,
Disconnected, Reconnecting, Degraded, Error, Recovery, RequiresUserAction, Offline`

Cada falha carrega um `FailureReason`: `PrinterUnreachable` (impressora desligada/fora
de alcance), `PortChanged` (porta COM sumiu, Windows renumerou), `AdapterError`
(adaptador com erro), `AdapterNeedsReboot` (só reiniciar o Windows resolve), `Timeout`,
`Busy` (buffer cheio, não é erro real).

## Fluxo de conexão

1. `/connect`, `/ticket`, `/print` chamam `PrinterManager.Current.Connect(printerInfo)`.
2. `ConnectionManager.Connect` cria o transporte certo via `TransportFactory.Create`
   (olha `PrinterInfo.Type`), fecha o que tinha antes, tenta abrir.
3. Se Bluetooth e abriu, salva o MAC (`SaveBtMac`) — igual antes.

## Fluxo de reconexão

`WatchdogLoop` (a cada 4s, como antes) só chama `PrinterManager.Current.Tick()`.
Dentro do `Tick()`:

1. Se a porta não está aberta e devia estar (`WantConnect`), marca `Reconnecting`.
2. Se é Bluetooth e já tem 2+ falhas seguidas, consulta o `AdapterMonitor` (só aí —
   não em todo tick, pra não gastar WMI à toa).
3. Aplica o backoff (`ReconnectPolicy`: 0s, 1s, 2s, 5s, 10s, 20s, 30s, 30s...).
4. Tenta reabrir (`TryOpen`). A cada 3 tentativas falhas, tenta redescobrir a mesma
   impressora pelo identificador persistente (`TryReacquireByIdentity` — MAC hoje,
   pronto pra Device Instance ID/IP no futuro).

## Fluxo de recuperação do adaptador Bluetooth

`AdapterMonitor.Check()` usa WMI (`Win32_PnPEntity`, `PNPClass='Bluetooth'`,
filtrando por nome conter "Adapter") — **descobre o adaptador dinamicamente, nunca
por Device Instance ID fixo**. Se tiver mais de um adaptador presente, reporta o
pior estado.

Escalada, deliberadamente limitada (ver decisão abaixo):
1. Redescoberta por identidade (nível `ConnectionManager`, já citado acima).
2. Toggle de rádio via WinRT (`bt-radio-reset.ps1`, sem exigir elevação/UAC) —
   cooldown de 2 min entre tentativas.
3. Se persistir → `RequiresUserAction`, para de insistir na recuperação do
   adaptador (mas continua tentando reabrir a porta, caso o usuário reinicie
   sozinho). Fila **preservada**.

**Decisão de segurança importante**: esta camada **nunca** chama
`Disable-PnpDevice`/`Enable-PnpDevice`/`pnputil` automaticamente. Foi
investigado nesta mesma sessão (2026-08-15) que rodar `Disable-PnpDevice` num
adaptador com o driver travado pode ficar preso em "aguardando reinicialização
do Windows" — ou seja, a authorized-mas-manual "cura" pode piorar o problema.
Só o toggle de rádio (que não mexe no estado habilitado/desabilitado do
dispositivo) é automático.

## BluetoothRfcommTransport (RFCOMM direto, sem COM3/COM4) — FUNCIONAL

Implementado em 2026-08-15 depois de uma investigação extensa confirmando que RFCOMM
direto (bypassando COM/BTHMODEM) conecta e reconecta de forma muito mais confiável que
a porta COM pra KP-1025 (SDP ao vivo confirmado, 61min sem queda, 6/6 power-cycles
reconectados). **É o transporte padrão pra impressoras Bluetooth agora**
(`TransportFactory.Create` usa `BluetoothRfcommTransport` quando o MAC está disponível;
`BluetoothSerialTransport`/COM continua no código como fallback se por algum motivo o MAC
não vier da descoberta).

Implementação: WinRT nativo (`Windows.Devices.Bluetooth.Rfcomm` +
`Windows.Networking.Sockets`), 100% em C# compilado, dentro do mesmo `PrintServer.exe` -
sem subprocesso, sem PowerShell. Cada chamada assíncrona é bloqueada inline
(`.AsTask().Wait()`), sem `async`/`await` (mantém o estilo síncrono do resto do arquivo).

**Como foi destravado** (durante a implementação, `Write()` não funcionava - ver histórico
de tentativas abaixo): compilar contra `Windows.Devices.Bluetooth.Rfcomm`/
`Windows.Networking.Sockets` exige os metadados de contrato do Windows Runtime, e esta
máquina não tinha o Windows SDK instalado. Tentativas de vendorizar `.winmd` de contrato
individuais (~94 arquivos, via um pacote NuGet em cache) esbarraram num bug muito
frágil/imprevisível do `csc.exe` (adicionar um único método trivial no mesmo arquivo
mudava entre compilar e falhar com `CS0012`). **A correção real foi instalar o Windows
SDK de verdade** (`winget install Microsoft.WindowsSDK.10.0.18362`) e referenciar o
arquivo **unificado** que ele gera —
`Windows Kits\10\UnionMetadata\<versão>\Windows.winmd` — em vez dos arquivos de contrato
separados. Esse é o mesmo arquivo que o Visual Studio referencia por baixo dos panos
quando você adiciona "Windows" como referência num projeto .NET Framework; resolveu o bug
de forma limpa e estável. Ver `dist/build.ps1` (localiza a versão instalada
automaticamente).

**Importante sobre distribuição**: o Windows SDK só é necessário nesta máquina, na hora de
compilar. O `PrintServer.exe` resultante roda em qualquer Windows 10/11 comum sem precisar
do SDK lá - as APIs de Bluetooth já vêm de fábrica no sistema operacional, e
`System.Runtime.WindowsRuntime.dll` já vem com o .NET Framework.

**Testado de ponta a ponta contra a KP-1025 de verdade** (não só a conexão - a escrita
também): `Open()` conecta, `HealthCheck()` confirma `ConnectionStatus=Connected`,
`Write()` entrega bytes reais (confirmado com uma consulta de status ESC/POS que não
imprime), um ticket real foi impresso via `/ticket` (`printedOk` incrementou, sem erro),
dedupe por `requestId` confirmado (reenvio virou `duplicate:true`, sem imprimir de novo),
e o teste de estresse embutido (`/diagnostics/stress-test`) rodou 3 ciclos de
conectar→imprimir→desconectar→reconectar com 100% de sucesso, todos via RFCOMM direto.

## Detecção de "Windows precisa reiniciar"

Reaproveita a técnica validada na investigação real desta sessão: o canal de
evento `Microsoft-Windows-Kernel-PnP/Configuration` registra o evento **1065**
("requer que o sistema seja reinicializado") quando uma operação no
dispositivo fica pendente. `AdapterMonitor.CheckNeedsReboot` só dispara essa
consulta (via `Get-WinEvent`, mais pesada) quando o WMI já mostrou 2+ checagens
seguidas com problema — nunca no caminho quente.

## Job em estado indeterminado

`SendAndChunk` já retomava do `offset` onde parou (não reenviava do zero) —
isso foi preservado. O que mudou: `PrintJob.State` (`Queued → Sending →
Completed`, ou `SentIndeterminate`/`Failed` se abandonado):

- `SentIndeterminate`: `offset > 0` quando desistiu — **parte** dos bytes já
  tinha sido entregue ao SO, não sabemos se a impressora recebeu tudo. **Nunca
  reenviado automaticamente.**
- `Failed`: `offset == 0` — nada saiu daqui, esse sim seria seguro pro
  chamador reenviar (e a janela de dedupe por `requestId`, 25s, já cobre
  reenvio rápido).

## API (só aditivo)

- `/status`: ganhou `state`, `lastFailureReason`, blocos `health` e
  `reliability` — nenhum campo antigo mudou.
- `GET /diagnostics` (novo): impressora + adaptador (quando Bluetooth) +
  confiabilidade, formato pra painel.
- `POST /diagnostics/stress-test {cycles: N}` (novo, N ≤ 50): conecta → imprime
  teste → desconecta **nosso próprio transporte** (nunca a impressora física)
  → reconecta, repete. Recusa rodar se já há impressão real em andamento.

## Como estender no futuro

- **USB dedicado**: `UsbSerialTransport` já existe (mesma mecânica de
  `BluetoothSerialTransport`) — falta dar a ele uma `PrinterIdentity` própria
  (Device Instance ID) quando `FindPrinters` passar a expor isso pra USB.
- **TCP/IP**: `TcpPrinterTransport` (stub em `PrintTransport.cs`) — implementar
  `Open`/`Write` com `TcpClient`/`NetworkStream`; `FindPrinters` precisa ganhar
  descoberta de rede ou IP configurado manualmente; `TransportFactory.Create`
  ganha um `case "tcp"`.
- **Bluetooth RFCOMM direto**: `BluetoothRfcommTransport` (stub) — RFCOMM via
  P/Invoke Winsock (`AF_BTH`), sem depender da porta COM/SPP do Windows (evita
  a fonte da maioria dos problemas de reconexão vistos até hoje).

## Teste de estresse

```
POST /diagnostics/stress-test
Content-Type: application/json

{ "cycles": 20 }
```

Resposta: `{ ok, cycles, successfulReconnects, failedReconnects, successRate,
printFailures }`. Não desliga a impressora fisicamente — só fecha/reabre o
transporte do lado do PrintServer.

## Verificação feita nesta sessão

- Compilação real via `csc.exe` (mesmo toolchain do `.exe` de produção),
  `dist/PrintServer.exe` **não recompilado/tocado**.
- Smoke test de leitura (`FindPrinters`, `AdapterMonitor.Check`,
  `TransportFactory`, `ConnectionManager`) rodado contra o estado real desta
  máquina, sem abrir porta/conectar de verdade (não disputa com a instância de
  produção já rodando). Confirmou detecção dinâmica correta do
  `Generic Bluetooth Adapter` (Código 22, desabilitado) — o mesmo problema
  identificado na investigação manual mais cedo nesta sessão.
- **Não testado ainda**: fluxo completo de reconexão com a KP-1025 física
  (desligar/religar), teste de estresse de verdade, e o adaptador TP-Link
  reabilitado (ainda desabilitado, ver conversa anterior — decisão de
  reabilitar é separada desta mudança).
