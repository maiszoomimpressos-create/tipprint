'use strict';

const $ = (id) => document.getElementById(id);

const statusText = $('statusText');
let currentType = null;
let activePort = null;

function showStatus(msg) {
  statusText.textContent = msg;
}

function setControls(enabled) {
  ['backToTypes', 'refreshPorts', 'refreshPortsUsb', 'connectNet', 'printTest'].forEach((id) => {
    const el = $(id);
    if (el) el.disabled = !enabled;
  });
}

function openWork(type) {
  currentType = type;
  $('chooser').classList.add('hidden');
  $('work').classList.remove('hidden');
  $('btSection').classList.toggle('hidden', type !== 'bt');
  $('usbSection').classList.toggle('hidden', type !== 'usb');
  $('netSection').classList.toggle('hidden', type !== 'net');
  if (type === 'bt') refreshPorts('bt');
  if (type === 'usb') refreshPorts('usb');
}

function openChooser() {
  currentType = null;
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
window.addEventListener('focus', () => refreshBtStatus());

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

async function refreshPorts(kind) {
  setControls(false);
  showStatus('Buscando portas...');
  await new Promise((r) => setTimeout(r, 800));
  try {
    const ports = await window.tipprint.listPorts();
    renderPorts(kind === 'bt' ? 'portsList' : 'portsListUsb', ports);
    showStatus(ports.length
      ? 'Portas encontradas: ' + ports.map((p) => p.path).join(', ')
      : 'Nenhuma porta encontrada.');
  } catch (e) {
    showStatus('Falha ao listar portas: ' + e.message);
  } finally {
    setControls(true);
  }
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
  } catch (e) {
    showStatus('Falha ao conectar: ' + e.message);
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
