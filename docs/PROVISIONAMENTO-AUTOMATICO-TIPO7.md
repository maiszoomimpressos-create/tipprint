# TipPrint — Provisionamento automático do PC (Bluetooth) para o tipo7.com

> ✅ **No ar em produção desde 2026-08-14.** Backend publicado em
> **`https://tipprint-backend.vercel.app`**. Testado ponta a ponta contra o endereço
> público de verdade: `/provision` → `/download` → ZIP → `PrintServer.exe` validou a
> chave contra o backend público, sem nada local envolvido. Pode chamar de qualquer
> lugar agora.

---

## 1. Objetivo

Hoje, pra usar o TipPrint num PC Windows, alguém baixa um ZIP genérico, instala, e
**configura a chave de API na mão** (edita `config.txt`). Isso é o que estamos eliminando.

Fluxo novo: o cliente do Tipo7 escolhe "imprimir via Bluetooth" → "PC Windows" → baixa um
ZIP **já personalizado pra aquela instalação**, com a autorização embutida. Instala e já
funciona — nenhum passo de "cole sua chave aqui".

```
[Cliente no tipo7.com]
    escolhe: Bluetooth → PC Windows
        │
        ▼
[Servidor do tipo7.com]  ──POST /provision (chave do sistema)──▶  [TipPrint Backend]
        │                                                              │
        │◀── { downloadUrl } ──────────────────────────────────────────┘
        │
        ▼
[Navegador do cliente] ── GET downloadUrl ──▶ baixa ZIP já com token embutido
        │
        ▼
Instalar.bat → PrintServer já nasce autorizado (SystemCheckLoop valida sozinho)
```

---

## 2. O que o tipo7.com precisa ter pronto (lado servidor, nunca no navegador)

1. Guardar a chave de sistema do Tipo7 (`tp_live_...`, já gerada) como **variável de
   ambiente no servidor** — nunca no código do frontend, nunca exposta ao navegador.
2. Quando o cliente, no fluxo de configuração de impressão, escolher **Bluetooth → PC
   Windows**, o **backend do Tipo7** (não o navegador) chama:

### `POST /provision` (TipPrint Backend)

```
Headers:
  Authorization: Bearer tp_live_xxxxxxxxxxxxxxxxxxxxxxxx   (chave do sistema "tipo7")
  Content-Type: application/json

Body (opcional):
{
  "label": "Estacionamento B - Caixa 3"   // texto livre, so' pra identificar essa
                                            // instalacao depois numa lista (revogar etc)
}
```

**Resposta OK (200):**
```json
{
  "ok": true,
  "downloadUrl": "https://tipprint-backend.vercel.app/download/dl_8f2a1c9e...",
  "expiresAt": "2026-08-15T00:20:00Z"
}
```

**Resposta erro (401/403 — chave invalida ou revogada):**
```json
{ "ok": false, "error": "Chave de sistema invalida ou revogada." }
```

3. Redirecionar o navegador do cliente (ou mostrar um botão/link) pra esse `downloadUrl`
   recebido — **é ele que baixa o ZIP de verdade**, não uma URL fixa do `/downloads/`.
   Esse link é de uso único / expira rápido (curto prazo, tipo 15-30min) — não guardar
   nem reusar, pedir um novo `/provision` a cada instalação.

4. Mostrar o passo a passo de instalação (mesmo texto de sempre — rodar `Instalar.bat`,
   parear a impressora). Isso não muda.

5. **(Opcional, recomendado)** Antes de mandar baixar de novo, checar se já tem um
   PrintServer rodando naquele PC:
   ```js
   fetch('http://localhost:8080/status', { signal: AbortSignal.timeout(1000) })
   ```
   Se responder, já está instalado e autorizado — pular direto pra tela de "escolher
   impressora" (já documentado em `docs/INTEGRACAO-TIPO7.md`, seção 3.1 `/printers`).

---

## 3. O que NÃO muda

- A API de impressão em si (`GET /printers`, `POST /connect`, `POST /ticket`, `GET
  /status` etc., tudo em `docs/INTEGRACAO-TIPO7.md`) continua **exatamente igual**.
  `/provision` só resolve a instalação/autorização inicial — depois disso, o fluxo de
  vender ingresso e imprimir é o mesmo de sempre.
- A chave de sistema do Tipo7 (`tp_live_...`) continua sendo **uma só**, do sistema
  inteiro — `/provision` é quem gera, por trás, um token individual pra cada instalação
  (uma máquina revogada não derruba as outras).

---

## 4. Pendências do lado do TipPrint (não é responsabilidade do Tipo7)

- [x] Implementar `POST /provision` no TipPrint Backend (gera token por instalação +
      monta o ZIP com `config.txt` pré-preenchido)
- [x] Implementar `GET /download/:token` (serve o ZIP, uso único/expira em 30min)
- [x] Fluxo testado de ponta a ponta localmente (provision → download → ZIP →
      `PrintServer.exe` extraído validou sozinho como "tipo7")
- [x] Publicar o TipPrint Backend num endereço público —
      **`https://tipprint-backend.vercel.app`**, testado de verdade contra produção
- [ ] Endpoint de admin pra listar/revogar instalações por sistema (hoje só dá pra
      revogar via SQL direto)

## 5. Android — ainda não incluído nesse desenho

O app Android já resolve impressão via link `tipprint://print` (sem RawBT), mas **ainda
não tem essa mesma amarração com a chave de sistema do Tipo7**. Cada estacionamento/caixa
ambulante que usar celular também vai precisar disso (rastrear/revogar por ponto de uso),
mas fica pra depois de validar o caminho do PC. Quando chegar a hora, o padrão é o mesmo:
o app pede um token individual, não usa a chave do sistema direto.
