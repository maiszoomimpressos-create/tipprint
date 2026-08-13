const { app, BrowserWindow, ipcMain } = require('electron');
const path = require('path');
const { SerialPort } = require('serialport');
const net = require('net');
const { buildTestReceipt } = require('./lib/escpos');

let win = null;
let active = null; // { kind: 'serial' | 'net', port?: SerialPort, socket?: net.Socket }

function createWindow() {
  win = new BrowserWindow({
    width: 430,
    height: 860,
    minWidth: 380,
    minHeight: 600,
    title: 'TipPrint',
    backgroundColor: '#11161D',
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false
    }
  });
  win.loadFile(path.join(__dirname, 'renderer', 'index.html'));
}

function writeActive(buffer) {
  if (!active) throw new Error('Nenhuma conexao ativa');
  return new Promise((resolve, reject) => {
    if (active.kind === 'serial') {
      active.port.write(buffer, (err) => (err ? reject(err) : resolve()));
    } else {
      active.socket.write(buffer, (err) => (err ? reject(err) : resolve()));
    }
  });
}

async function connectSerial(portPath, baud) {
  await disconnect();
  const port = new SerialPort({ path: portPath, baudRate: baud, autoOpen: false });
  await new Promise((resolve, reject) => port.open((err) => (err ? reject(err) : resolve())));
  active = { kind: 'serial', port };
}

async function connectNet(host, port) {
  await disconnect();
  const socket = net.connect({ host, port });
  await new Promise((resolve, reject) => {
    socket.once('connect', resolve);
    socket.once('error', reject);
  });
  socket.removeAllListeners('error');
  active = { kind: 'net', socket };
}

function disconnect() {
  return new Promise((resolve) => {
    if (!active) return resolve();
    const old = active;
    active = null;
    const finish = () => resolve();
    if (old.kind === 'serial') {
      if (old.port.isOpen) old.port.close(finish); else finish();
    } else {
      old.socket.end(finish);
    }
  });
}

ipcMain.handle('list-ports', async () => {
  const ports = await SerialPort.list();
  return ports.map((p) => ({
    path: p.path,
    manufacturer: p.manufacturer || null,
    friendlyName: p.friendlyName || null,
    pnpId: p.pnpId || null,
    productId: p.productId || null,
    vendorId: p.vendorId || null
  }));
});

ipcMain.handle('connect-serial', async (_e, portPath, baud) => {
  await connectSerial(portPath, baud);
  return { ok: true };
});

ipcMain.handle('connect-net', async (_e, host, port) => {
  await connectNet(host, port);
  return { ok: true };
});

ipcMain.handle('disconnect', async () => {
  await disconnect();
  return { ok: true };
});

ipcMain.handle('print-raw', async (_e, base64) => {
  await writeActive(Buffer.from(base64, 'base64'));
  return { ok: true };
});

ipcMain.handle('print-test', async (_e, printerLabel) => {
  const bytes = buildTestReceipt(printerLabel || 'TIPO7');
  await writeActive(bytes);
  return { ok: true, bytes: bytes.length };
});

app.whenReady().then(() => {
  createWindow();
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.on('before-quit', () => {
  if (active) active.kind === 'serial' ? active.port.close() : active.socket.destroy();
});
