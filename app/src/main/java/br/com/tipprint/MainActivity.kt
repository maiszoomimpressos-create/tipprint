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
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.TextView
import android.widget.Toast
import android.view.LayoutInflater
import android.view.View
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
    private lateinit var btList: ListView
    private lateinit var btConnectedLabel: TextView
    private lateinit var ipInput: EditText
    private lateinit var portInput: EditText
    private lateinit var connectBluetooth: Button
    private lateinit var discoverBluetooth: Button
    private lateinit var connectUsb: Button
    private lateinit var connectNet: Button
    private lateinit var printTest: Button
    private lateinit var settingsButton: Button
    private lateinit var backToTypes: Button
    private lateinit var btSection: LinearLayout
    private lateinit var usbSection: LinearLayout
    private lateinit var netSection: LinearLayout
    private lateinit var chooserBluetooth: Button
    private lateinit var chooserUsb: Button
    private lateinit var chooserNet: Button
    private lateinit var settingsButtonChooser: Button
    private lateinit var scanHint: TextView
    private lateinit var cancelScan: Button
    private var scanModeActive = false

    private var currentView = "chooser"

    private val mainHandler = Handler(Looper.getMainLooper())
    private val scope = CoroutineScope(Dispatchers.Default)

    private val bluetoothAdapter: BluetoothAdapter? by lazy {
        getSystemService<BluetoothManager>()?.adapter
    }

    private val usbManager: UsbManager? by lazy { getSystemService() }

    private var activePrinter: String? = null

    private var pendingPairDevice: BluetoothDevice? = null

    private var pendingAutoConnect: String? = null

    private val discoveredDevices = mutableListOf<BluetoothDevice>()

    private var discoveryInProgress = false

    private val MAC_REGEX = Regex("^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")

    private fun setControlsEnabled(enabled: Boolean) {
        connectBluetooth.isEnabled = enabled
        discoverBluetooth.isEnabled = enabled
        connectUsb.isEnabled = enabled
        connectNet.isEnabled = enabled
        printTest.isEnabled = enabled
    }

    private val bluetoothPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
            if (allGranted(it)) fillBluetoothDevices() else showStatus(getString(R.string.bluetooth_permission_denied))
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_chooser)
        wireChooser()

        requestBluetoothPermission()
        checkForUpdate()
        restoreSession()
    }

    private fun restoreSession() {
        val prefs = getSharedPreferences("tipprint", MODE_PRIVATE)
        val type = prefs.getString("printer_type", null)
        val target = prefs.getString("printer_target", null)
        if (type == null || target == null) return
        openWork(type)
        when (type) {
            "bt" -> pendingAutoConnect = target
            "net" -> {
                ipInput.setText(target.substringBefore(":"))
                portInput.setText(target.substringAfter(":"))
                connectNet(target.substringBefore(":"), target.substringAfter(":").toIntOrNull() ?: 9100)
            }
            "usb" -> autoReconnectUsb(target)
        }
    }

    private fun autoReconnectUsb(deviceId: String) {
        val manager = usbManager ?: return showStatus(getString(R.string.usb_unavailable))
        val device = manager.deviceList.values.firstOrNull { it.deviceId.toString() == deviceId }
        if (device == null) {
            showStatus(getString(R.string.usb_reconnect_failed))
            return
        }
        if (manager.hasPermission(device)) {
            connectUsbDeviceAfterPermission(device)
        } else {
            connectUsbDevice(manager, device)
        }
    }

    private fun maybeAutoConnect() {
        val mac = pendingAutoConnect ?: return
        pendingAutoConnect = null
        val adapter = bluetoothAdapter ?: return showStatus(getString(R.string.bluetooth_unavailable))
        val device = runCatching { adapter.getRemoteDevice(mac) }.getOrNull() ?: return
        connectToDevice(device)
    }

    private fun wireChooser() {
        chooserBluetooth = findViewById(R.id.chooserBluetooth)
        chooserUsb = findViewById(R.id.chooserUsb)
        chooserNet = findViewById(R.id.chooserNet)
        settingsButtonChooser = findViewById(R.id.settingsButtonChooser)
        currentView = "chooser"

        chooserBluetooth.setOnClickListener { openWork("bt") }
        chooserUsb.setOnClickListener { openWork("usb") }
        chooserNet.setOnClickListener { openWork("net") }
        settingsButtonChooser.setOnClickListener {
            startActivity(Intent(this, SettingsActivity::class.java))
        }
    }

    private fun openWork(type: String) {
        setContentView(R.layout.activity_main)
        statusText = findViewById(R.id.statusText)
        btList = findViewById(R.id.btList)
        btConnectedLabel = findViewById(R.id.btConnectedLabel)
        ipInput = findViewById(R.id.ipInput)
        portInput = findViewById(R.id.portInput)
        connectBluetooth = findViewById(R.id.connectBluetooth)
        discoverBluetooth = findViewById(R.id.discoverBluetooth)
        connectUsb = findViewById(R.id.connectUsb)
        connectNet = findViewById(R.id.connectNet)
        printTest = findViewById(R.id.printTest)
        settingsButton = findViewById(R.id.settingsButton)
        backToTypes = findViewById(R.id.backToTypes)
        btSection = findViewById(R.id.btSection)
        usbSection = findViewById(R.id.usbSection)
        netSection = findViewById(R.id.netSection)
        scanHint = findViewById(R.id.scanHint)
        cancelScan = findViewById(R.id.cancelScan)
        currentView = "work"

        btSection.visibility = if (type == "bt") View.VISIBLE else View.GONE
        usbSection.visibility = if (type == "usb") View.VISIBLE else View.GONE
        netSection.visibility = if (type == "net") View.VISIBLE else View.GONE

        connectBluetooth.setOnClickListener { connectBluetoothClicked() }
        discoverBluetooth.setOnClickListener { discoverBluetoothClicked() }
        connectUsb.setOnClickListener { connectUsbClicked() }
        connectNet.setOnClickListener { connectNetClicked() }
        printTest.setOnClickListener { printTestClicked() }
        settingsButton.setOnClickListener {
            startActivity(Intent(this, SettingsActivity::class.java))
        }
        backToTypes.setOnClickListener { openChooser() }
        btList.setOnItemClickListener { _, _, position, _ ->
            val list = if (scanModeActive) discoveredDevices else bondedDevices
            val device = list.getOrNull(position) ?: return@setOnItemClickListener
            if (scanModeActive) {
                stopScan()
                pairWithDevice(device)
            } else {
                connectToDevice(device)
            }
        }
        cancelScan.setOnClickListener { stopScan() }
        applyBtListMaxItems(4)

        if (type == "bt") fillBluetoothDevices()
    }

    private fun openChooser() {
        stopScan()
        setContentView(R.layout.activity_chooser)
        wireChooser()
    }

    override fun onBackPressed() {
        if (currentView == "work") {
            openChooser()
        } else {
            super.onBackPressed()
        }
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

    private val bondedDevices = mutableListOf<BluetoothDevice>()

    private fun fillBluetoothDevices() {
        val adapter = bluetoothAdapter
        if (adapter == null) {
            fillBluetoothList(emptyList())
            showStatus(getString(R.string.bluetooth_unavailable))
            return
        }
        runCatching {
            adapter.bondedDevices
        }.onSuccess { devices ->
            bondedDevices.clear()
            bondedDevices += devices.sortedBy { it.name }
            maybeAutoConnect()
            if (scanModeActive) return@onSuccess
            fillBluetoothList(bondedDevices)
            if (devices.isEmpty()) showStatus(getString(R.string.bluetooth_no_devices))
        }.onFailure {
            bondedDevices.clear()
            fillBluetoothList(emptyList())
            showStatus(getString(R.string.bluetooth_permission_denied))
        }
    }

    private fun fillBluetoothList(devices: List<BluetoothDevice>) {
        if (!::btList.isInitialized) return
        val labels = devices.map { "${it.name} - ${it.address}" }
        btList.adapter = ArrayAdapter(this, R.layout.item_printer, labels)
        applyBtListMaxItems(4)
    }

    private fun fillFoundList() {
        if (!::btList.isInitialized) return
        val labels = discoveredDevices.map { it.name ?: it.address }
        btList.adapter = ArrayAdapter(this, R.layout.item_printer, labels)
        applyBtListMaxItems(4)
    }

    private fun applyBtListMaxItems(maxItems: Int) {
        btList.post {
            if (!::btList.isInitialized) return@post
            val sample = LayoutInflater.from(this).inflate(R.layout.item_printer, btList, false)
            val w = View.MeasureSpec.makeMeasureSpec(btList.width, View.MeasureSpec.EXACTLY)
            val h = View.MeasureSpec.makeMeasureSpec(0, View.MeasureSpec.UNSPECIFIED)
            sample.measure(w, h)
            val itemHeight = sample.measuredHeight
            val count = btList.adapter?.count ?: 0
            if (count <= 0) return@post
            val visible = minOf(count, maxItems)
            btList.layoutParams.height =
                itemHeight * visible + btList.dividerHeight * (visible - 1)
            btList.requestLayout()
        }
    }

    private fun updateBtConnectedLabel() {
        if (!::btConnectedLabel.isInitialized) return
        val target = activePrinter
        if (target == null || !MAC_REGEX.matches(target)) {
            btConnectedLabel.visibility = View.GONE
            return
        }
        val name = bondedDevices.firstOrNull { it.address == target }?.name ?: target
        btConnectedLabel.text = "● $name"
        btConnectedLabel.visibility = View.VISIBLE
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
        val saved = getSharedPreferences("tipprint", MODE_PRIVATE).getString("printer_target", null)
        val device = devices.firstOrNull { it.address == saved } ?: devices.first()
        connectToDevice(device)
    }

    private fun connectToDevice(device: BluetoothDevice) {
        val adapter = bluetoothAdapter ?: return showStatus(getString(R.string.bluetooth_unavailable))
        showStatus(getString(R.string.connecting_bluetooth, device.name, device.address))
        setControlsEnabled(false)
        scope.launch {
            val ok = tryOpenPrinter(adapter, device)
            withContext(Dispatchers.Main) {
                setControlsEnabled(true)
                if (ok) {
                    activePrinter = device.address
                    savePrinter("bluetooth", device.address)
                    updateBtConnectedLabel()
                    showStatus(getString(R.string.bluetooth_connected, device.name))
                } else {
                    activePrinter = null
                    updateBtConnectedLabel()
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
        discoveryInProgress = true
        scanModeActive = true
        setControlsEnabled(false)
        scanHint.visibility = View.GONE
        cancelScan.visibility = View.VISIBLE
        fillFoundList()
        runCatching { adapter.startDiscovery() }
            .onFailure { stopScan(); showStatus(getString(R.string.bluetooth_scan_failed)) }
            .onSuccess { showScanningStatus() }
    }

    private fun finishScan() {
        cancelScan.visibility = View.GONE
        if (discoveredDevices.isEmpty()) {
            scanHint.visibility = View.VISIBLE
            scanModeActive = false
            fillBluetoothDevices()
        }
    }

    private fun stopScan() {
        runCatching { bluetoothAdapter?.cancelDiscovery() }
        discoveryInProgress = false
        stopScanningAnimation()
        setControlsEnabled(true)
        if (scanModeActive) {
            scanModeActive = false
            scanHint.visibility = View.GONE
            cancelScan.visibility = View.GONE
            fillBluetoothDevices()
        }
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
                        fillFoundList()
                    }
                }
                BluetoothAdapter.ACTION_DISCOVERY_FINISHED -> {
                    if (!discoveryInProgress) return
                    discoveryInProgress = false
                    stopScanningAnimation()
                    setControlsEnabled(true)
                    fillFoundList()
                    finishScan()
                    if (discoveredDevices.isEmpty()) {
                        showStatus(getString(R.string.bluetooth_none_found))
                    }
                }
                BluetoothDevice.ACTION_BOND_STATE_CHANGED -> {
                    val device = intent.bluetoothDeviceExtra()
                    when (intent.getIntExtra(BluetoothDevice.EXTRA_BOND_STATE, -1)) {
                        BluetoothDevice.BOND_BONDED -> {
                            fillBluetoothDevices()
                            showStatus(getString(R.string.bluetooth_bonded))
                            val pending = pendingPairDevice
                            if (device != null && pending != null && pending.address == device.address) {
                                pendingPairDevice = null
                                connectToDevice(device)
                            }
                        }
                        BluetoothDevice.BOND_NONE -> {
                            val pending = pendingPairDevice
                            if (device != null && pending != null && pending.address == device.address) {
                                pendingPairDevice = null
                                setControlsEnabled(true)
                                showStatus(getString(R.string.bluetooth_pair_failed))
                            }
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
        setControlsEnabled(false)
        scope.launch {
            val ok = tryOpenPrinter(adapter, device)
            withContext(Dispatchers.Main) {
                if (ok) {
                    setControlsEnabled(true)
                    activePrinter = device.address
                    savePrinter("bluetooth", device.address)
                    updateBtConnectedLabel()
                    showStatus(getString(R.string.bluetooth_connected, device.name))
                } else {
                    pendingPairDevice = device
                    showStatus(getString(R.string.bluetooth_pairing, device.name ?: device.address))
                    runCatching { device.createBond() }
                        .onSuccess { showStatus(getString(R.string.bluetooth_pairing, device.name ?: device.address)) }
                        .onFailure { setControlsEnabled(true); showStatus(getString(R.string.bluetooth_pair_failed)) }
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
        if (::scanHint.isInitialized && scanModeActive) {
            scanHint.visibility = View.GONE
            cancelScan.visibility = View.GONE
        }
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
                    updateBtConnectedLabel()
                    showStatus(getString(R.string.usb_connected, device.productName ?: device.deviceName))
                } else {
                    activePrinter = null
                    updateBtConnectedLabel()
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
        connectNet(host, port)
    }

    private fun connectNet(host: String, port: Int) {
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
                    updateBtConnectedLabel()
                    showStatus(getString(R.string.net_connected, target))
                } else {
                    activePrinter = null
                    updateBtConnectedLabel()
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
                    MAC_REGEX.matches(target) -> BluetoothPrinter(bluetoothAdapter!!, bluetoothAdapter!!.getRemoteDevice(target))
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
        mainHandler.post { if (::statusText.isInitialized) statusText.text = message }
    }
}