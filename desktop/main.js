const { app, BrowserWindow, ipcMain } = require('electron');
const path = require('path');
const os = require('os');
const { execFile, spawn } = require('child_process');
const { SerialPort } = require('serialport');
const net = require('net');
const { buildTestReceipt } = require('./lib/escpos');
const { checkForUpdate, downloadInstaller, installerTempPath } = require('./lib/updater');

const DISPLAY_VERSION = require('./package.json').displayVersion || '1.0.5.0.0.0';

let win = null;
let active = null; // { kind: 'serial' | 'net', port?: SerialPort, socket?: net.Socket }

function createWindow() {
  win = new BrowserWindow({
    width: 430,
    height: 860,
    minWidth: 380,
    minHeight: 600,
    title: 'TipPrint · ' + DISPLAY_VERSION,
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
  let lastErr;
  for (let attempt = 1; attempt <= 4; attempt++) {
    const port = new SerialPort({ path: portPath, baudRate: baud, autoOpen: false });
    try {
      await new Promise((resolve, reject) => port.open((err) => (err ? reject(err) : resolve())));
      active = { kind: 'serial', port };
      return;
    } catch (e) {
      lastErr = e;
      if (port.isOpen) port.close(() => {});
      if (attempt < 4) await new Promise((r) => setTimeout(r, 1500));
    }
  }
  throw lastErr;
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

const BT_RADIO_SCRIPT = `
$radios = @(Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'Adapter|Wireless' })
$srv = Get-Service bthserv -ErrorAction SilentlyContinue
$on = ($srv.Status -eq 'Running')
$radios | ForEach-Object {
  $state = if ($_.Status -ne 'OK') { 'Disabled' } elseif ($on) { 'On' } else { 'Off' }
  [PSCustomObject]@{ Name = $_.FriendlyName; State = $state }
} | ConvertTo-Json -Compress
`;

function getBtRadios() {
  return new Promise((resolve) => {
    execFile(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-Command', BT_RADIO_SCRIPT],
      { timeout: 20000, windowsHide: true },
      (err, stdout) => {
        if (err) return resolve([]);
        try {
          const parsed = JSON.parse(stdout.trim());
          resolve(Array.isArray(parsed) ? parsed : []);
        } catch {
          resolve([]);
        }
      }
    );
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

ipcMain.handle('app-version', async () => DISPLAY_VERSION);

ipcMain.handle('update-check', async () => checkForUpdate(DISPLAY_VERSION));

ipcMain.handle('update-download', async () => {
  const info = await checkForUpdate(DISPLAY_VERSION);
  if (!info) return { ok: false, error: 'sem atualizacao' };
  try {
    const dest = installerTempPath();
    await downloadInstaller(info.url, dest);
    return { ok: true, path: dest };
  } catch (e) {
    return { ok: false, error: e.message };
  }
});

ipcMain.handle('update-install', async (_e, installerPath) => {
  spawn(installerPath, [], { detached: true, stdio: 'ignore' }).unref();
  setTimeout(() => app.quit(), 500);
  return { ok: true };
});

function runBtScanFile() {
  return new Promise((resolve) => {
    execFile(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
        path.join(__dirname, 'lib', 'scripts', 'bt-scan.ps1')],
      { timeout: 30000, windowsHide: true },
      (err, stdout) => {
        if (err) return resolve([]);
        try {
          const parsed = JSON.parse(stdout.trim());
          resolve(Array.isArray(parsed) ? parsed : []);
        } catch {
          resolve([]);
        }
      }
    );
  });
}

ipcMain.handle('bt-devices', async () => runBtScanFile());

const BT_PAIR_SCRIPT = `
param($id)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[void][Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType=WindowsRuntime]
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation\`1' })[0]
function Await($op, $resultType) {
  $task = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($op))
  $task.Wait()
  $task.Result
}
$di = Await ([Windows.Devices.Enumeration.DeviceInformation]::CreateFromIdAsync($id)) ([Windows.Devices.Enumeration.DeviceInformation])
$res = Await ($di.Pairing.PairAsync()) ([Windows.Devices.Enumeration.DevicePairingResult])
[PSCustomObject]@{ Status = $res.Status.ToString() } | ConvertTo-Json -Compress
`;

function runPs(script) {
  return new Promise((resolve) => {
    execFile(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-Command', script],
      { timeout: 30000, windowsHide: true },
      (err, stdout) => {
        if (err) return resolve(null);
        try {
          const parsed = JSON.parse(stdout.trim());
          resolve(Array.isArray(parsed) ? parsed : [parsed]);
        } catch {
          resolve(null);
        }
      }
    );
  });
}

ipcMain.handle('bt-pair', async (_e, id) => {
  const safeId = String(id).replace(/'/g, '');
  const result = await runPs("$id = '" + safeId + "'; " + BT_PAIR_SCRIPT.replace('param($id)\n', ''));
  return result && result[0] ? result[0].Status : 'Failed';
});

ipcMain.handle('bt-status', async () => getBtRadios());

ipcMain.handle('open-bt-settings', async () => {
  spawn('cmd.exe', ['/c', 'start', '', 'ms-settings:bluetooth'], {
    detached: true,
    stdio: 'ignore'
  }).unref();
  return { ok: true };
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
