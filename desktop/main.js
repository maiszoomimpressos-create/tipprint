const { app, BrowserWindow, ipcMain } = require('electron');
const path = require('path');
const os = require('os');
const { execFile, spawn } = require('child_process');
const { SerialPort } = require('serialport');
const net = require('net');
const { buildTestReceipt } = require('./lib/escpos');
const { checkForUpdate, downloadInstaller, installerTempPath } = require('./lib/updater');

const DISPLAY_VERSION = require('./package.json').displayVersion || '1.0.5.0.0.0';

const LOG_PATH = path.join(os.tmpdir(), 'tipprint.log');

function log(...args) {
  try {
    const line = '[' + new Date().toISOString() + '] ' + args.join(' ') + '\n';
    require('fs').appendFileSync(LOG_PATH, line);
  } catch {
    // nunca deixar o log derrubar o app
  }
}

let win = null;
let active = null; // { kind: 'serial' | 'net', port?: SerialPort, socket?: net.Socket }

app.whenReady().then(() => {
  log('APP iniciado, versao ' + DISPLAY_VERSION + ', plataforma ' + process.platform);
  const fs = require('fs');
  try {
    if (fs.existsSync(LOG_PATH) && fs.statSync(LOG_PATH).size > 300 * 1024) {
      fs.unlinkSync(LOG_PATH);
    }
  } catch {}
});

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
  log('list-ports -> ' + JSON.stringify(ports.map((p) => ({ path: p.path, pnpId: p.pnpId || null }))));
  return ports.map((p) => ({
    path: p.path,
    manufacturer: p.manufacturer || null,
    friendlyName: p.friendlyName || null,
    pnpId: p.pnpId || null,
    productId: p.productId || null,
    vendorId: p.vendorId || null
  }));
});

ipcMain.handle('get-log', async () => {
  try {
    return require('fs').readFileSync(LOG_PATH, 'utf8').slice(-120000);
  } catch {
    return '';
  }
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

function runBtWatcher(seconds) {
  return new Promise((resolve) => {
    execFile(
      path.join(__dirname, 'lib', 'scripts', 'bt-watcher.exe'),
      [String(seconds)],
      { timeout: 40000, windowsHide: true },
      (err, stdout) => {
        if (err) return resolve([]);
        const out = [];
        String(stdout).split(/\r?\n/).forEach((line) => {
          const parts = line.split('\t');
          if (parts.length < 3 || !parts[1]) return;
          const macParts = parts[1].split('-');
          const mac = (macParts[macParts.length - 1] || '').replace(/:/g, '').toUpperCase();
          if (mac) out.push({ Name: parts[0], Id: parts[1], Mac: mac, Paired: parts[2] === '1' });
        });
        resolve(out);
      }
    );
  });
}

ipcMain.handle('bt-devices', async () => {
  const known = await runBtScanFile();
  const scanned = await runBtWatcher(10);
  log('bt-devices known(raw)=' + JSON.stringify(known));
  log('bt-devices scanned(raw)=' + JSON.stringify(scanned));
  const map = {};
  (known || []).forEach((d) => {
    const mac = String(d.Address || '').replace(/:/g, '').toUpperCase();
    if (!mac) return;
    map[mac] = { Name: d.Name || '', Address: d.Address, Paired: true };
  });
  scanned.forEach((d) => {
    if (!map[d.Mac]) {
      map[d.Mac] = { Name: d.Name || '', Address: d.Mac, Id: d.Id, Paired: d.Paired };
    } else {
      if (!map[d.Mac].Id) map[d.Mac].Id = d.Id;
      if (d.Name && !map[d.Mac].Name) map[d.Mac].Name = d.Name;
    }
  });
  const merged = Object.values(map);
  log('bt-devices merged -> ' + JSON.stringify(merged));
  return merged;
});

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

function runPsLong(script, timeoutMs) {
  return new Promise((resolve) => {
    execFile(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-Command', script],
      { timeout: timeoutMs, windowsHide: true },
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
  log('bt-pair chamado id=' + safeId);
  const cmd = "$id = '" + safeId + "'; " + BT_PAIR_SCRIPT.replace('param($id)\n', '');
  for (let attempt = 1; attempt <= 2; attempt++) {
    try {
      const result = await runPsLong(cmd, 95000);
      const status = result && result[0] ? result[0].Status : 'Failed';
      log('bt-pair tentativa ' + attempt + ' -> ' + status);
      if (status === 'Paired' || status === 'AlreadyPaired') return status;
      if (status !== 'ConnectionRejected' && status !== 'AuthenticationFailure' && status !== 'Failed') {
        return status;
      }
    } catch (e) {
      log('bt-pair tentativa ' + attempt + ' EXCECAO: ' + (e && e.message));
    }
    await new Promise((r) => setTimeout(r, 3000));
  }
  return 'Failed';
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
  log('connect-serial iniciando port=' + portPath + ' baud=' + baud);
  try {
    await connectSerial(portPath, baud);
    log('connect-serial SUCESSO em ' + portPath);
    return { ok: true };
  } catch (e) {
    log('connect-serial FALHOU em ' + portPath + ' -> ' + (e && e.message));
    throw e;
  }
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
