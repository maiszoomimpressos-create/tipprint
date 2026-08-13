package br.com.tipprint.printer

interface PrinterConnection : AutoCloseable {

    val name: String

    fun open()

    fun write(data: ByteArray)

    fun checkStatus(): Boolean

    override fun close()
}

fun printBytes(connection: PrinterConnection, data: ByteArray) {
    connection.open()
    try {
        connection.write(data)
    } finally {
        connection.close()
    }
}