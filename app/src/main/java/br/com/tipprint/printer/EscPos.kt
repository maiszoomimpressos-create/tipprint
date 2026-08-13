package br.com.tipprint.printer

import java.io.ByteArrayOutputStream
import java.nio.charset.Charset

class EscPos {

    private val out = ByteArrayOutputStream()

    enum class Align(val value: Int) { LEFT(0), CENTER(1), RIGHT(2) }

    enum class FontSize(val multiplier: Int) { NORMAL(0), DOUBLE_WIDTH(1), DOUBLE_HEIGHT(2), DOUBLE_BOTH(3) }

    val bytes: ByteArray get() = out.toByteArray()

    fun raw(data: ByteArray): EscPos {
        out.write(data)
        return this
    }

    fun init(): EscPos = raw(byteArrayOf(0x1B, 0x40))

    fun setCharset(): EscPos {
        out.write(0x1B)
        out.write(0x74)
        out.write(0x00)
        return this
    }

    fun align(align: Align): EscPos = raw(byteArrayOf(0x1B, 0x61, align.value.toByte()))

    fun fontSize(size: FontSize): EscPos = raw(byteArrayOf(0x1D, 0x21, size.multiplier.toByte()))

    fun enableBold(enable: Boolean): EscPos {
        out.write(0x1B)
        out.write(0x45)
        out.write(if (enable) 1 else 0)
        return this
    }

    fun enableUnderline(enable: Boolean): EscPos {
        out.write(0x1B)
        out.write(0x2D)
        out.write(if (enable) 1 else 0)
        return this
    }

    fun text(value: String): EscPos {
        out.write(value.toByteArray(Charset.forName("ISO-8859-1")))
        return this
    }

    fun line(value: String = ""): EscPos {
        text(value)
        out.write(0x0A)
        return this
    }

    fun separator(char: Char = '-', width: Int = 32): EscPos = line(char.toString().repeat(width))

    fun feed(lines: Int): EscPos = raw(byteArrayOf(0x1B, 0x64, lines.toByte()))

    fun cut(): EscPos = raw(byteArrayOf(0x1D, 0x56, 0x42, 0x00))

    fun openDrawer(): EscPos = raw(byteArrayOf(0x1B, 0x70, 0x00, 0x19, 0x32))

    fun qrCode(content: String, size: Int = 8): EscPos {
        val data = content.toByteArray(Charset.forName("ISO-8859-1"))
        out.write(0x1D)
        out.write('('.code)
        out.write('k'.code)
        raw(intLowHigh(3))
        out.write(0x00)
        out.write(0x31)
        out.write(0x43)
        out.write(size)
        out.write(0x1D)
        out.write('('.code)
        out.write('k'.code)
        raw(intLowHigh(3))
        out.write(0x00)
        out.write(0x31)
        out.write(0x45)
        out.write(0x32)
        out.write(0x1D)
        out.write('('.code)
        out.write('k'.code)
        raw(intLowHigh(data.size + 3))
        out.write(0x00)
        out.write(0x31)
        out.write(0x50)
        out.write(0x30)
        out.write(data)
        out.write(0x1D)
        out.write('('.code)
        out.write('k'.code)
        raw(intLowHigh(3))
        out.write(0x00)
        out.write(0x31)
        out.write(0x51)
        out.write(0x30)
        return this
    }

    fun barcodeEan13(content: String, height: Int = 60): EscPos {
        out.write(0x1D)
        out.write('h'.code)
        out.write(height)
        out.write(0x1D)
        out.write('H'.code)
        out.write(0x02)
        out.write(0x1D)
        out.write('w'.code)
        out.write(0x02)
        out.write(0x1D)
        out.write('k'.code)
        out.write(67)
        out.write(content.padEnd(13, '0').substring(0, 13).toByteArray(Charset.forName("US-ASCII")))
        return this
    }

    private fun intLowHigh(value: Int): ByteArray =
        byteArrayOf((value and 0xFF).toByte(), ((value shr 8) and 0xFF).toByte())
}