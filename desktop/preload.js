const { contextBridge, ipcRenderer, clipboard } = require('electron');

contextBridge.exposeInMainWorld('tipprint', {
  appVersion: () => ipcRenderer.invoke('app-version'),
  listPorts: () => ipcRenderer.invoke('list-ports'),
  getLog: () => ipcRenderer.invoke('get-log'),
  copyText: (text) => clipboard.writeText(text),
  btStatus: () => ipcRenderer.invoke('bt-status'),
  btDevices: () => ipcRenderer.invoke('bt-devices'),
  btPair: (id) => ipcRenderer.invoke('bt-pair', id),
  openBtSettings: () => ipcRenderer.invoke('open-bt-settings'),
  connectSerial: (portPath, baud) => ipcRenderer.invoke('connect-serial', portPath, baud),
  connectNet: (host, port) => ipcRenderer.invoke('connect-net', host, port),
  disconnect: () => ipcRenderer.invoke('disconnect'),
  printRaw: (base64) => ipcRenderer.invoke('print-raw', base64),
  printTest: (printerLabel) => ipcRenderer.invoke('print-test', printerLabel),
  updateCheck: () => ipcRenderer.invoke('update-check'),
  updateDownload: () => ipcRenderer.invoke('update-download'),
  updateInstall: (installerPath) => ipcRenderer.invoke('update-install', installerPath)
});
