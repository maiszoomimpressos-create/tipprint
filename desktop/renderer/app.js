'use strict';

const $ = (id) => document.getElementById(id);

const statusText = $('statusText');
let currentType = null;
let activePort = null;

// Sobe a cada vez que o usuario sai da tela atual (botao "Voltar"). Fluxos longos (parear,
// reparar pareamento, esperar porta COM aparecer) guardam o valor no inicio e conferem depois
// de cada espera - se mudou, o usuario ja saiu daqui e o fluxo para de mexer na tela sozinho.
// Nao mata o processo de fundo (PowerShell/bt-watcher), so evita que a UI fique presa nele.
let opGeneration = 0;

function showStatus(msg) {
  statusText.textContent = msg;
}

function setControls(enabled) {
  // "backToTypes" fica de fora de proposito: o usuario sempre tem que conseguir sair da tela,
  // mesmo com uma busca/pareamento/reparo rodando (era o que prendia o painel em "Buscando...").
  ['refreshPortsUsb', 'connectNet', 'printTest', 'repairBtBtn', 'discoverDevices'].forEach((id) => {
    const el = $(id);
    if (el) el.disabled = !enabled;
  });
}

function setBusy(id, busy) {
  const el = $(id);
  if (el) el.classList.toggle('busy', busy);
}

function renderConnBanner() {
  const el = $('connBanner');
  if (!el) return;
  el.classList.remove('ok', 'err');
  if (activePort) {
    el.textContent = 'Impressora conectada — ' + activePort;
    el.classList.add('ok');
  } else {
    el.textContent = 'Nenhuma impressora conectada';
    el.classList.add('err');
  }
  ['btDevices', 'newDevices', 'portsListUsb'].forEach((listId) => {
    const listEl = $(listId);
    if (!listEl) return;
    listEl.querySelectorAll('.port-item, .device-item').forEach((item) => {
      item.classList.toggle('active', !!item.dataset.path && item.dataset.path === activePort);
    });
  });
}

function openWork(type) {
  currentType = type;
  $('chooser').classList.add('hidden');
  $('work').classList.remove('hidden');
  $('btSection').classList.toggle('hidden', type !== 'bt');
  $('usbSection').classList.toggle('hidden', type !== 'usb');
  $('netSection').classList.toggle('hidden', type !== 'net');
  // "Reparar pareamento"/"Copiar log" so fazem sentido no Bluetooth - "Trocar tipo de
  // conexao" (dentro do mesmo menu "Mais opcoes") vale pros 3 tipos.
  ['repairBtBtn', 'copyLogBtn', 'repairCaption'].forEach((id) => $(id).classList.toggle('hidden', type !== 'bt'));
  $('moreOptions').classList.add('hidden');
  $('moreOptionsToggle').textContent = 'Mais opções ▾';
  activePort = null;
  renderConnBanner();
  if (type === 'bt') {
    $('newDevicesWrap').classList.add('hidden');
    loadPairedDevices();
  }
  if (type === 'usb') refreshPorts();
}

function openChooser() {
  opGeneration++; // cancela qualquer espera/pareamento/reparo em andamento
  currentType = null;
  activePort = null;
  window.tipprint.disconnect().catch(() => {});
  $('work').classList.add('hidden');
  $('chooser').classList.remove('hidden');
  showStatus('');
  setControls(true);
  setBusy('discoverDevices', false);
  setBusy('repairBtBtn', false);
}

function renderBtStatus(elId, radios) {
  const el = $(elId);
  if (!el) return;
  const active = radios.filter((r) => r.State === 'On');
  if (active.length > 0) {
    el.className = 'bt-status ok';
    el.innerHTML =
      'Bluetooth ativo · <span class="bt-name">' + active[0].Name + '</span>';
    return;
  }
  if (radios.length > 0) {
    el.className = 'bt-status warn';
    const names = radios.map((r) => r.Name).join(' / ');
    el.innerHTML = 'Bluetooth desligado ou desativado · <span class="bt-name">' + names +
      ' — ligue em Configurações → Bluetooth e dispositivos.</span>';
    return;
  }
  el.className = 'bt-status err';
  el.innerHTML = 'Nenhum adaptador Bluetooth detectado · <span class="bt-name">' +
    'Se for PC de mesa, conecte um adaptador (ex.: TP-Link) para parear a impressora.</span>';
}

