const { app, BrowserWindow, ipcMain, clipboard } = require('electron');
const path = require('path');
const os = require('os');
const fs = require('fs');
const { execFile, spawn } = require('child_process');
const { SerialPort } = require('serialport');
const net = require('net');
const { buildTestReceipt } = require('./lib/escpos');
const { checkForUpdate, downloadInstaller, installerTempPath } = require('./lib/updater');
const { createClient } = require('@supabase/supabase-js');
// O processo main do Electron nao tem WebSocket nativo (so no navegador/renderer) - o
// cliente Supabase tenta montar um canal de Realtime na criacao mesmo sem usar, e quebra
// sem isso. Nao usamos Realtime (so Auth + REST), mas precisa desse polyfill pra nem
// dar erro no createClient.
global.WebSocket = require('ws');

// Login do TipPrint Desktop: e' do CLIENTE que contrata a API do TipPrint pra usar no
// site/produto dele (ex: dono do Tipo7), nao do atendente do caixa. Usa Supabase Auth
// (mesmo banco do backend/, ver backend/server.js e a tabela tipprint_systems).
// Chave anon e' segura pra embutir aqui - e' feita pra rodar em app cliente, protegida
// por RLS no banco (cada usuario so ve/mexe nos proprios sistemas).
const SUPABASE_URL = 'https://hjusqwsqykdzkftyeykd.supabase.co';
const SUPABASE_ANON_KEY = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImhqdXNxd3NxeWtkemtmdHlleWtkIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODY3MjY3OTksImV4cCI6MjEwMjMwMjc5OX0.5v9BqoyRowJriBbwL_TKF4jTKosQRMtGUo3HrGJ_Z3M';
const SESSION_PATH = path.join(process.env.LOCALAPPDATA || '', 'TipPrint', 'session.json');

// supabase-js espera um storage tipo localStorage (getItem/setItem/removeItem); no
// processo main do Electron nao existe isso, entao guardamos num arquivo local. E' assim
// que o login sobrevive a fechar/abrir o app de novo.
const fileAuthStorage = {
  getItem: (key) => {
    try {
      const all = JSON.parse(fs.readFileSync(SESSION_PATH, 'utf8'));
      return all[key] ?? null;
    } catch { return null; }
  },
  setItem: (key, value) => {
    try {
      fs.mkdirSync(path.dirname(SESSION_PATH), { recursive: true });
      let all = {};
      try { all = JSON.parse(fs.readFileSync(SESSION_PATH, 'utf8')); } catch {}
      all[key] = value;
      fs.writeFileSync(SESSION_PATH, JSON.stringify(all));
    } catch {}
  },
  removeItem: (key) => {
    try {
      let all = {};
      try { all = JSON.parse(fs.readFileSync(SESSION_PATH, 'utf8')); } catch {}
      delete all[key];
      fs.writeFileSync(SESSION_PATH, JSON.stringify(all));
    } catch {}
  }
};

const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY, {
  auth: { storage: fileAuthStorage, persistSession: true, autoRefreshToken: true, detectSessionInUrl: false }
});

// TipPrint Desktop Agent: quem tem acesso fisico a impressora (COM/USB/BT) agora e o
// PrintServer (localhost:8080), nao o Electron. O Electron so fala com ele por HTTP,
// pra nao disputar a mesma porta COM com o Agent (era a causa do "Access denied").
// Conexao de rede (TCP direto pra impressora) continua sendo feita aqui mesmo, pois
// nao usa porta COM e nao tem esse conflito.
const AGENT_BASE = 'http://localhost:8080';
const AGENT_EXE = path.join(process.env.LOCALAPPDATA || '', 'TipPrint', 'PrintServer.exe');
const AGENT_LOG = path.join(process.env.LOCALAPPDATA || '', 'TipPrint', 'printserver.log');

async function agentFetch(urlPath, opts) {
  let res;
  try {
    res = await fetch(AGENT_BASE + urlPath, { ...opts, signal: AbortSignal.timeout(8000) });
  } catch (e) {
    throw new Error('TipPrint Desktop Agent nao respondeu em localhost:8080 (' + (e.message || e) + ')');
  }
  const body = await res.json().catch(() => ({}));
  if (!body.ok) throw new Error(body.error || ('Agent respondeu ' + res.status));
  return body;
}

// Se o Agent nao estiver rodando, tenta subir sozinho (mesmo binario que o instalador
// do PrintServer coloca em %LOCALAPPDATA%\TipPrint). Best-effort: nunca lanca excecao.
async function ensureAgentRunning() {
  try {
    await agentFetch('/status');
    return true;
  } catch {
    // segue para tentar iniciar
  }
  try {
    if (!fs.existsSync(AGENT_EXE)) return false;
    log('Agent nao respondeu - iniciando automaticamente: ' + AGENT_EXE);
    spawn(AGENT_EXE, ['8080', '9100', AGENT_LOG], {
      detached: true,
      stdio: 'ignore',
      cwd: path.dirname(AGENT_EXE)
    }).unref();
    await new Promise((r) => setTimeout(r, 1500));
    return true;
  } catch (e) {
    log('Falha ao iniciar o Agent automaticamente: ' + (e && e.message));
    return false;
  }
}

