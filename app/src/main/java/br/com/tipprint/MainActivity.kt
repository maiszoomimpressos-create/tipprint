package br.com.tipprint

import android.Manifest
import android.app.AlertDialog
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.hardware.usb.UsbManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.getSystemService
import br.com.tipprint.printer.BluetoothPrinter
import br.com.tipprint.printer.NetPrinter
import br.com.tipprint.printer.SampleReceipt
import br.com.tipprint.printer.UsbPrinter
import br.com.tipprint.printer.UsbPrinterManager
import br.com.tipprint.printer.printBytes
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : AppCompatActivity() {

    private lateinit var statusText: TextView
    private lateinit var bluetoothSpinner: Spinner
    private lateinit var ipInput: EditText
    private lateinit var portInput: EditText
    private lateinit var connectBluetooth: Button
    private lateinit var discoverBluetooth: Button
    private lateinit var connectUsb: Button
    private lateinit var connectNet: Button
    private lateinit var printTest: Button
    private lateinit var settingsButton: Button

    private val mainHandler = Handler(Looper.getMainLooper())
    private val scope = CoroutineScope(Dispatchers.Default)

    private val bluetoothAdapter: BluetoothAdapter? by lazy {
        getSystemService<BluetoothManager>()?.adapter
    }

    private val usbManager: UsbManager? by lazy { getSystemService() }

    private var activePrinter: String? = null

    private var pendingPairDevice: BluetoothDevice? = null

    private val discoveredDevices = mutableListOf<BluetoothDevice>()

    private var discoveryInProgress = false

    private var foundDialog: AlertDialog? = null

    private val bluetoothPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
            if (allGranted(it)) fillBluetoothDevices() else showStatus(getString(R.string.bluetooth_permission_denied))
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        statusText = findViewById(R.id.statusText)
        bluetoothSpinner = findViewById(R.id.bluetoothSpinner)
        ipInput = findViewById(R.id.ipInput)
        portInput = findViewById(R.id.portInput)
        connectBluetooth = findViewById(R.id.connectBluetooth)
        discoverBluetooth = findViewById(R.id.discoverBluetooth)
        connectUsb = findViewById(R.id.connectUsb)
        connectNet = findViewById(R.id.connectNet)
        printTest = findViewById(R.id.printTest)
        settingsButton = findViewById(R.id.settingsButton)

        connectBluetooth.setOnClickListener { connectBluetoothClicked() }
        discoverBluetooth.setOnClickListener { discoverBluetoothClicked() }
        connectUsb.setOnClickListener { connectUsbClicked() }
        connectNet.setOnClickListener { connectNetClicked() }
        printTest.setOnClickListener { printTestClicked() }
        settingsButton.setOnClickListener {
            startActivity(Intent(this, SettingsActivity::class.java))
        }

        requestBluetoothPermission()
        checkForUpdate()
    }

    private fun checkForUpdate() {
        scope.launch {
            val info = UpdateChecker.check(this@MainActivity)
            if (info == null) return@launch
            withContext(Dispatchers.Main) {
                if (UpdateChecker.autoUpdateEnabled(this@MainActivity)) {
                    downloadAndInstall(info.apkUrl)
                } else {
                    AlertDialog.Builder(this@MainActivity)
                        .setTitle(R.string.update_available_title)
                        .setMessage(getString(R.string.update_available_message, info.versionName, info.notes))
                        .setPositiveButton(R.string.update_button) { _, _ -> downloadAndInstall(info.apkUrl) }
                        .setNegativeButton(R.string.update_later, null)
                        .show()
                }
            }
        }
    }

    private fun downloadAndInstall(url: String) {
        showStatus(getString(R.string.update_downloading))
        scope.launch {
            val apk = UpdateChecker.downloadApk(this@MainActivity, url)
            withContext(Dispatchers.Main) {
                when {
                    apk == null -> showStatus(getString(R.string.update_download_failed))
                    !UpdateChecker.installApk(this@MainActivity, apk) ->
                        showStatus(getString(R.string.update_open_installer_failed))
                    else -> showStatus(getString(R.string.update_waiting_install))
                }
            }
        }
    }

    private fun allGranted(result: Map<String, Boolean>) = result.values.all { it }

    private fun requestBluetoothPermission() {
        val needed = mutableListOf<String>()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            needed += Manifest.permission.BLUETOOTH_CONNECT
            needed += Manifest.permission.BLUETOOTH_SCAN
        } else {
            needed += Manifest.permission.ACCESS_FINE_LOCATION
        }
        val pending = needed.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (pending.isNotEmpty()) {
            bluetoothPermissionLauncher.launch(pending.toTypedArray())
            return
        }
        fillBluetoothDevices()
    }

    private fun fillBluetoothDevices() {
        val adapter = bluetoothAdapter
        if (adapter == null) {
            fillBluetoothSpinner(emptyList())
            showStatus(getString(R.string.bluetooth_unavailable))
            return
        }
        runCatching {
            adapter.bondedDevices
        }.onSuccess { devices ->
            fillBluetoothSpinner(devices.sortedBy { it.name })
            if (devices.isEmpty()) showStatus(getString(R.string.bluetooth_no_devices))
        }.onFailure {
            fillBluetoothSpinner(emptyList())
            showStatus(getString(R.string.bluetooth_permission_denied))
        }
    }

    private fun fillBluetoothSpinner(devices: List<BluetoothDevice>) {
        val labels = devices.map { "${it.name} - ${it.address}" }
        bluetoothSpinner.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, labels)
    }

    private fun connectBluetoothClicked() {
        val adapter = bluetoothAdapter ?: return showStatus(getString(R.string.bluetooth_unavailable))
        val devices = runCatching { adapter.bondedDevices }.getOrNull()?.sortedBy { it.name } ?: emptyList()
        if (devices.isEmpty()) {
            AlertDialog.Builder(this)
                .setTitle(R.string.bluetooth_no_paired_search)
                .setPositiveButton(R.string.discover_bluetooth) { _, _ -> discoverBluetoothClicked() }
                .setNegativeButton(R.string.cancel, null)
                .show()
            return
        }
        val position = bluetoothSpinner.selectedItemPosition.coerceIn(0, devices.lastIndex)
        connectToDevice(devices[position])
    }

    private fun connectToDevice(device: BluetoothDevice) {
        val adapter = bluetoothAdapter ?: return showStatus(getString(R.string.bluetooth_unavailable))
        showStatus(getString(R.string.connecting_bluetooth, device.name, device.address))
        scope.launch {
            val ok = tryOpenPrinter(adapter, device)
            withContext(Dispatchers.Main) {
                if (ok) {
                    activePrinter = device.address
                    savePrinter("bluetooth", device.address)
                    showStatus(getString(R.string.bluetooth_connected, device.name))
                } else {
                    activePrinter = null
                    showStatus(getString(R.string.connection_failed, resultException?.message ?: "erro"))
                }
            }
        }
    }

    private suspend fun tryOpenPrinter(adapter: BluetoothAdapter, device: BluetoothDevice): Boolean {
        var attempt = 1
        while (attempt <= 3) {
            val result = runCatching {
                val printer = BluetoothPrinter(adapter, device)
                printer.open()
                val ok = printer.checkStatus()
                printer.close()
                ok
            }
            if (result.isSuccess) return true
            resultException = result.exceptionOrNull()
            attempt++
            if (attempt <= 3) delay(1000)
        }
        return false
    }

    private var resultException: Throwable? = null

    private fun discoverBluetoothClicked() {
        val adapter = bluetoothAdapter ?: return showStatus(getString(R.string.bluetooth_unavailable))
        stopScanningAnimation()
        discoveredDevices.clear()
        foundDialog?.dismiss()
        foundDialog = null
        discoveryInProgress = true
        runCatching { adapter.startDiscovery() }
            .onFailure { discoveryInProgress = false; showStatus(getString(R.string.bluetooth_scan_failed)) }
            .onSuccess { showScanningStatus() }
    }

    private var scanningDots = 0
    private val scanningAnimation = object : Runnable {
        override fun run() {
            scanningDots = (scanningDots + 1) % 4
            statusText.text = getString(R.string.bluetooth_scanning) + ".".repeat(scanningDots)
            mainHandler.postDelayed(this, 400)
        }
    }

    private fun showScanningStatus() {
        scanningDots = 0
        statusText.text = getString(R.string.bluetooth_scanning)
        mainHandler.postDelayed(scanningAnimation, 400)
    }

    private fun stopScanningAnimation() {
        mainHandler.removeCallbacks(scanningAnimation)
    }

    private val discoveryReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            when (intent.action) {
                BluetoothDevice.ACTION_FOUND -> {
                    val device = intent.bluetoothDeviceExtra()
                    if (device != null && discoveryInProgress && !discoveredDevices.any { it.address == device.address }) {
                        discoveredDevices += device
                    }
                }
                BluetoothAdapter.ACTION_DISCOVERY_FINISHED -> {
                    if (!discoveryInProgress) return
                    discoveryInProgress = false
                    stopScanningAnimation()
                    if (discoveredDevices.isEmpty()) {
                        showStatus(getString(R.string.bluetooth_none_found))
                        return
                    }
                    val names = discoveredDevices.map { it.name ?: it.address }
                    foundDialog?.dismiss()
                    foundDialog = AlertDialog.Builder(this@MainActivity)
                        .setTitle(R.string.bluetooth_found_title)
                        .setItems(names.toTypedArray()) { _, which -> pairWithDevice(discoveredDevices[which]) }
                        .setNegativeButton(R.string.cancel, null)
                        .show()
                }
                BluetoothDevice.ACTION_BOND_STATE_CHANGED -> {
                    if (intent.getIntExtra(BluetoothDevice.EXTRA_BOND_STATE, -1) == BluetoothDevice.BOND_BONDED) {
                        fillBluetoothDevices()
                        showStatus(getString(R.string.bluetooth_bonded))
                        val device = intent.bluetoothDeviceExtra()
                        val pending = pendingPairDevice
                        if (device != null && pending != null && pending.address == device.address) {
                            pendingPairDevice = null
                            connectToDevice(device)
                        }
                    }
                }
                BluetoothDevice.ACTION_PAIRING_REQUEST -> {
                    val device = intent.bluetoothDeviceExtra() ?: return
                    val pending = pendingPairDevice
                    if (pending == null || device.address != pending.address) return
                    runCatching { device.setPin("0000".toByteArray()) }
                }
            }
        }
    }

    private fun Intent.bluetoothDeviceExtra(): BluetoothDevice? =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            getParcelableExtra(BluetoothDevice.EXTRA_DEVICE, BluetoothDevice::class.java)
        } else {
            @Suppress("DEPRECATION")
            getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
        }

    private fun pairWithDevice(device: BluetoothDevice) {
        val adapter = bluetoothAdapter ?: return
        runCatching { adapter.cancelDiscovery() }
        val bonded = runCatching { device.bondState }.getOrDefault(BluetoothDevice.BOND_NONE) == BluetoothDevice.BOND_BONDED
        if (bonded) {
            connectToDevice(device)
            return
        }
        showStatus(getString(R.string.connecting_bluetooth, device.name, device.address))
        scope.launch {
            val ok = tryOpenPrinter(adapter, device)
            withContext(Dispatchers.Main) {
                if (ok) {
                    activePrinter = device.address
                    savePrinter("bluetooth", device.address)
                    showStatus(getString(R.string.bluetooth_connected, device.name))
                } else {
                    pendingPairDevice = device
                    showStatus(getString(R.string.bluetooth_pairing, device.name ?: device.address))
                    runCatching { device.createBond() }
                        .onSuccess { showStatus(getString(R.string.bluetooth_pairing, device.name ?: device.address)) }
                        .onFailure { showStatus(getString(R.string.bluetooth_pair_failed)) }
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        val filter = IntentFilter().apply {
            addAction(BluetoothDevice.ACTION_FOUND)
            addAction(BluetoothAdapter.ACTION_DISCOVERY_FINISHED)
            addAction(BluetoothDevice.ACTION_BOND_STATE_CHANGED)
            addAction(BluetoothDevice.ACTION_PAIRING_REQUEST)
            setPriority(IntentFilter.SYSTEM_HIGH_PRIORITY)
        }
        registerReceiver(discoveryReceiver, filter)
    }

    override fun onStop() {
        super.onStop()
        runCatching { bluetoothAdapter?.cancelDiscovery() }
        runCatching { unregisterReceiver(discoveryReceiver) }
        foundDialog?.dismiss()
        foundDialog = null
    }

    private fun connectUsbClicked() {
        val manager = usbManager ?: return showStatus(getString(R.string.usb_unavailable))
        val printers = UsbPrinterManager.listPrinters(manager)
        if (printers.isEmpty()) {
            showStatus(getString(R.string.usb_no_devices))
            return
        }
        if (printers.size == 1) {
            connectUsbDevice(manager, printers.first())
            return
        }
        val names = printers.map { it.productName ?: it.deviceName }
        AlertDialog.Builder(this)
            .setTitle(R.string.select_printer)
            .setItems(names.toTypedArray()) { _, which -> connectUsbDevice(manager, printers[which]) }
            .show()
    }

    private fun connectUsbDevice(manager: UsbManager, device: android.hardware.usb.UsbDevice) {
        if (!manager.hasPermission(device)) {
            showStatus(getString(R.string.usb_requesting_permission))
            scope.launch {
                val granted = UsbPrinterManager.requestPermission(this@MainActivity, manager, device).await()
                withContext(Dispatchers.Main) {
                    if (granted) connectUsbDeviceAfterPermission(device) else showStatus(getString(R.string.usb_permission_denied))
                }
            }
        }
    }

    private fun connectUsbDeviceAfterPermission(device: android.hardware.usb.UsbDevice) {
        showStatus(getString(R.string.connecting_usb, device.productName ?: device.deviceName))
        scope.launch {
            val result = runCatching {
                val printer = UsbPrinter(usbManager!!, device)
                printer.open()
                val ok = printer.checkStatus()
                printer.close()
                ok
            }
            withContext(Dispatchers.Main) {
                if (result.isSuccess) {
                    activePrinter = "usb:${device.deviceId}"
                    savePrinter("usb", device.deviceId.toString())
                    showStatus(getString(R.string.usb_connected, device.productName ?: device.deviceName))
                } else {
                    activePrinter = null
                    showStatus(getString(R.string.connection_failed, result.exceptionOrNull()?.message ?: "erro"))
                }
            }
        }
    }

    private fun connectNetClicked() {
        val host = ipInput.text.toString().trim()
        if (host.isEmpty()) {
            showStatus(getString(R.string.net_enter_ip))
            return
        }
        val port = portInput.text.toString().toIntOrNull() ?: 9100
        val target = "$host:$port"
        showStatus(getString(R.string.connecting_net, target))
        scope.launch {
            val result = runCatching {
                val printer = NetPrinter(host, port)
                printer.open()
                val ok = printer.checkStatus()
                printer.close()
                ok
            }
            withContext(Dispatchers.Main) {
                if (result.isSuccess) {
                    activePrinter = target
                    savePrinter("net", target)
                    showStatus(getString(R.string.net_connected, target))
                } else {
                    activePrinter = null
                    showStatus(getString(R.string.connection_failed, result.exceptionOrNull()?.message ?: "erro"))
                }
            }
        }
    }

    private fun printTestClicked() {
        val target = activePrinter ?: return showStatus(getString(R.string.no_printer_selected))
        showStatus(getString(R.string.printing))
        scope.launch {
            val result = runCatching {
                val printer = when {
                    target.startsWith("usb:") -> UsbPrinter(usbManager!!, usbManager!!.deviceList.values.first { it.deviceId.toString() == target.removePrefix("usb:") })
                    target.contains(":") -> NetPrinter(target.substringBefore(":"), target.substringAfter(":").toInt())
                    else -> BluetoothPrinter(bluetoothAdapter!!, bluetoothAdapter!!.getRemoteDevice(target))
                }
                printBytes(printer, SampleReceipt.build(printer.name))
                true
            }
            withContext(Dispatchers.Main) {
                if (result.isSuccess) {
                    showStatus(getString(R.string.print_success))
                    Toast.makeText(this@MainActivity, R.string.print_success, Toast.LENGTH_SHORT).show()
                } else {
                    showStatus(getString(R.string.print_failed, result.exceptionOrNull()?.message ?: "erro"))
                }
            }
        }
    }

    private fun savePrinter(type: String, target: String) {
        getSharedPreferences("tipprint", MODE_PRIVATE).edit()
            .putString("printer_type", type)
            .putString("printer_target", target)
            .apply()
    }

    private fun showStatus(message: String) {
        mainHandler.post { statusText.text = message }
    }
}