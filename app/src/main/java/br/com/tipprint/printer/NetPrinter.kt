package br.com.tipprint.printer

import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket

class NetPrinter(
    private val host: String,
    private val port: Int = 9100,
) : PrinterConnection {

    private var socket: Socket? = null

    override val name: String = "$host:$port"

    override fun open() {
        socket = Socket().apply {
            connect(InetSocketAddress(host, port), 5000)
            soTimeout = 5000
        }
    }

    override fun write(data: ByteArray) {
        val connected = socket ?: throw IOException("Impressora de rede não conectada")
        val stream = connected.getOutputStream() ?: throw IOException("Fluxo de saída indisponível")
        stream.write(data)
        stream.flush()
    }

    override fun checkStatus(): Boolean =
        runCatching { socket?.isConnected == true && (socket?.inputStream?.available() ?: 0) >= 0 }.getOrDefault(false)

    override fun close() {
        try {
            socket?.close()
        } catch (_: IOException) {
        } finally {
            socket = null
        }
    }
}