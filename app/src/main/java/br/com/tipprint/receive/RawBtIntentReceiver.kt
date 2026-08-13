package br.com.tipprint.receive

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

class TipPrintIntentReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_PRINT) return
        val printSpec = intent.getStringExtra(PRINT_SPEC_KEY) ?: return
        val serviceIntent = Intent(context, PrintService::class.java)
            .putExtra(PRINT_SPEC_KEY, printSpec)
        context.startForegroundService(serviceIntent)
    }

    companion object {
        const val ACTION_PRINT = "kellinwood.net.tipprint.intent.action.PRINT"
        const val PRINT_SPEC_KEY = "kellinwood.net.tipprint.intent.extra.PRINT_SPEC"
    }
}