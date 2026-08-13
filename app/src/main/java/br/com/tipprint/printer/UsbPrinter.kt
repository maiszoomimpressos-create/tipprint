package br.com.tipprint.printer

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.hardware.usb.UsbConstants
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbDeviceConnection
import android.hardware.usb.UsbEndpoint
import android.hardware.usb.UsbInterface
import android.hardware.usb.UsbManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CompletableDeferred
import java.io.IOException

object UsbPrinterManager {

    private const val TAG = "TipPrintUsb"
    private const val ACTION_USB_PERMISSION = "br.com.tipprint.USB_PERMISSION"

    fun listPrinters(usbManager: UsbManager): List<UsbDevice> =
        usbManager.deviceList.values.filter(::isPrinterLike)

    fun isPrinterLike(device: UsbDevice): Boolean {
        for (i in 0 until device.interfaceCount) {
            val usbInterface = device.getInterface(i)
            if (usbInterface.interfaceClass == UsbConstants.USB_CLASS_PRINTER) return true
        }
        return false
    }

    fun requestPermission(context: Context, usbManager: UsbManager, device: UsbDevice): CompletableDeferred<Boolean> {
        val granted = CompletableDeferred<Boolean>()
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context, intent: Intent) {
                if (intent.action != ACTION_USB_PERMISSION) return
                ctx.unregisterReceiver(this)
                granted.complete(intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false))
            }
        }
        val flags = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) PendingIntent.FLAG_MUTABLE else 0
        val pendingIntent = PendingIntent.getBroadcast(
            context, 0, Intent(ACTION_USB_PERMISSION), flags
        )
        ContextCompat.registerReceiver(
            context,
            receiver,
            IntentFilter(ACTION_USB_PERMISSION),
            ContextCompat.RECEIVER_NOT_EXPORTED
        )
        usbManager.requestPermission(device, pendingIntent)
        return granted
    }
}

class UsbPrinter(
    private val usbManager: UsbManager,
    private val device: UsbDevice,
) : PrinterConnection {

    private var connection: UsbDeviceConnection? = null
    private var endpoints: Pair<UsbEndpoint, UsbEndpoint>? = null

    override val name: String = device.productName ?: device.deviceName

    override fun open() {
        if (!usbManager.hasPermission(device)) {
            throw IOException("Sem permissão USB para ${device.deviceName}")
        }
        connection = usbManager.openDevice(device)
            ?: throw IOException("Não foi possível abrir o dispositivo USB ${device.deviceName}")
        connectInterface(0)
    }

    private fun connectInterface(index: Int) {
        if (index >= device.interfaceCount) throw IOException("Nenhuma interface de impressora encontrada em $name")
        val usbInterface = device.getInterface(index)
        if (usbInterface.interfaceClass != UsbConstants.USB_CLASS_PRINTER) {
            connectInterface(index + 1)
            return
        }
        val claimed = connection?.claimInterface(usbInterface, true) ?: false
        if (!claimed) throw IOException("Falha ao reivindicar a interface USB de $name")
        var out: UsbEndpoint? = null
        var inEndpoint: UsbEndpoint? = null
        for (i in 0 until usbInterface.endpointCount) {
            val endpoint = usbInterface.getEndpoint(i)
            if (endpoint.type == UsbConstants.USB_ENDPOINT_XFER_BULK) {
                if (endpoint.direction == UsbConstants.USB_DIR_OUT && out == null) out = endpoint
                if (endpoint.direction == UsbConstants.USB_DIR_IN && inEndpoint == null) inEndpoint = endpoint
            }
        }
        endpoints = out?.let { it to (inEndpoint ?: it) } ?: throw IOException("Impressora USB $name sem endpoint de saída")
    }

    override fun write(data: ByteArray) {
        val (out, _) = endpoints ?: throw IOException("Impressora USB não conectada")
        val conn = connection ?: throw IOException("Impressora USB não conectada")
        var offset = 0
        val page = 4096
        while (offset < data.size) {
            val chunk = if (offset + page <= data.size) data.copyOfRange(offset, offset + page) else data.copyOfRange(offset, data.size)
            val written = conn.bulkTransfer(out, chunk, chunk.size, 5000)
            if (written < 0) throw IOException("Falha ao enviar dados para $name")
            offset += written
        }
    }

    override fun checkStatus(): Boolean {
        return try {
            val (_, inEndpoint) = endpoints ?: return false
            val buffer = ByteArray(64)
            val read = connection?.bulkTransfer(inEndpoint, buffer, buffer.size, 1000) ?: -1
            read >= 0
        } catch (e: Exception) {
            Log.w(TAG, "Falha ao ler status USB", e)
            false
        }
    }

    override fun close() {
        try {
            connection?.close()
        } finally {
            connection = null
            endpoints = null
        }
    }

    companion object {
        private const val TAG = "TipPrintUsbPrinter"
    }
}