async function refreshBtStatus() {
  try {
    const radios = await window.tipprint.btStatus();
    renderBtStatus('btStatusLine', radios);
    renderBtStatus('btStatusLineSection', radios);
    const active = radios.some((r) => r.State === 'On');
    const present = radios.length > 0;
    $('enableBt').classList.toggle('hidden', active || !present);
    $('enableBtSection').classList.toggle('hidden', active || !present);
    if (active) stopBtPolling();
  } catch (e) {
    const el = $('btStatusLine');
    if (el) {
      el.className = 'bt-status err';
      el.textContent = 'Falha ao verificar Bluetooth: ' + e.message;
    }
  }
}

let btPollTimer = null;

function stopBtPolling() {
  if (btPollTimer) {
    clearInterval(btPollTimer);
    btPollTimer = null;
  }
}

async function askEnableBt() {
  stopBtPolling();
  showStatus('Abra as configurações do Windows e ligue o Bluetooth. Vou reconferir sozinho.');
  await window.tipprint.openBtSettings();
  btPollTimer = setInterval(refreshBtStatus, 2000);
  setTimeout(stopBtPolling, 60000);
}

$('enableBt').addEventListener('click', askEnableBt);
$('enableBtSection').addEventListener('click', askEnableBt);
$('discoverNewDev').addEventListener('click', discoverNewDev);
$('repairBtBtn').addEventListener('click', repairBtClicked);
$('copyLogBtn').addEventListener('click', async () => {
  try {
    const logText = await window.tipprint.getLog();
    if (!logText) {
      showStatus('Log vazio — faça uma busca e uma tentativa de conexão primeiro.');
      return;
    }
    window.tipprint.copyText(logText);
    showStatus('Log copiado! Cole aqui no chat de suporte (Ctrl+V).');
  } catch (e) {
    showStatus('Falha ao copiar log: ' + e.message);
  }
});
window.addEventListener('focus', () => {
  refreshBtStatus();
  if (currentType === 'bt') loadPairedDevices({ silent: true });
});

function tagFor(port) {  const hay = ((port.friendlyName || '') + ' ' + (port.pnpId || '') + ' ' + (port.manufacturer || '')).toLowerCase();
  if (hay.includes('bluetooth') || hay.includes('bthenum') || hay.includes('tooth') || hay.includes('spp')) {
    return 'BT';
  }
  return 'USB';
}

function renderPorts(listId, ports) {
  const list = $(listId);
  list.innerHTML = '';
  $('portsHintUsb').classList.toggle('hidden', ports.length > 0);
  ports.forEach((port) => {
    const item = document.createElement('div');
    item.className = 'port-item';
    item.dataset.path = port.path;
    if (port.path === activePort) item.classList.add('active');
    const tag = document.createElement('span');
    tag.className = 'port-tag';
    tag.textContent = tagFor(port);
    item.innerHTML =
      '<div><div class="port-path"></div><div class="port-meta"></div></div>';
    item.querySelector('.port-path').textContent = port.path;
    const meta = [port.friendlyName, port.manufacturer]
      .filter(Boolean).join(' · ') || 'Dispositivo serial';
    item.querySelector('.port-meta').textContent = meta;
    item.appendChild(tag);
    item.addEventListener('click', () => connectPort(port.path));
    list.appendChild(item);
  });
}

function deviceMac(device) {
  if (device && device.Address) {
    return String(device.Address).replace(/:/g, '').toUpperCase();
  }
  if (device && device.Id) {
    const parts = String(device.Id).split('-');
    return (parts[parts.length - 1] || '').replace(/:/g, '').toUpperCase();
  }
  return '';
}

function macDisplay(mac) {
  if (!mac) return '';
  return mac.toLowerCase().replace(/(.{2})(?=.)/g, '$1:');
}

let cachedPorts = [];
let pairedDevices = []; // ultima lista carregada em "Suas impressoras" (so pareadas)

// So USB agora (Bluetooth usa loadPairedDevices/searchNewDevices, ver acima).
async function refreshPorts() {
  setControls(false);
  setBusy('refreshPortsUsb', true);
  showStatus('Buscando portas...');
  await new Promise((r) => setTimeout(r, 800));
  try {
    const ports = await window.tipprint.listPorts();
    cachedPorts = ports;
    renderPorts('portsListUsb', ports);
    showStatus(ports.length
      ? 'Portas encontradas: ' + ports.map((p) => p.path).join(', ')
      : 'Nenhuma porta encontrada.');
  } catch (e) {
    showStatus('Falha ao listar portas: ' + e.message);
  } finally {
    setControls(true);
    setBusy('refreshPortsUsb', false);
  }
}

function findPortForDevice(device) {
  const mac = deviceMac(device);
  if (!mac) return null;
  return cachedPorts.find((p) => ((p.pnpId || '') + '').toUpperCase().includes(mac)) || null;
}

