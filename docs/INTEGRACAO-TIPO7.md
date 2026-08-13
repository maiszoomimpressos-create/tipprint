# TipPrint PrintServer — Guia de Integração para o tipo7.com

> Documento de referência para o desenvolvedor do tipo7.com.
> Este arquivo descreve o sistema de impressão térmica via PC já construído e testado,
> e o que o site precisa fazer para integrá-lo.

---

## 1. Visão geral (o que é)

O **TipPrint PrintServer** é um aplicativo para **Windows** que roda no PC do usuário
(portaria, estacionamento, bilheteria) e transforma o PC em uma "impressora de rede local".

O site do tipo7.com, aberto no navegador do mesmo PC, chama o PrintServer via
`http://localhost:8080` (JavaScript `fetch`) e ele imprime ingressos/cupons na
impressora térmica conectada — **Bluetooth ou USB**.

Fluxo:

```
[tipo7.com (https)]  →  fetch  →  http://localhost:8080  →  PrintServer  →  Impressora
        (navegador do PC do usuário)                          (processo Windows)    (BT/USB)
```

**Já validado fisicamente** com a impressora Kanup KP-1025 (58mm, Bluetooth):
- Impressão real via Bluetooth do PC
- Chamada a partir de uma página **https** (não há bloqueio de navegador para localhost)
- Auto-reconexão automática quando a impressora desconecta
- Descoberta e identificação da impressora (modelo, tipo, largura)

---

## 2. O que o usuário precisa fazer (uma vez, na instalação)

1. Baixar o pacote `TipPrintPrintServer.zip` (contém `PrintServer.exe`, `Instalar.bat`, `Desinstalar.bat`)
2. Rodar `Instalar.bat` (duplo clique):
   - Copia o programa para `%LOCALAPPDATA%\TipPrint\`
   - Ativa o início automático com o Windows
   - Desativa a economia de energia USB (evita desconexões)
   - Abre as configurações de Bluetooth do Windows
3. Parear a impressora no Windows (PIN padrão: **0000**)
4. Abrir `http://localhost:8080` no navegador e clicar na impressora detectada

A partir daí o PrintServer **lembra a impressora e reconecta sozinho** a cada ligada do PC.

### Onde hospedar o download

O site deve disponibilizar o ZIP. Sugestões de URL no tipo7.com:
`https://tipo7.com/downloads/impressao/TipPrintPrintServer.zip`

O conteúdo do ZIP é a pasta `dist/` deste projeto (exe + 2 .bat + opcionalmente o
código-fonte `PrintServer.cs`). O `Instalar.bat` deve ser executado pelo usuário.

---

## 3. API do PrintServer (base `http://localhost:8080`)

### 3.1 `GET /printers` — listar impressoras detectadas

O site usa este endpoint para **descobrir o modelo/tipo da impressora do usuário**.

```json
{
  "ok": true,
  "printers": [
    {
      "Type": "bluetooth",            // "bluetooth" | "usb" | "windows"
      "Name": "Bluetooth (porta COM4)",
      "Id": "COM4",                   // identificador a usar em /connect, /print, /ticket
      "Detail": "KP-1025 (86:67:7A:B6:30:57)",
      "Status": "conectado",          // "conectado" | "disponivel"
      "Width": "58"                   // largura estimada: "58" ou "80" (mm)
    },
    {
      "Type": "windows",
      "Name": "EPSON TM-T20",
      "Id": "EPSON TM-T20",
      "Detail": "Impressora do Windows (driver: ...)",
      "Status": "disponivel",
      "Width": "80"
    }
  ]
}
```

Observações:
- `bluetooth` → impressora pareada no Windows (SPP), via porta COM virtual.
- `usb` → impressora USB exposta como porta serial (CDC).
- `windows` → impressora com driver do Windows (ex.: 80mm com driver USB). Envio por RAW.
- `Width` é uma estimativa por heurística do nome; o site pode sobrescrever em suas configurações se souber a largura real.

### 3.2 `POST /connect` — selecionar a impressora

```json
// body
{ "printer": "COM4" }

// resposta
{ "ok": true }
```

