const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('tipprint', {
  listPorts: () => ipcRenderer.invoke('list-ports'),
  connectSerial: (portPath, baud) => ipcRenderer.invoke('connect-serial', portPath, baud),
  connectNet: (host, port) => ipcRenderer.invoke('connect-net', host, port),
  disconnect: () => ipcRenderer.invoke('disconnect'),
  printRaw: (base64) => ipcRenderer.invoke('print-raw', base64),
  printTest: (printerLabel) => ipcRenderer.invoke('print-test', printerLabel)
});