function renderDeviceList(listId, hintId, devices) {
  const list = $(listId);
  list.innerHTML = '';
  if (hintId) $(hintId).classList.toggle('hidden', devices.length > 0);
  devices.forEach((device) => {
    const item = document.createElement('div');
    item.className = 'device-item';
    const port = findPortForDevice(device);
    item.dataset.path = port ? port.path : '';
    if (port && port.path === activePort) item.classList.add('active');
    const tag = document.createElement('span');
    if (port) {
      tag.className = 'device-tag connected';
      tag.textContent = port.path;
    } else if (device.Kind === 'ble') {
      tag.className = 'device-tag new';
      tag.textContent = 'BLE';
    } else if (device.Paired) {
      tag.className = 'device-tag paired';
      tag.textContent = 'PAREADA';
    } else {
      tag.className = 'device-tag new';
      tag.textContent = 'NOVA';
    }
    item.innerHTML = '<div><div class="device-name"></div><div class="device-meta"></div></div>';
    item.querySelector('.device-name').textContent = device.Name || 'Dispositivo sem nome';
    item.querySelector('.device-meta').textContent = macDisplay(deviceMac(device));
    item.appendChild(tag);
    item.addEventListener('click', () => handleDeviceTap(device));
    list.appendChild(item);
  });
}

// Rapido: so impressoras ja pareadas (sem varredura de radio). E' o que roda ao abrir a
// tela e ao voltar o foco pro app - antes disparava a busca de ~10s toda vez, prendendo
// o usuario numa espera desnecessaria pra so reconectar em algo que ja tinha pareado.
async function loadPairedDevices(opts) {
  const silent = !!(opts && opts.silent); // true = so atualiza a lista, nao mexe no status
  // (evita apagar uma mensagem especifica - "essa impressora nao respondeu" etc. -
  // que some em menos de 1s se a gente sempre escrever por cima com o texto generico)
  setControls(false);
  if (!silent) showStatus('Carregando impressoras pareadas...');
  try {
    const [devices, ports] = await Promise.all([
      window.tipprint.btKnownDevices(),
      window.tipprint.listPorts()
    ]);
    cachedPorts = ports;
    pairedDevices = devices || [];
    renderDeviceList('btDevices', 'btDevicesHint', pairedDevices);
    renderConnBanner();
    if (!silent) {
      showStatus(pairedDevices.length
        ? 'Impressoras pareadas: ' + pairedDevices.map((d) => d.Name || macDisplay(deviceMac(d))).join(', ')
        : 'Nenhuma impressora pareada ainda.');
    }
  } catch (e) {
    if (!silent) showStatus('Falha ao carregar impressoras pareadas: ' + e.message);
  } finally {
    setControls(true);
  }
}

// Lento (~10s, varredura ativa): so roda quando o usuario pede "Procurar nova impressora".
// Mostra so quem ainda NAO esta na lista de pareadas (a pareada some daqui pra nao duplicar).
async function searchNewDevices() {
  setControls(false);
  setBusy('discoverDevices', true);
  $('newDevicesWrap').classList.remove('hidden');
  showStatus('Pesquisando dispositivos Bluetooth por perto (~10s)...');
  try {
    const all = await window.tipprint.btDevices();
    if (!all) {
      showStatus('Falha na busca de dispositivos.');
      return;
    }
    const pairedMacs = new Set(pairedDevices.map(deviceMac));
    const news = all.filter((d) => !pairedMacs.has(deviceMac(d)));
    renderDeviceList('newDevices', 'newDevicesHint', news);
    showStatus(news.length
      ? 'Encontrados: ' + news.map((d) => d.Name || macDisplay(deviceMac(d))).join(', ')
      : 'Nenhum dispositivo novo encontrado por perto.');
  } finally {
    setControls(true);
    setBusy('discoverDevices', false);
  }
}

async function discoverNewDev() {
  showStatus('Abrindo a varredura de dispositivos do Windows... pareie o sistema lá e, quando voltar, atualizo a lista sozinho.');
  await window.tipprint.openBtSettings();
  btPollTimer = setInterval(() => {
    if (currentType === 'bt') loadPairedDevices({ silent: true });
  }, 4000);
  setTimeout(stopBtPolling, 120000);
}

async function ensurePortForDevice(device) {
  try {
    cachedPorts = await window.tipprint.listPorts();
  } catch {
    // mantem cache atual
  }
  return findPortForDevice(device);
}

