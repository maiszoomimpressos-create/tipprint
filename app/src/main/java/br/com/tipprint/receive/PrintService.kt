package br.com.tipprint.receive

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.Build
import android.os.IBinder
import android.util.Base64
import android.util.Log
import androidx.core.app.NotificationCompat
import br.com.tipprint.R
import br.com.tipprint.printer.EscPos
import br.com.tipprint.printer.printBytes
import org.json.JSONObject
import java.io.IOException

class PrintService : Service() {

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val printSpec = intent?.getStringExtra(TipPrintIntentReceiver.PRINT_SPEC_KEY)
        if (printSpec != null) {
            startForeground(1, buildNotification())
            Thread {
                try {
                    printFromSpec(printSpec)
                } catch (e: Exception) {
                    Log.e(TAG, "Falha ao processar tarefa de impressão", e)
                } finally {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                        stopForeground(STOP_FOREGROUND_REMOVE)
                    } else {
                        @Suppress("DEPRECATION")
                        stopForeground(true)
                    }
                    stopSelf()
                }
            }.start()
        } else {
            stopSelf()
        }
        return START_NOT_STICKY
    }

    private fun printFromSpec(printSpec: String) {
        val json = JSONObject(printSpec)
        val data = json.optJSONArray("data") ?: return
        val pos = EscPos()
        pos.init().setCharset()
        for (i in 0 until data.length()) {
            val item = data.getJSONObject(i)
            val text = item.optString("printText")
            if (text.isNotBlank()) {
                pos.align(EscPos.Align.LEFT).line(text)
                continue
            }
            val size = item.optInt("printImageSize", 384)
            if (item.has("printImage")) {
                val decoded = Base64.decode(item.getString("printImage"), Base64.DEFAULT)
                throw IOException("Imagem ESC/POS não implementada nesta versão (tamanho máximo: $size)")
            }
        }
        pos.feed(3).cut()
        printBytes(resolveConnection(), pos.bytes)
    }

    private fun resolveConnection(): br.com.tipprint.printer.PrinterConnection {
        val prefs = getSharedPreferences(PREFS, MODE_PRIVATE)
        val type = prefs.getString(PREFS_TYPE, null) ?: throw IOException("Nenhuma impressora foi configurada no app")
        return when (type) {
            "bluetooth" -> br.com.tipprint.printer.BluetoothPrinter(
                getSystemService(android.bluetooth.BluetoothManager::class.java).adapter,
                getSystemService(android.bluetooth.BluetoothManager::class.java).adapter.getRemoteDevice(prefs.getString(PREFS_TARGET, "")!!)
            )
            "usb" -> br.com.tipprint.printer.UsbPrinter(
                getSystemService(android.hardware.usb.UsbManager::class.java),
                getSystemService(android.hardware.usb.UsbManager::class.java).deviceList.values.first {
                    it.deviceId.toString() == prefs.getString(PREFS_TARGET, "")
                }
            )
            "net" -> br.com.tipprint.printer.NetPrinter(
                prefs.getString(PREFS_TARGET, "")!!.substringBefore(":"),
                prefs.getString(PREFS_TARGET, "")!!.substringAfter(":").toIntOrNull() ?: 9100
            )
            else -> throw IOException("Tipo de impressora desconhecido: $type")
        }
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID, getString(R.string.print_channel), NotificationManager.IMPORTANCE_LOW
            )
            getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
    }

    private fun buildNotification(): Notification =
        NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(getString(R.string.print_notification_title))
            .setContentText(getString(R.string.print_notification_text))
            .setSmallIcon(R.drawable.ic_print)
            .build()

    companion object {
        private const val TAG = "TipPrintPrintService"
        private const val CHANNEL_ID = "tipprint_print"
        private const val PREFS = "tipprint"
        private const val PREFS_TYPE = "printer_type"
        private const val PREFS_TARGET = "printer_target"
    }
}