# Compilar contra WinRT (Windows Runtime) em C#/.NET Framework sem Visual Studio

Guia de referência — como resolver `error CS0012` / `CS1928` / `CS1929` ao consumir APIs
modernas do Windows (`Windows.Devices.*`, `Windows.Networking.*`, `Windows.Storage.Streams.*`
etc.) num projeto C# compilado direto com `csc.exe` (sem `.csproj`/Visual Studio/MSBuild).

Escrito em 2026-08-15 depois de resolver esse exato problema pra implementar o
`BluetoothRfcommTransport` do TipPrint (`dist/PrintTransport.cs`) — ver
`docs/MONITORAMENTO-RECONEXAO.md` pro contexto do feature. Guardado aqui separado porque o
problema é genérico (qualquer API WinRT), não específico de Bluetooth.

## Sintoma

Código C# usando `using Windows.Devices.Bluetooth;` (ou qualquer namespace `Windows.*`) e
chamando `.AsTask()`/`await` numa `IAsyncOperation<T>` falha ao compilar com:

```
error CS0012: Tipo 'Windows.Foundation.IAsyncOperation<T>' está definido em um assembly
que não é usado como referência. Adicione uma referência ao assembly 'Windows,
Version=255.255.255.255, ..., ContentType=WindowsRuntime'.
error CS1928: '...' não contém uma definição para 'AsTask' ...
```

## O que NÃO resolve (testado, não repetir)

1. **Referenciar os `.winmd` soltos de `C:\Windows\System32\WinMetadata\`** (os do runtime
   instalado) — insuficiente, faltam os "contratos" (`*Contract.winmd`) que definem
   `IAsyncOperationWithProgress<T,P>` etc.
2. **Vendorizar dezenas de `.winmd` de contrato individuais** (ex: de um pacote NuGet tipo
   `Microsoft.Windows.SDK.Contracts` em cache, ou baixados avulsos) e referenciar todos via
   `/reference:` — **compila às vezes, mas de forma extremamente frágil e imprevisível**:
   adicionar um único método trivial no mesmo arquivo `.cs`, ou uma segunda classe no
   mesmo arquivo, pode fazer o MESMO código passar a falhar com `CS0012`. Não é
   confiável o bastante pra produção, mesmo que "funcione hoje".
3. Nenhuma combinação de `using`/nome totalmente qualificado, helper genérico vs. inline,
   ou ordem dos `/reference:` resolveu esse comportamento errático — não é bug de sintaxe
   do seu código, é resolução de tipo incompleta/instável do `csc.exe` com um conjunto de
   `.winmd` fragmentado.

## O que resolve de verdade

Instalar o **Windows SDK** de verdade e referenciar o arquivo **unificado** que ele gera —
não os contratos separados:

```
C:\Program Files (x86)\Windows Kits\10\UnionMetadata\<versão>\Windows.winmd
```

Esse é literalmente o mesmo arquivo que o Visual Studio referencia por baixo dos panos
quando você adiciona "Windows" como referência num projeto .NET Framework clássico (Add
Reference → Windows → Core). Referenciar só ELE (nem precisa dos contratos individuais
junto) resolve a ambiguidade de forma limpa e estável.

### Passo a passo

1. Instalar o SDK (não precisa ser a versão mais nova — qualquer uma recente tem as
   contracts básicas de Bluetooth/Networking/Storage):
   ```powershell
   winget search "Windows SDK"
   winget install --id Microsoft.WindowsSDK.10.0.18362 --silent --accept-package-agreements --accept-source-agreements
   ```
   Isso baixa e instala uns poucos GB (`Windows Kits\10\...`) — não precisa reiniciar,
   nem elevar manualmente (o winget cuida disso). Leva alguns minutos.

2. Localizar o `Windows.winmd` unificado (a pasta de versão dentro de `UnionMetadata` às
   vezes tem uma subpasta chamada `Facade` também com um `Windows.winmd` — **não é esse**,
   use o que fica direto na pasta da versão, ex.:
   `UnionMetadata\10.0.18362.0\Windows.winmd`, não `UnionMetadata\Facade\Windows.WinMD`
   nem `UnionMetadata\10.0.18362.0\Facade\windows.winmd`):
   ```powershell
   Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\UnionMetadata" -Directory |
     Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
     Sort-Object Name -Descending | Select-Object -First 1
   ```

3. Compilar referenciando **só esse arquivo** (mais `System.Runtime.WindowsRuntime.dll` e
   `System.Runtime.dll`, que já vêm com o .NET Framework):
   ```
   csc.exe /reference:System.Runtime.WindowsRuntime.dll
           /reference:"C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll"
           /reference:"C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.18362.0\Windows.winmd"
           SeuArquivo.cs
   ```
   **Não** adicione também `Windows.Foundation.UniversalApiContract.winmd` separado — o
   union já contém tudo, e referenciar os dois juntos dá `CS0433` (tipo duplicado).

4. Código C#: pode usar `using Windows.Devices.Bluetooth;` etc. normalmente, e bloquear
   chamadas assíncronas inline sem `async`/`await` (mantém o resto do código síncrono):
   ```csharp
   var task = AlgumaApiAsync().AsTask();
   task.Wait();
   var resultado = task.Result;
   ```

Ver `dist/build.ps1` no repo pra implementação real disso (localiza a versão do SDK
instalada automaticamente, monta os `/reference:` certos).

## Importante: SDK é só ferramenta de build, não de runtime

O `.exe` resultante roda em qualquer Windows 10/11 comum **sem precisar do SDK
instalado lá** — as APIs de Bluetooth/Networking/Storage já vêm de fábrica no sistema
operacional (são componentes do próprio Windows, não do SDK), e
`System.Runtime.WindowsRuntime.dll` já vem com o .NET Framework. O SDK só entra na
hora de COMPILAR, nesta máquina (ou em qualquer máquina de desenvolvimento) — nunca
precisa ir junto no pacote distribuído pros clientes.

## Diagnóstico rápido: "é esse problema mesmo?"

Se a mensagem de erro citar `IAsyncOperationWithProgress`, `IAsyncActionWithProgress`,
`AsTask` não encontrado, ou "Adicione uma referência ao assembly 'Windows,
Version=255.255.255.255...'" — é isso. Não perca tempo testando variações de sintaxe;
vá direto pro Windows SDK.

## Se a escrita/leitura de stream WinRT falhar especificamente em PowerShell (não em C#)

Problema relacionado, mas diferente: **PowerShell 5.1 não consegue fazer bind dinâmico em
interfaces WinRT sem classe concreta por trás** (`IInputStream`/`IOutputStream` — usados
por `DataReader`/`DataWriter`). Sintomas: `New-Object DataWriter($stream)` falha com "não
é possível converter System.__ComObject", e mesmo `Type.InvokeMember`/reflection direta no
método não resolve. Testado exaustivamente sem solução nesta sessão.

**Isso NÃO afeta métodos que retornam classes concretas** (`BluetoothDevice`,
`StreamSocket`, `RfcommDeviceService` etc. funcionam normalmente em PowerShell via
`Add-Type -AssemblyName System.Runtime.WindowsRuntime` +
`[Tipo, Assembly, ContentType=WindowsRuntime]`, ver `desktop/lib/scripts/bt-scan.ps1`).
Só a leitura/escrita de stream especificamente é o problema.

**Solução**: se só precisa conectar/consultar status, PowerShell resolve. Se precisa
ler/escrever bytes de verdade, compile em C# (com o Windows SDK, ver acima) em vez de
PowerShell.