async function handleDeviceTap(device) {
  if (device.Kind === 'ble') {
    // "Reparar pareamento" so funciona pra impressora que ja tem porta COM quebrada - um
    // dispositivo visto so via BLE nunca teve porta COM, entao esse botao nao ajudaria aqui.
    // O que resolve e um pareamento classico novo, feito pela propria tela do Windows.
    showStatus('Este dispositivo só foi visto via BLE (ainda não pareou no modo clássico) — BLE não cria porta COM. ' +
      'Abrindo o pareamento do Windows: pareie lá (PIN 0000 se pedir) que eu conecto sozinho assim que a porta COM aparecer.');
    await window.tipprint.openBtSettings();
    setControls(false);
    const myGen = opGeneration;
    try {
      const found = await waitForPort(device, 30, myGen); // espera ate ~60s pela porta COM aparecer
      if (opGeneration !== myGen) return; // usuario ja saiu dessa tela - nao mexe mais nela
      if (found) {
        connectPort(found.path);
      } else {
        showStatus('Ainda não vi a porta COM dessa impressora. Confira se ela ficou pareada em Configurações > ' +
          'Bluetooth e dispositivos, e toque em "Procurar nova impressora" de novo.');
        loadPairedDevices({ silent: true });
      }
    } finally {
      if (opGeneration === myGen) setControls(true);
    }
    return;
  }
  const port = await ensurePortForDevice(device);
  if (port) {
    connectPort(port.path);
    return;
  }
  if (device.Paired) {
    showStatus((device.Name || macDisplay(deviceMac(device))) +
      ' está pareada, mas não respondeu agora — confira se está ligada e por perto, e toque nela de novo.');
    loadPairedDevices({ silent: true });
    return;
  }
  if (!device.Id) {
    showStatus('Este dispositivo não abriu porta COM. Use "Abrir pareamento do Windows" e depois toque aqui de novo.');
    return;
  }
  setControls(false);
  const myGen = opGeneration;
  showStatus('Pareando com ' + (device.Name || macDisplay(deviceMac(device))) +
    '... CONFIRA O CELULAR: se ele pedir pareamento, aceite nele (o PIN aparece na tela do celular). ' +
    'Se o Windows pedir PIN, digite 0000. (Pode demorar ate ~3min - da pra sair da tela a qualquer momento.)');
  try {
    const status = await window.tipprint.btPair(device.Id);
    if (opGeneration !== myGen) return; // usuario ja saiu dessa tela - nao mexe mais nela
    if (status === 'Paired' || status === 'AlreadyPaired') {
      showStatus('Pareado! Procurando a porta COM...');
      const found = await waitForPort(device, 15, myGen);
      if (opGeneration !== myGen) return;
      if (found) {
        connectPort(found.path);
      } else {
        showStatus('Pareado! Mas nenhuma porta COM apareceu: celulares geralmente não expõem porta serial — ' +
          'só impressoras/equipamentos.');
        loadPairedDevices({ silent: true });
      }
      return;
    }
    const after = await ensurePortForDevice(device);
    if (after) {
      connectPort(after.path);
      return;
    }
    showStatus('Pareamento pelo app falhou (' + status + '). Abrindo o pareamento do Windows ' +
      '(Configurações > Bluetooth)... aceite lá e volte aqui depois.');
    await window.tipprint.openBtSettings();
  } catch (e) {
    if (opGeneration !== myGen) return;
    showStatus('Falha ao parear: ' + e.message);
  } finally {
    if (opGeneration === myGen) setControls(true);
  }
}

function printerMacFromPorts() {
  for (const p of cachedPorts) {
    const m = (p.pnpId || '').match(/&([0-9A-F]{12})_C\d+$/i);
    if (m && !(p.pnpId || '').includes('LOCALMFG')) return m[1];
  }
  return '';
}

// Caso real (2026-08-14): a impressora parava de conectar com "dispositivo inexistente" e
// nem religar a impressora nem reparar/re-parear pelo Windows resolvia - a causa de verdade
// era o driver RFCOMM (Bluetooth classico, o que vira porta COM) do adaptador USB travado
// com "Codigo 10". Reparar pareamento nunca ajudaria nesse caso, entao checamos isso ANTES -
// se achar, avisa e nem tenta reparar pareamento (economiza o usuario de girar em falso).
async function checkBtAdapterHealth() {
  try {
    return await window.tipprint.btAdapterCheck();
  } catch {
    return { Problems: [], AdapterCount: 0, Adapters: [] };
  }
}

// Modal reservado pra achados que merecem chamar atenção de verdade (driver travado, sugestao
// de porta USB) - diferente da mensagem no toque de um dispositivo (essa e' rotina do dia a
// dia, fica inline mesmo). Reaproveita o mesmo componente visual do modal de atualizacao.
function showDiagModal(severity, title, message) {
  const titleEl = $('diagModalTitle');
  titleEl.className = 'modal-title ' + severity;
  titleEl.textContent = title;
  $('diagModalMsg').textContent = message;
  $('diagModal').classList.remove('hidden');
}

