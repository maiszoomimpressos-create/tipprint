package br.com.tipprint.printer

import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import java.io.IOException
import java.util.UUID

class BluetoothPrinter(
    private val adapter: BluetoothAdapter,
    private val device: BluetoothDevice,
    private val checkStatus: Boolean = false,
) : PrinterConnection {

    private var socket: BluetoothSocket? = null

    override val name: String = device.name ?: device.address

    override fun open() {
        adapter.cancelDiscovery()
        var lastError: IOException? = null
        for (candidate in candidateSockets(device)) {
            try {
                candidate.connect()
                socket = candidate
                return
            } catch (e: IOException) {
                lastError = e
                runCatching { candidate.close() }
            }
        }
        throw IOException("Não foi possível conectar com $name", lastError)
    }

    private fun candidateSockets(device: BluetoothDevice): List<BluetoothSocket> {
        val sockets = mutableListOf<BluetoothSocket>()
        runCatching { sockets += device.createRfcommSocketToServiceRecord(SPP_UUID) }
        runCatching {
            val method = device.javaClass.getMethod("createRfcommSocket", Int::class.javaPrimitiveType)
            @Suppress("UNCHECKED_CAST")
            sockets += method.invoke(device, 1) as BluetoothSocket
        }
        return sockets
    }

    override fun write(data: ByteArray) {
        val connected = socket ?: throw IOException("Impressora Bluetooth não conectada")
        val stream = connected.outputStream ?: throw IOException("Fluxo de saída indisponível")
        stream.write(data)
        stream.flush()
    }

    override fun checkStatus(): Boolean {
        try {
            val connected = socket ?: return false
            val available = connected.inputStream?.available() ?: 0
            if (available > 0) connected.inputStream!!.read()
            return true
        } catch (e: IOException) {
            return false
        }
    }

    override fun close() {
        try {
            socket?.close()
        } catch (_: IOException) {
        } finally {
            socket = null
        }
    }

    companion object {
        private val SPP_UUID: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
    }
}