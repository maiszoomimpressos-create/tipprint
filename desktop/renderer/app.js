'use strict';

const $ = (id) => document.getElementById(id);

const statusText = $('statusText');
let currentType = null;
let activePort = null;

function showStatus(msg) {
  statusText.textContent = msg;
}

function setControls(enabled) {
  ['backToTypes', 'refreshPorts', 'refreshPortsUsb', 'connectNet', 'printTest', 'repairBtBtn'].forEach((id) => {
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
  ['portsList', 'portsListUsb'].forEach((listId) => {
    $(listId).querySelectorAll('.port-item').forEach((item) => {
      item.classList.toggle('active', item.dataset.path === activePort);
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
  activePort = null;
  renderConnBanner();
  if (type === 'bt') {
    discoverDevices();
    refreshPorts('bt');
  }
  if (type === 'usb') refreshPorts('usb');
}

function openChooser() {
  currentType = null;
  activePort = null;
  window.tipprint.disconnect().catch(() => {});
  $('work').classList.add('hidden');
  $('chooser').classList.remove('hidden');
  showStatus('');
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
  if (currentType === 'bt') discoverDevices();
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
  const hint = listId === 'portsList' ? $('portsHint') : $('portsHintUsb');
  hint.classList.toggle('hidden', ports.length > 0);
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

async function refreshPorts(kind) {
  const btnId = kind === 'bt' ? 'refreshPorts' : 'refreshPortsUsb';
  setControls(false);
  setBusy(btnId, true);
  showStatus('Buscando portas...');
  await new Promise((r) => setTimeout(r, 800));
  try {
    const ports = await window.tipprint.listPorts();
    cachedPorts = ports;
    renderPorts(kind === 'bt' ? 'portsList' : 'portsListUsb', ports);
    showStatus(ports.length
      ? 'Portas encontradas: ' + ports.map((p) => p.path).join(', ')
      : 'Nenhuma porta encontrada.');
  } catch (e) {
    showStatus('Falha ao listar portas: ' + e.message);
  } finally {
    setControls(true);
    setBusy(btnId, false);
  }
}

function findPortForDevice(device) {
  const mac = deviceMac(device);
  if (!mac) return null;
  return cachedPorts.find((p) => ((p.pnpId || '') + '').toUpperCase().includes(mac)) || null;
}

function renderBtDevices(devices) {
  const list = $('btDevices');
  list.innerHTML = '';
  $('btDevicesHint').classList.toggle('hidden', devices.length > 0);
  devices.forEach((device) => {
    const item = document.createElement('div');
    item.className = 'device-item';
    const port = findPortForDevice(device);
    const tag = document.createElement('span');
    if (port) {
      tag.className = 'device-tag connected';
      tag.textContent = port.path;
    } else if (device.Kind === 'ble') {
      tag.className = 'device-tag new';
      tag.textContent = 'BLE';
    } else if (device.Paired) {
      tag.className = 'device-tag paired';
      tag.textContent = 'PAREADO';
    } else {
      tag.className = 'device-tag new';
      tag.textContent = 'NOVO';
    }
    item.innerHTML = '<div><div class="device-name"></div><div class="device-meta"></div></div>';
    item.querySelector('.device-name').textContent = device.Name || 'Dispositivo sem nome';
    item.querySelector('.device-meta').textContent = macDisplay(deviceMac(device));
    item.appendChild(tag);
    item.addEventListener('click', () => handleDeviceTap(device));
    list.appendChild(item);
  });
}

async function discoverDevices() {
  setControls(false);
  setBusy('discoverDevices', true);
  showStatus('Pesquisando dispositivos Bluetooth por perto (~10s)...');
  try {
    const devices = await window.tipprint.btDevices();
    if (!devices) {
      showStatus('Falha na busca de dispositivos.');
      return;
    }
    const list = devices;
    renderBtDevices(list);
    showStatus(list.length
      ? 'Dispositivos: ' + list.map((d) => d.Name || macDisplay(deviceMac(d))).join(', ')
      : 'Nenhum dispositivo encontrado por perto.');
  } finally {
    setControls(true);
    setBusy('discoverDevices', false);
  }
}

async function discoverNewDev() {
  showStatus('Abrindo a varredura de dispositivos do Windows... pareie o sistema lá e, quando voltar, atualizo a lista sozinho.');
  await window.tipprint.openBtSettings();
  btPollTimer = setInterval(() => {
    if (currentType === 'bt') discoverDevices();
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
    try {
      const found = await waitForPort(device, 30); // espera ate ~60s pela porta COM aparecer
      if (found) {
        connectPort(found.path);
      } else {
        showStatus('Ainda não vi a porta COM dessa impressora. Confira se ela ficou pareada em Configurações > ' +
          'Bluetooth e dispositivos, e toque nela na lista de novo (se ela sumir da lista, clique em "Buscar dispositivos").');
        discoverDevices();
      }
    } finally {
      setControls(true);
    }
    return;
  }
  const port = await ensurePortForDevice(device);
  if (port) {
    connectPort(port.path);
    return;
  }
  if (device.Paired) {
    showStatus('Dispositivo pareado, mas sem porta COM ainda. Clique em "Atualizar portas".');
    return;
  }
  if (!device.Id) {
    showStatus('Este dispositivo não abriu porta COM. Use "Abrir pareamento do Windows" e depois toque aqui de novo.');
    return;
  }
  setControls(false);
  showStatus('Pareando com ' + (device.Name || macDisplay(deviceMac(device))) +
    '... CONFIRA O CELULAR: se ele pedir pareamento, aceite nele (o PIN aparece na tela do celular). ' +
    'Se o Windows pedir PIN, digite 0000.');
  try {
    const status = await window.tipprint.btPair(device.Id);
    if (status === 'Paired' || status === 'AlreadyPaired') {
      showStatus('Pareado! Procurando a porta COM...');
      const found = await waitForPort(device);
      if (found) {
        connectPort(found.path);
      } else {
        showStatus('Pareado! Mas nenhuma porta COM apareceu: celulares geralmente não expõem porta serial — ' +
          'só impressoras/equipamentos. Clique em "Atualizar portas".');
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
    showStatus('Falha ao parear: ' + e.message);
  } finally {
    setControls(true);
  }
}

function printerMacFromPorts() {
  for (const p of cachedPorts) {
    const m = (p.pnpId || '').match(/&([0-9A-F]{12})_C\d+$/i);
    if (m && !(p.pnpId || '').includes('LOCALMFG')) return m[1];
  }
  return '';
}

async function repairBtClicked() {
  setControls(false);
  const mac = printerMacFromPorts();
  if (!mac) {
    showStatus('Nenhuma impressora com porta COM quebrada encontrada. Separe-a no Windows e busque dispositivos de novo.');
    setControls(true);
    return;
  }
  setBusy('repairBtBtn', true);
  showStatus('Reparando pareamento da impressora (' + mac + '): desparear, reparar e recriar a porta COM...');
  try {
    const r = await window.tipprint.btRepair(mac);
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
    await refreshPorts('bt');
    discoverDevices();
    const logText = await window.tipprint.getLog();
    if (logText) await window.tipprint.copyText(logText);
    showStatus(msg + ' Log copiado para o suporte (Ctrl+V).');
  } catch (e) {
    showStatus('Falha no reparo: ' + e.message);
  } finally {
    setControls(true);
    setBusy('repairBtBtn', false);
  }
}

async function waitForPort(device, attempts = 15) {
  for (let i = 0; i < attempts; i++) {
    await new Promise((r) => setTimeout(r, 2000));
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

async function connectPort(portPath) {
  setControls(false);
  showStatus('Conectando a ' + portPath + ' (9600)...');
  try {
    await window.tipprint.connectSerial(portPath, 9600);
    activePort = portPath;
    showStatus('Conectado: ' + portPath);
    if (currentType === 'bt') {
      $('btConnectedLabel').textContent = '● ' + portPath;
      $('btConnectedLabel').classList.remove('hidden');
    }
    renderConnBanner();
  } catch (e) {
    const msg = String(e.message || '');
    if (/access denied/i.test(msg)) {
      showStatus('Access denied em ' + portPath + ': a impressora pode estar ligada em outro aparelho ' +
        '(desconecte do celular — Bluetooth aguenta 1 conexão por vez), desligada ou fora do alcance. ' +
        'Confira e toque em conectar de novo.');
    } else {
      showStatus('Falha ao conectar: ' + msg);
    }
  } finally {
    setControls(true);
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
$('refreshPorts').addEventListener('click', () => refreshPorts('bt'));
$('discoverDevices').addEventListener('click', discoverDevices);
$('refreshPortsUsb').addEventListener('click', () => refreshPorts('usb'));
$('connectNet').addEventListener('click', connectNetClicked);
$('printTest').addEventListener('click', printTestClicked);

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