function hideDiagModal() {
  $('diagModal').classList.add('hidden');
}

$('diagModalOk').addEventListener('click', hideDiagModal);
$('diagModalCopyLog').addEventListener('click', async () => {
  try {
    const logText = await window.tipprint.getLog();
    if (!logText) return;
    await window.tipprint.copyText(logText);
    hideDiagModal();
    showStatus('Log copiado! Cole aqui no chat de suporte (Ctrl+V).');
  } catch (e) {
    showStatus('Falha ao copiar log: ' + e.message);
  }
});

async function repairBtClicked() {
  setControls(false);
  setBusy('repairBtBtn', true);
  showStatus('Conferindo a saúde do adaptador Bluetooth antes de reparar o pareamento...');
  const health = await checkBtAdapterHealth();
  const blocking = (health.Problems || []).find((p) => String(p.Problem || '').toUpperCase().includes('FAILED_START'));
  if (blocking) {
    const adapterName = blocking.ParentAdapter || blocking.Device;
    showStatus('');
    showDiagModal('blocking', '⛔ Não é o pareamento',
      'O driver Bluetooth clássico (RFCOMM) do adaptador "' + adapterName + '" travou no Windows ' +
      '(Código 10 — "não pode iniciar"). Reparar pareamento não vai resolver isso.\n\n' +
      'Desconecte e reconecte o adaptador Bluetooth USB, ou: Gerenciador de Dispositivos → Bluetooth → ' +
      'o adaptador → Desabilitar dispositivo, espera 5s, Habilitar de novo. Depois toque em ' +
      '"Reparar pareamento" outra vez.');
    setControls(true);
    setBusy('repairBtBtn', false);
    return;
  }
  // Nao e' garantia de causa (ja vimos conexao funcionar mesmo com isso true), mas e' um
  // fator de risco conhecido pra Bluetooth USB - vale sugerir se o reparo falhar mesmo assim.
  const usb3Adapter = (health.Adapters || []).find((a) => a.Status === 'OK' && a.OnUsb3);
  const mac = printerMacFromPorts();
  if (!mac) {
    showStatus('Nenhuma impressora com porta COM quebrada encontrada. Separe-a no Windows e busque dispositivos de novo.');
    setControls(true);
    setBusy('repairBtBtn', false);
    return;
  }
  const myGen = opGeneration;
  showStatus('Reparando pareamento da impressora (' + mac + '): desparear, reparar e recriar a porta COM... ' +
    '(pode demorar ate ~2min - da pra sair da tela a qualquer momento.)');
  try {
    const r = await window.tipprint.btRepair(mac);
    if (opGeneration !== myGen) return; // usuario ja saiu dessa tela - nao mexe mais nela
    let msg;
    if (r.ok && r.port) {
      msg = 'Pareamento reparado! Porta nova: ' + r.port + '.';
    } else if (r.paired) {
      msg = 'Pareada, mas sem porta COM ainda. Se a impressora não piscar em modo de pareamento, desligue-a por uns minutos.';
    } else if (r.error) {
      msg = 'Falha no reparo (' + r.error + ').';
    } else {
      msg = 'Não foi possível parear (' + (r.status || '?') + '). Confira se a impressora está ligada e próxima do adaptador.';
    }
    await loadPairedDevices({ silent: true });
    const logText = await window.tipprint.getLog();
    if (logText) await window.tipprint.copyText(logText);
    showStatus(msg + ' Log copiado para o suporte (Ctrl+V).');
    if (!(r.ok && r.port) && usb3Adapter) {
      showDiagModal('warn', '⚠ Reparo não pegou',
        msg + '\n\nVale tentar: o adaptador "' + usb3Adapter.FriendlyName + '" está numa porta USB 3.0 — ' +
        'Bluetooth USB às vezes fica instável nelas. Troca pra uma porta USB 2.0 e tenta de novo.');
    }
  } catch (e) {
    if (opGeneration !== myGen) return;
    showStatus('Falha no reparo: ' + e.message);
  } finally {
    if (opGeneration === myGen) setControls(true);
    setBusy('repairBtBtn', false);
  }
}

async function waitForPort(device, attempts, myGen) {
  for (let i = 0; i < attempts; i++) {
    await new Promise((r) => setTimeout(r, 2000));
    if (opGeneration !== myGen) return null; // usuario saiu da tela - para de esperar
    try {
      cachedPorts = await window.tipprint.listPorts();
    } catch {
      continue;
    }
    const port = findPortForDevice(device);
    if (port) return port;
  }
  return null;
}