- Salva a escolha em disco (`%LOCALAPPDATA%\TipPrint\config.txt`) → reutilizada na auto-reconexão.
- `Status` passa a ser `conectado` em `/printers`.

### 3.3 `GET /status` — estado atual

```json
{
  "ok": true,
  "connected": true,
  "printer": "KP-1025",
  "port": "COM4",
  "type": "bluetooth",
  "width": "58",
  "tries": 0,
  "charset": "ascii",
  "profile": "fraca",     // "fraca" | "seguro" | "forte" (ritmo de impressao automatico)
  "queue": 0,             // trabalhos esperando na fila
  "printing": 0           // trabalhos sendo impressos agora (0 ou 1)
}
```

### 3.7 `GET /capabilities` — capacidade/ritmo da impressora conectada

Retorna como o PrintServer decidiu **imprimir nesta impressora**, sem perguntar nada ao usuário:

```json
{
  "ok": true,
  "model": "KP-1025",
  "type": "bluetooth",
  "width": "58",
  "profile": "fraca",
  "chunkBytes": 512,        // impressao enviada em blocos deste tamanho
  "pauseMs": 300,           // pausa entre blocos
  "pauseBetweenJobsMs": 900,// pausa entre trabalhos (ex.: entre um ingresso e o proximo)
  "qrSupport": true,
  "charset": "ascii"
}
```

Como o perfil é escolhido (100% automático, sem interação do usuário):
- **Tabela de modelos**: nomes conhecidos determinam o perfil — impressoras de loja
  (Bematech, Elgin, Epson, TM-T20, 80mm...) → `forte`; portáteis 58mm comuns
  (KP-1025, KP-109, POS58, 58mm...) → `fraca`.
- **Perfil seguro universal**: modelo desconhecido → `seguro` (nunca trava nenhuma
  impressora; só imprime um pouco mais devagar).
- O modelo é lido do Windows (nome real da impressora via registro Bluetooth),
  então a detecção é automática e sem pedir confirmação ao usuário.

**Por que isso importa:** impressoras 58mm baratas (KP-1025) têm buffer pequeno e
travar quando recebem muitos ingressos enfileirados sem pausa. O PrintServer agora
**fila todas as impressões** e as envia em blocos com pausas conforme o perfil.
Na prática: chegaram 4 ingressos juntos → imprimem um a um, sem travar, sem
pergunta ao usuário. Impressoras de loja continuam imprimindo rápido.

### 3.4 `POST /print` — impressão simples

```json
// body
{
  "mode": "text",            // "text" | "raw" | "escpos"
  "data": "Cupom do sistema\nLinha 2\nValor: R$ 10,00",
  "charset": "ascii",        // opcional: "ascii" (padrao) | "cp850"
  "printer": "COM4"          // opcional: troca de impressora na hora
}

// respostas
{ "ok": true, "bytes": 159 }                    // sucesso
{ "ok": false, "error": "..." }                 // erro
```

- `text`: converte texto em ESC/POS (com normalização de acentos se `ascii`).
- `raw`: `data` é base64 de bytes ESC/POS prontos.
- `escpos`: imprime cupom de teste/título (só para testes).

### 3.5 `POST /ticket` — ingresso com QR Code (uso principal da Tipo7)

```json
// body
{
  "printer": "COM4",            // opcional
  "charset": "ascii",           // opcional
  "title": "INGRESSO TIPO7",
  "event": "FESTIVAL ROCK 2026",
  "date": "20/12/2026 - 20:00",
  "local": "Arena Sao Paulo",
  "sector": "Pista Premium",
  "buyer": "FULANO DA SILVA",
  "code": "TP7-2026-000123",
  "price": "R$ 180,00",
  "qr": "TIPO7|2026-000123|PISTA-PREMIUM"   // conteúdo do QR Code (ex.: código de validação)
}

// resposta
{ "ok": true, "bytes": 369 }
```

O PrintServer monta o cupom 58mm com título centralizado, dados do evento,
bloco do comprador e **QR Code gerado pelo próprio servidor (ESC/POS `GS ( k`)**
— sem precisar gerar imagem no site.

