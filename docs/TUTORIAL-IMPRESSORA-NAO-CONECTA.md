# TipPrint — impressora Bluetooth não conecta (tutorial rápido para o show)

Guia para quem está operando no evento resolver sozinho, sem precisar chamar o
suporte/dev. Segue a ordem — cada passo resolve a maioria dos casos antes de
precisar ir pro próximo.

Causa mais comum: o Windows "esquece" a porta COM da impressora depois que o
PC reinicia ou fica muito tempo sem usar o Bluetooth. A impressora continua
pareada, mas sem porta serial ativa — por isso o TipPrint não acha ela.

---

## Checklist de 30 segundos (antes de mexer em qualquer coisa)

- [ ] Impressora **ligada** e com **bateria** (carrega se a luz estiver piscando vermelho)
- [ ] Impressora **perto do PC** (Bluetooth clássico tem alcance curto, ~5-10m sem obstáculo)
- [ ] Impressora **não está conectada em outro aparelho** (celular, outro notebook) — Bluetooth clássico só aceita 1 conexão por vez
- [ ] Bluetooth do PC está **ligado** (ícone na barra de tarefas)

Se os 4 itens acima estão OK e mesmo assim não conecta, segue os passos.

---

## Passo 1 — Tentar no próprio TipPrint

1. Abre o TipPrint
2. Vai na aba **Bluetooth**
3. Clica em **"Buscar dispositivos"** (espera uns 10s, o botão fica com uma bolinha girando)
4. A impressora (ex: `KP-1025`) deve aparecer na lista — toca nela
5. Se aparecer uma porta COM (ex: `COM3`), clica em **"Atualizar portas"** e depois na porta que apareceu

Conectou? A faixa lá em cima fica **verde** escrito "Impressora conectada — COMx".
Se ficou verde, testa com **"Imprimir cupom de teste"**. Resolvido ✅

Se não conectou, vai pro passo 2.

---

## Passo 2 — Botão "Reparar pareamento"

1. Ainda na aba Bluetooth, clica em **"Reparar pareamento (refresh)"**
2. Espera (leva até ~30s) — ele desempareia e repareia a impressora sozinho
3. Quando terminar, olha a mensagem:
   - **"Pareamento reparado! Porta nova: COMx"** → volta pro Passo 1 e conecta na porta nova
   - Qualquer outra mensagem → vai pro Passo 3

---

## Passo 3 — Parear direto pelo Windows

Às vezes o pareamento programático falha mas o pareamento manual do Windows funciona.

1. Desliga a impressora, espera 5 segundos, liga de novo (isso ajuda ela a ficar "visível")
2. No TipPrint, clica em **"Abrir pareamento do Windows (se pedir PIN)"**
   - (ou manualmente: `Configurações do Windows` → `Bluetooth e dispositivos` → `Adicionar dispositivo` → `Bluetooth`)
3. A impressora deve aparecer na lista (`KP-1025` ou nome parecido) — clica nela
4. Se pedir PIN: tenta **0000**. Se não der, tenta **1234**
5. É normal ela aparecer como "conectando... conectado... desconectado" — isso é
   esperado, a porta COM só fica ativa de verdade quando um app (o TipPrint) abre ela
6. Volta pro TipPrint, clica em **"Atualizar portas"** — a porta nova deve aparecer
7. Clica na porta pra conectar

---

## Passo 4 — Se nada disso funcionar

- **Reinicia o TipPrint** (fecha e abre de novo)
- Se aparecer erro **"Access denied"** na porta: quer dizer que outro programa ou
  outro aparelho está segurando a conexão — confere se não tem celular pareado
  com a impressora ao mesmo tempo
- Em último caso: **reinicia o PC**, espera ele terminar de subir, e repete o
  Passo 1 → 2 → 3 nessa ordem

---

## Pra mandar pro suporte (se precisar mesmo escalar)

No TipPrint, aba Bluetooth, clica em **"Copiar log (para suporte)"** — isso já
copia o log técnico pra área de transferência. Cola (Ctrl+V) na mensagem pra
quem for ajudar. Isso economiza uma rodada inteira de perguntas.