// Falhas de conexao BT costumam ser transitorias (rádio ocupado um instante, porta ainda
// "esfriando" depois de outra tentativa) - o programa mesmo tenta de novo sozinho antes de
// pedir qualquer coisa pro usuario (achado na pratica em 2026-08-14: 1ª tentativa falhou com
// "acesso negado", a 2ª segundos depois conectou, sem nada ter mudado fisicamente).
async function connectPort(portPath) {
  const maxAttempts = 3;
  setControls(false);
  let lastErr = null;
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    showStatus(attempt === 1
      ? ('Conectando a ' + portPath + '...')
      : ('Não respondeu, tentando de novo automaticamente (' + attempt + '/' + maxAttempts + ')...'));
    try {
      await window.tipprint.connectSerial(portPath);
      activePort = portPath;
      showStatus('Conectado: ' + portPath);
      if (currentType === 'bt') {
        $('btConnectedLabel').textContent = '● ' + portPath;
        $('btConnectedLabel').classList.remove('hidden');
      }
      renderConnBanner();
      setControls(true);
      return;
    } catch (e) {
      lastErr = e;
      if (attempt < maxAttempts) await new Promise((r) => setTimeout(r, 2500));
    }
  }
  await showConnectFailure(portPath, String((lastErr && lastErr.message) || ''));
  setControls(true);
}

// So chega aqui depois que o programa ja tentou sozinho (connectPort) e nao conseguiu -
// nesse ponto e' mesmo um problema que precisa de acao fisica, entao a mensagem já diz qual.
// Roda o diagnostico do adaptador Bluetooth em segundo plano e anexa se achar algo concreto
// (driver travado etc) em vez de deixar so' o "tente de novo" generico.
async function showConnectFailure(portPath, msg) {
  let friendly;
  if (/access denied|acesso.*negad/i.test(msg)) {
    friendly = 'Acesso negado em ' + portPath + ': a impressora pode estar ligada em outro aparelho ' +
      '(desconecte do celular — Bluetooth aguenta 1 conexão por vez) ou a porta ainda está sendo liberada. ' +
      'Desligue e ligue a impressora e toque em conectar de novo.';
  } else if (/n[aã]o encontrada|not found/i.test(msg)) {
    friendly = 'Impressora não encontrada em ' + portPath + '. Desligue e ligue a impressora física, ' +
      'aproxime do computador, e toque em conectar de novo. Se continuar, use "Reparar pareamento".';
  } else if (/tempo limite|timeout|sem[aá]foro|n[aã]o respondeu ao conectar/i.test(msg)) {
    friendly = 'A impressora não respondeu a tempo. Desligue e ligue a impressora física, aproxime do ' +
      'computador, e tente conectar de novo.';
  } else if (/agent n[aã]o respondeu|desktop agent/i.test(msg)) {
    friendly = 'O TipPrint Desktop Agent não respondeu a tempo. Tente conectar de novo em alguns segundos.';
  } else {
    friendly = 'Falha ao conectar: ' + msg;
  }
  showStatus(friendly);
  if (currentType === 'bt') {
    try {
      const check = await window.tipprint.btAdapterCheck();
      if (check && check.Problems && check.Problems.length > 0) {
        const p = check.Problems[0];
        showStatus(friendly + ' Além disso, o adaptador Bluetooth "' + p.Device +
          '" está com problema de driver (' + p.Problem + ') — pode ser a causa raiz.');
      }
    } catch { /* diagnostico e' so' um extra - nao trava o fluxo se falhar */ }
  }
}

async function connectNetClicked() {
  const host = $('ipInput').value.trim();
  if (!host) return showStatus('Digite o endereço IP da impressora.');
  const port = parseInt($('portInput').value, 10) || 9100;
  setControls(false);
  showStatus('Conectando a ' + host + ':' + port + '...');
  try {
    await window.tipprint.connectNet(host, port);
    activePort = host + ':' + port;
    showStatus('Rede conectada: ' + activePort);
    renderConnBanner();
  } catch (e) {
    showStatus('Falha ao conectar: ' + e.message);
  } finally {
    setControls(true);
  }
}

async function printTestClicked() {
  if (!activePort) return showStatus('Nenhuma impressora conectada. Conecte primeiro.');
  setControls(false);
  showStatus('Imprimindo...');
  try {
    const r = await window.tipprint.printTest(activePort);
    showStatus('Impresso com sucesso (' + r.bytes + ' bytes).');
  } catch (e) {
    showStatus('Falha ao imprimir: ' + e.message);
  } finally {
    setControls(true);
  }
}