### 3.6 `POST /config` — tabela de caracteres

```json
{ "charset": "ascii" }   // "ascii" (padrao) | "cp850"
```

- `ascii`: acentos são normalizados (não, voce) — funciona em impressoras portáteis 58mm (KP-1025 etc).
- `cp850`: acentos reais (não, você) — para impressoras de loja (Bematech, Elgin, Epson).

### 3.8 `GET /licenca` — modo de uso atual (pronto para cobrança futura)

Hoje o PrintServer roda no **modo livre** (nada é cobrado nem bloqueado). O mecanismo
de restrição **já está implementado** e fica desligado:

```json
{ "ok": true, "modo": "livre", "licenca": "gratis", "origens": [] }
```

Quando vocês decidirem começar a cobrar terceiros, basta **adicionar a 3ª linha** do
`config.txt` (`%LOCALAPPDATA%\TipPrint\config.txt`) com as origens autorizadas:

```
COM4
ascii
tipo7.com, *.tipo7.com
```

A partir daí o modo muda para `restrita`/`licenciada` e o servidor passa a **bloquear
com HTTP 403** qualquer impressão (`/ticket` e `/print`) vinda de origem não listada:

```json
{ "ok": false, "licenca": "bloqueada", "error": "Uso nao autorizado: ..." }
```

Regras:
- `tipo7.com` autoriza o domínio exato; `*.tipo7.com` autoriza subdomínios.
- Chamadas sem cabeçalho `Origin` (apps/scripts diretos) são **bloqueadas** em modo restrito.
- O painel de cobrança futuro pode gerar essa linha de config remotamente (ex.: distribuir
  uma versão do ZIP com o config pré-preenchido por licença paga) ou validar licença online
  contra o tipo7.com — a estrutura de `/licenca` já foi feita para esse encaixe.

---

## 4. Integração com o tipo7.com — JÁ IMPLEMENTADA no site

O site (`TIPO7/web`) já está integrado com o PrintServer:

- `src/lib/printServerClient.ts` — cliente do PrintServer (status + `/ticket` + teste)
- `src/components/PrintServerPanel.tsx` — painel no site que detecta se o app está rodando
  (`/status`), mostra o estado e imprime cupom de teste
- `src/app/bilheteria/[eventoId]/BilheteiroClient.tsx` — na venda no caixa, imprime os
  ingressos automaticamente via `/ticket` (formato "PrintServer")
- `src/app/estacionamento/[eventoId]/AtendenteClient.tsx` — mesma integração no estacionamento
- `src/lib/tipprintPrint.ts` — alternativa de impressão via app Android (TipPrint) no celular

Fluxo real (já testado com a KP-1025): venda confirmada no caixa → o site chama
`POST /ticket` para cada ingresso → o PrintServer fila, monta o cupom com QR e imprime.

### 4.1 Onde fica o download
- [ ] Hospedar `TipPrintPrintServer.zip` em uma URL pública do tipo7.com
- [ ] Criar página/área "Imprimir ingressos" com o passo a passo de instalação
- [ ] (Opcional) Detectar se o PrintServer está rodando no PC do usuário via `GET /status` com timeout curto (ex.: 1s); se não responder, mostrar instruções de instalação

### 4.2 Associar o usuário à impressora (armazenar)
- [ ] No cadastro/área do usuário, adicionar uma tela "Configurar impressora"
      que chama `GET /printers` e deixa o usuário escolher
- [ ] **Armazenar no banco do tipo7.com** (ex.: tabela `usuarios_config`):
      `printer_id`, `printer_type`, `printer_width`, `charset`, `ponto_de_uso`
      (ex.: `portaria` | `estacionamento` | `bilheteria`)
- [ ] Se o mesmo PC usa 2 impressoras (portaria + estacionamento), guardar a
      configuração **por ponto de uso** e passar `"printer"` explícito no `/print` ou `/ticket`

### 4.3 Chamada de impressão no fluxo de venda
- [ ] No momento de vender/emitir o ingresso, chamar `POST /ticket` (ou `/print`)
      com os dados do ingresso e o `printer` salvo do usuário
