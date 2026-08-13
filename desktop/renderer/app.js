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

function tagFor(port) {
  const hay = ((port.friendlyName || '') + ' ' + (port.pnpId || '') + ' ' + (port.manufacturer || '')).toLowerCase();
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