$('chooserBluetooth').addEventListener('click', () => openWork('bt'));
$('chooserUsb').addEventListener('click', () => openWork('usb'));
$('chooserNet').addEventListener('click', () => openWork('net'));
$('backToTypes').addEventListener('click', openChooser);
$('discoverDevices').addEventListener('click', searchNewDevices);
$('refreshPortsUsb').addEventListener('click', refreshPorts);
$('connectNet').addEventListener('click', connectNetClicked);
$('printTest').addEventListener('click', printTestClicked);
$('moreOptionsToggle').addEventListener('click', () => {
  const el = $('moreOptions');
  const willShow = el.classList.contains('hidden');
  el.classList.toggle('hidden', !willShow);
  $('moreOptionsToggle').textContent = willShow ? 'Mais opções ▴' : 'Mais opções ▾';
});

// ---------- Conta (login do cliente que contrata a API do TipPrint) ----------

let authMode = 'login';

async function refreshAccountBtn() {
  const r = await window.tipprint.authStatus().catch(() => ({ ok: false }));
  const btn = $('accountBtn');
  if (r.ok && r.loggedIn) {
    btn.textContent = r.email.split('@')[0];
    btn.classList.add('logged-in');
  } else {
    btn.textContent = 'Entrar';
    btn.classList.remove('logged-in');
  }
  return r;
}

function setAuthMode(mode) {
  authMode = mode;
  $('authModalTitle').textContent = mode === 'login' ? 'Entrar no TipPrint' : 'Criar conta TipPrint';
  $('authSubmit').textContent = mode === 'login' ? 'Entrar' : 'Criar conta';
  $('authToggleMode').textContent = mode === 'login' ? 'Ainda não tenho conta — criar' : 'Já tenho conta — entrar';
  $('authModalMsg').classList.add('hidden');
  $('authPasswordChecklist').classList.toggle('hidden', mode !== 'signup');
  updatePasswordChecklist();
}

const PASSWORD_RULES = [
  { rule: 'len', test: (pw) => pw.length >= 8 },
  { rule: 'upper', test: (pw) => /[A-Z]/.test(pw) },
  { rule: 'lower', test: (pw) => /[a-z]/.test(pw) },
  { rule: 'number', test: (pw) => /[0-9]/.test(pw) },
  { rule: 'special', test: (pw) => /[^A-Za-z0-9]/.test(pw) }
];

// Vai marcando ao vivo, enquanto a pessoa digita, o que ja foi cumprido - em vez de so
// avisar depois de tentar enviar.
function updatePasswordChecklist() {
  const pw = $('authPassword').value;
  PASSWORD_RULES.forEach(({ rule, test }) => {
    const li = $('authPasswordChecklist').querySelector('[data-rule="' + rule + '"]');
    if (li) li.classList.toggle('met', test(pw));
  });
}
$('authPassword').addEventListener('input', updatePasswordChecklist);

// So exigida na criacao de conta - login so tenta autenticar com o que a pessoa digitar,
// nao tem por que barrar login por causa de regra de senha (a conta ja existe do jeito que existe).
function validatePasswordStrength(pw) {
  const failed = PASSWORD_RULES.find(({ test }) => !test(pw));
  if (!failed) return null;
  const labels = {
    len: 'A senha precisa ter pelo menos 8 caracteres.',
    upper: 'A senha precisa de pelo menos uma letra maiúscula.',
    lower: 'A senha precisa de pelo menos uma letra minúscula.',
    number: 'A senha precisa de pelo menos um número.',
    special: 'A senha precisa de pelo menos um caractere especial (ex: ! @ # $ %).'
  };
  return labels[failed.rule];
}

function openAuthModal() {
  setAuthMode('login');
  $('authEmail').value = '';
  $('authPassword').value = '';
  $('authModal').classList.remove('hidden');
}

async function renderSystemsList() {
  const r = await window.tipprint.systemsList();
  const list = $('systemsList');
  list.innerHTML = '';
  if (!r.ok) {
    $('systemsHint').textContent = 'Falha ao carregar: ' + r.error;
    $('systemsHint').classList.remove('hidden');
    return;
  }
  $('systemsHint').classList.toggle('hidden', r.systems.length > 0);
  r.systems.forEach((sys) => {
    const item = document.createElement('div');
    item.className = 'device-item';
    const tag = document.createElement('span');
    tag.className = 'device-tag ' + (sys.status === 'active' ? 'connected' : 'new');
    tag.textContent = sys.status === 'active' ? 'ATIVA' : 'REVOGADA';
    item.innerHTML = '<div><div class="device-name"></div><div class="device-meta"></div></div>';
    item.querySelector('.device-name').textContent = sys.name;
    item.querySelector('.device-meta').textContent = sys.api_key;
    item.appendChild(tag);
    if (sys.status === 'active') {
      item.style.cursor = 'pointer';
      item.title = 'Clique pra revogar essa chave';
      item.addEventListener('click', async () => {
        if (!confirm('Revogar a chave de "' + sys.name + '"? O sistema que usa ela para de funcionar.')) return;
        await window.tipprint.systemsRevoke(sys.id);
        renderSystemsList();
      });
    }
    list.appendChild(item);
  });
}