async function agentConnect(printerId) {
  try {
    return await agentFetch('/connect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ printer: printerId })
    });
  } catch (e) {
    if (await ensureAgentRunning()) {
      return agentFetch('/connect', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ printer: printerId })
      });
    }
    throw e;
  }
}

async function agentPrintRaw(base64) {
  return agentFetch('/print', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ mode: 'raw', data: base64 })
  });
}

const DISPLAY_VERSION = require('./package.json').displayVersion || '1.0.5.0.0.0';

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (win) {
      if (win.isMinimized()) win.restore();
      win.focus();
    }
  });
}

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
let active = null; // { kind: 'net', socket: net.Socket } - so impressora de rede fica aberta aqui
let agentConnected = false; // true quando a impressora ativa e' gerenciada pelo Agent (COM/USB/BT/Windows)

app.whenReady().then(() => {
  log('APP iniciado, versao ' + DISPLAY_VERSION + ', plataforma ' + process.platform);
  ensureAgentRunning().catch(() => {});
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
    icon: path.join(__dirname, 'renderer', 'icon.ico'),
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
  if (!active || active.kind !== 'net') throw new Error('Nenhuma conexao de rede ativa');
  return new Promise((resolve, reject) => {
    active.socket.write(buffer, (err) => (err ? reject(err) : resolve()));
  });
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
  // Nao chamamos o Agent aqui de proposito: ele deve continuar conectado a impressora
  // mesmo com a janela do TipPrint fechada, porque e' ele quem atende o tipo7.com.
  agentConnected = false;
  return new Promise((resolve) => {
    if (!active) return resolve();
    const old = active;
    active = null;
    old.socket.end(() => resolve());
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

ipcMain.handle('copy-text', (_e, text) => {
  clipboard.writeText(String(text));
  return { ok: true };
});

function runBtRepair(mac) {
  return new Promise((resolve) => {
    execFile(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
        path.join(__dirname, 'lib', 'scripts', 'bt-repair.ps1'), '-mac', String(mac)],
      { timeout: 120000, windowsHide: true },
      (err, stdout) => {
        if (err) return resolve({ ok: false, error: String(err.message || err), raw: String(stdout) });
        try {
          resolve(JSON.parse(stdout.trim()));
        } catch {
          resolve({ ok: false, error: 'saida invalida do reparo', raw: String(stdout) });
        }
      }
    );
  });
}

ipcMain.handle('bt-repair', async (_e, mac) => {
  log('bt-repair iniciando mac=' + mac);
  const r = await runBtRepair(mac);
  log('bt-repair resultado=' + JSON.stringify(r));
  return r;
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

// Diagnostico de hardware/driver do adaptador Bluetooth - ver bt-adapter-check.ps1 pro
// porque disso existir (caso real: RFCOMM com "Codigo 10", pareamento nao tinha nada a ver).
function runBtAdapterCheck() {
  return new Promise((resolve) => {
    execFile(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
        path.join(__dirname, 'lib', 'scripts', 'bt-adapter-check.ps1')],
      { timeout: 15000, windowsHide: true },
      (err, stdout) => {
        if (err) return resolve({ Problems: [], AdapterCount: 0, Adapters: [] });
        try {
          const parsed = JSON.parse(stdout.trim());
          resolve(parsed && typeof parsed === 'object' ? parsed : { Problems: [], AdapterCount: 0, Adapters: [] });
        } catch {
          resolve({ Problems: [], AdapterCount: 0, Adapters: [] });
        }
      }
    );
  });
}

ipcMain.handle('bt-adapter-check', async () => {
  const result = await runBtAdapterCheck();
  log('bt-adapter-check -> ' + JSON.stringify(result));
  return result;
});

function mapKnownDevices(known) {
  const map = {};
  (known || []).forEach((d) => {
    const mac = String(d.Address || '').replace(/:/g, '').toUpperCase();
    if (!mac) return;
    map[mac] = { Name: d.Name || '', Address: d.Address, Paired: true, Kind: 'classic' };
  });
  return map;
}

// Rapido (sem varredura de radio, so WinRT/registro): usado ao abrir a tela e ao voltar o
// foco pro app, pra listar so quem ja esta pareado sem prender o usuario ~10s toda vez.
ipcMain.handle('bt-known-devices', async () => {
  const known = await runBtScanFile();
  const map = mapKnownDevices(known);
  const merged = Object.values(map).filter((d) => d.Paired);
  log('bt-known-devices -> ' + JSON.stringify(merged));
  return merged;
});

// Lento (~10s, varredura de radio ativa via bt-watcher.exe): so roda quando o usuario pede
// explicitamente "Procurar nova impressora" - acha tambem quem ainda nao pareou (BLE/novo).
ipcMain.handle('bt-devices', async () => {
  const known = await runBtScanFile();
  const scanned = await runBtWatcher(10);
  log('bt-devices known(raw)=' + JSON.stringify(known));
  log('bt-devices scanned(raw)=' + JSON.stringify(scanned));
  const map = mapKnownDevices(known);
  scanned.forEach((d) => {
    const isBle = String(d.Id || '').startsWith('BluetoothLE');
    if (!map[d.Mac]) {
      map[d.Mac] = { Name: d.Name || '', Address: d.Mac, Id: d.Id, Paired: d.Paired, Kind: isBle ? 'ble' : 'classic' };
    } else {
      if (isBle) {
        if (d.Name && !map[d.Mac].Name) map[d.Mac].Name = d.Name;
      } else {
        map[d.Mac].Kind = 'classic';
        if (d.Id) map[d.Mac].Id = d.Id;
        if (d.Name && !map[d.Mac].Name) map[d.Mac].Name = d.Name;
      }
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

ipcMain.handle('connect-serial', async (_e, portPath) => {
  log('connect-serial (via Agent) iniciando port=' + portPath);
  try {
    await disconnect(); // solta uma eventual conexao de rede antes de trocar pro Agent
    await agentConnect(portPath);
    agentConnected = true;
    log('connect-serial SUCESSO em ' + portPath + ' (Agent)');
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
  if (active && active.kind === 'net') {
    await writeActive(Buffer.from(base64, 'base64'));
    return { ok: true };
  }
  if (!agentConnected) throw new Error('Nenhuma impressora conectada.');
  return agentPrintRaw(base64);
});

ipcMain.handle('print-test', async (_e, printerLabel) => {
  const bytes = buildTestReceipt(printerLabel || 'TIPO7');
  if (active && active.kind === 'net') {
    await writeActive(bytes);
    return { ok: true, bytes: bytes.length };
  }
  if (!agentConnected) throw new Error('Nenhuma impressora conectada.');
  const r = await agentPrintRaw(bytes.toString('base64'));
  return { ok: true, bytes: r.bytes != null ? r.bytes : bytes.length };
});

// ---------- Login (conta do cliente que contrata a API do TipPrint) ----------

ipcMain.handle('auth-signup', async (_e, email, password) => {
  const { data, error } = await supabase.auth.signUp({ email, password });
  if (error) return { ok: false, error: error.message };
  // Se a confirmacao por e-mail estiver ligada no projeto, session vem nula aqui -
  // o usuario so consegue entrar depois de confirmar o e-mail.
  return { ok: true, needsEmailConfirm: !data.session, email: data.user ? data.user.email : email };
});

ipcMain.handle('auth-login', async (_e, email, password) => {
  const { data, error } = await supabase.auth.signInWithPassword({ email, password });
  if (error) return { ok: false, error: error.message };
  return { ok: true, email: data.user.email };
});

ipcMain.handle('auth-logout', async () => {
  await supabase.auth.signOut();
  return { ok: true };
});

ipcMain.handle('auth-status', async () => {
  const { data } = await supabase.auth.getSession();
  if (!data.session) return { ok: true, loggedIn: false };
  return { ok: true, loggedIn: true, email: data.session.user.email };
});

// ---------- Sistemas do cliente (chaves de API pros produtos dele) ----------
// Passa pelo RLS do Supabase (nao pelo backend/service_role) - cada usuario logado so
// ve/mexe nos proprios registros em tipprint_systems, garantido pelo banco em si.

ipcMain.handle('systems-list', async () => {
  const { data, error } = await supabase
    .from('tipprint_systems')
    .select('id, name, api_key, status, allowed_origins, created_at')
    .order('created_at', { ascending: false });
  if (error) return { ok: false, error: error.message };
  return { ok: true, systems: data };
});

ipcMain.handle('systems-create', async (_e, name) => {
  const { data: userData } = await supabase.auth.getUser();
  if (!userData.user) return { ok: false, error: 'Faca login primeiro.' };
  const raw = require('crypto').randomBytes(24).toString('hex');
  const api_key = 'tp_live_' + raw;
  const { data, error } = await supabase
    .from('tipprint_systems')
    .insert({ name, api_key, owner_id: userData.user.id })
    .select()
    .single();
  if (error) return { ok: false, error: error.message };
  return { ok: true, system: data };
});

ipcMain.handle('systems-revoke', async (_e, id) => {
  const { data, error } = await supabase
    .from('tipprint_systems')
    .update({ status: 'revoked', revoked_at: new Date().toISOString() })
    .eq('id', id)
    .select()
    .single();
  if (error) return { ok: false, error: error.message };
  return { ok: true, system: data };
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
  // So a conexao de rede e' nossa pra fechar. A impressora gerenciada pelo Agent
  // continua conectada de proposito (o Agent segue rodando e atendendo o tipo7.com).
  if (active && active.kind === 'net') active.socket.destroy();
});