- [ ] Tratar erros: se o PrintServer não responder (usuário não instalou/PC desligado),
      exibir mensagem amigável com o link de download
- [ ] (Recomendado) Exibir o QR do ingresso **também na tela** do navegador,
      como fallback de validação caso a impressão falhe

### 4.4 Validação de QR Code (sugestão de padrão)
- [ ] Padrão do QR: `TIPO7|<codigo_ingresso>|<setor>`
- [ ] Na portaria, validar o QR escaneando e conferindo no banco
      (o conteúdo do QR pode ser assinado/criptografado se necessário)

---

## 5. Limitações e observações técnicas

- O PrintServer escuta em `http://localhost:8080` — **só o navegador do mesmo PC** alcança.
  (É o comportamento desejado: cada PC imprime na sua própria impressora.)
- Não é preciso abrir portas no firewall (localhost).
- Página https chamando `http://localhost:8080`: **validado que funciona**
  (navegadores tratam localhost como origem segura — testado com a KP-1025).
- A impressão via Bluetooth/COM tem auto-reconexão (watchdog) e retry progressivo.
- **Todas as impressões passam por uma fila** com envio em blocos + pausas (perfil automático:
  `fraca`/`seguro`/`forte`). Isso elimina o travamento de impressoras 58mm baratas com
  buffer pequeno (ex.: KP-1025 travava com 4 ingressos seguidos — agora imprime sem travar).
- Em caso de impressora ocupada (buffer cheio), o servidor **aguarda drenar e continua do
  mesmo ponto** — não reenvia o trabalho do zero nem duplica conteúdo.
- `/status` expõe `queue` (aguardando) e `printing` (imprimindo agora) para o site
  acompanhar em tempo real.
- `Width` (58/80mm) é estimado por heurística — o layout do cupom deve ser escolhido
  pelo site com base no `Width` retornado (58mm = 32 colunas; 80mm = 42 colunas).
- Caso o PC não tenha Bluetooth (desktop sem adaptador), o usuário precisa de um
  adaptador Bluetooth USB (sugestão usada/testada: TP-Link Bluetooth 5.4) ou
  impressora via cabo USB com driver do Windows.

---

## 6. Estado atual do projeto

| Item | Status |
|---|---|
| PrintServer (exe + código-fonte) | ✅ Pronto em `dist/` |
| Impressão Bluetooth (COM) | ✅ Testado fisicamente (KP-1025) |
| Impressão USB (COM serial e driver Windows) | ✅ Implementado (aguardando teste físico) |
| Descoberta de impressoras (modelo/tipo/largura) | ✅ Pronto |
| Auto-reconexão + início com Windows | ✅ Pronto e testado |
| Chamada a partir de página https | ✅ Testado |
| QR Code no ingresso (`/ticket`) | ✅ Implementado e testado |
| Normalização de acentos (ascii/cp850) | ✅ Pronto e testado |
| Fila de impressão + perfis automáticos (`fraca`/`seguro`/`forte`) | ✅ Implementado e testado |
| Envio em blocos com pausas + retomada sem duplicar (KP-1025) | ✅ Implementado e testado |
| `GET /capabilities` (capacidade detectada) | ✅ Pronto |
| `GET /licenca` (modo livre/restrito para cobrança futura) | ✅ Pronto (modo livre ativo) |
| Detecção do modelo real por Bluetooth (nome via BTHPORT) | ✅ Pronto e testado (KP-1025) |
| Painel local de configuração (`localhost:8080`) | ✅ Pronto |
| Integração no site tipo7.com (venda → impressão) | ✅ Implementada no site (ver seção 4) |
| Pacote de instalação para clientes | ✅ Pronto em `dist/` |

### Pendências sugeridas (opcionais)
- [ ] Teste físico com impressora USB (cabo) para validar o caminho `usb`/`windows`
- [ ] Logo no cupom (impressão de imagem bitmap — pode ser adicionada ao `/ticket`)
- [ ] Cópia do ingressos para reimpressão (a cargo do site)