async function openAccountModal() {
  const r = await refreshAccountBtn();
  $('accountEmail').textContent = r.loggedIn ? ('Logado como ' + r.email) : '';
  $('accountModal').classList.remove('hidden');
  renderSystemsList();
}

$('accountBtn').addEventListener('click', async () => {
  const r = await refreshAccountBtn();
  if (r.loggedIn) openAccountModal(); else openAuthModal();
});

$('authCancel').addEventListener('click', () => $('authModal').classList.add('hidden'));
$('authToggleMode').addEventListener('click', () => setAuthMode(authMode === 'login' ? 'signup' : 'login'));

$('authSubmit').addEventListener('click', async () => {
  const email = $('authEmail').value.trim();
  const password = $('authPassword').value;
  if (authMode === 'signup') {
    const err = validatePasswordStrength(password);
    if (err) {
      $('authModalMsg').textContent = err;
      $('authModalMsg').classList.remove('hidden');
      return;
    }
  }
  if (!email || !password) {
    $('authModalMsg').textContent = 'Preencha e-mail e senha.';
    $('authModalMsg').classList.remove('hidden');
    return;
  }
  $('authSubmit').disabled = true;
  try {
    const r = authMode === 'login'
      ? await window.tipprint.authLogin(email, password)
      : await window.tipprint.authSignup(email, password);
    if (!r.ok) {
      $('authModalMsg').textContent = r.error;
      $('authModalMsg').classList.remove('hidden');
      return;
    }
    if (authMode === 'signup' && r.needsEmailConfirm) {
      $('authModalMsg').textContent = 'Conta criada! Confirme seu e-mail (' + r.email + ') antes de entrar.';
      $('authModalMsg').classList.remove('hidden');
      return;
    }
    $('authModal').classList.add('hidden');
    await refreshAccountBtn();
    openAccountModal();
  } finally {
    $('authSubmit').disabled = false;
  }
});

$('accountClose').addEventListener('click', () => $('accountModal').classList.add('hidden'));
$('accountLogout').addEventListener('click', async () => {
  await window.tipprint.authLogout();
  $('accountModal').classList.add('hidden');
  refreshAccountBtn();
});

$('systemsCreateBtn').addEventListener('click', async () => {
  const name = $('newSystemName').value.trim();
  if (!name) return;
  $('systemsCreateBtn').disabled = true;
  try {
    const r = await window.tipprint.systemsCreate(name);
    if (!r.ok) { alert('Falha ao criar: ' + r.error); return; }
    $('newSystemName').value = '';
    renderSystemsList();
  } finally {
    $('systemsCreateBtn').disabled = false;
  }
});

refreshAccountBtn();

refreshBtStatus();

window.tipprint.appVersion().then((v) => {
  const el = $('appVersion');
  if (el) el.textContent = v;
}).catch(() => {});

let pendingUpdate = null;

function showUpdateModal(info) {
  pendingUpdate = info;
  $('updateMessage').textContent =
    'Nova versão ' + info.versionName + ' disponível.' +
    (info.notes ? '\n\n' + info.notes : '');
  $('updateProgress').classList.add('hidden');
  $('updateNow').disabled = false;
  $('updateModal').classList.remove('hidden');
}

function hideUpdateModal() {
  $('updateModal').classList.add('hidden');
  pendingUpdate = null;
}

async function checkUpdate(manual) {
  try {
    const info = await window.tipprint.updateCheck();
    if (!info) {
      if (manual) showStatus('Você já está na versão mais recente.');
      return;
    }
    showUpdateModal(info);
  } catch (e) {
    if (manual) showStatus('Falha ao verificar atualização: ' + e.message);
  }
}

$('checkUpdateLink').addEventListener('click', () => checkUpdate(true));
$('updateLater').addEventListener('click', hideUpdateModal);
$('updateNow').addEventListener('click', async () => {
  $('updateNow').disabled = true;
  $('updateProgress').classList.remove('hidden');
  $('updateMessage').classList.add('hidden');
  const r = await window.tipprint.updateDownload();
  if (!r.ok) {
    hideUpdateModal();
    showStatus('Falha ao baixar a atualização: ' + (r.error || 'erro'));
    return;
  }
  showStatus('Atualização baixada. Instalando…');
  await window.tipprint.updateInstall(r.path);
});

checkUpdate(false);
