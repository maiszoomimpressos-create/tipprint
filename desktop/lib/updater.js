'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');

const BASE_URL = 'https://tipprint.vercel.app';
const UPDATE_URL = BASE_URL + '/update-windows.json';

function parseVersion(v) {
  return String(v || '').split('.').map((s) => parseInt(s, 10) || 0);
}

function isNewer(candidate, current) {
  const a = parseVersion(candidate);
  const b = parseVersion(current);
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const x = a[i] || 0;
    const y = b[i] || 0;
    if (x !== y) return x > y;
  }
  return false;
}

async function checkForUpdate(currentVersion) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 10000);
  try {
    const res = await fetch(UPDATE_URL, { signal: controller.signal });
    if (!res.ok) return null;
    const info = await res.json();
    if (!info || !info.versionName || !isNewer(info.versionName, currentVersion)) {
      return null;
    }
    return {
      versionName: info.versionName,
      notes: info.notes || '',
      url: BASE_URL + (info.downloadPath || '/downloads/app-windows.exe')
    };
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

async function downloadInstaller(url, destPath) {
  const res = await fetch(url);
  if (!res.ok) throw new Error('HTTP ' + res.status);
  const buf = Buffer.from(await res.arrayBuffer());
  await fs.promises.writeFile(destPath, buf);
  return destPath;
}

function installerTempPath() {
  return path.join(os.tmpdir(), 'tipprint-update.exe');
}

module.exports = { checkForUpdate, downloadInstaller, installerTempPath, isNewer };
