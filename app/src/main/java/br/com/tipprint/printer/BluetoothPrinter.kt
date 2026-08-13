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
        socket = try {
            device.createRfcommSocketToServiceRecord(SPP_UUID)
        } catch (e: IOException) {
            throw IOException("Não foi possível criar a conexão Bluetooth com $name", e)
        }
        adapter.cancelDiscovery()
        socket?.connect()
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