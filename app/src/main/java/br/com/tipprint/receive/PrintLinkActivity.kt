package br.com.tipprint.receive

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.util.Log
import android.widget.Toast

// Ponte "site -> app", equivalente ao que o RawBT faz com o esquema
// intent:base64,<B64>#Intent;scheme=rawbt;package=ru.a402d.rawbtprinter;end;
// — só que aponta pro nosso proprio app, sem depender de terceiro.
//
// O tipo7.com (ou qualquer site) monta os bytes ESC/POS completos (com QR
// incluso) e dispara:
//   intent:base64,<B64>#Intent;scheme=tipprint;package=br.com.tipprint;end;
// via window.location.href, direto no navegador do celular — sem precisar
// abrir diálogo nenhum do Android nem passar pelo picker de apps.
//
// Sem UI própria (Theme.NoDisplay no manifesto): só repassa os bytes pro
// PrintService (mesmo caminho de impressão já usado pelo broadcast
// kellinwood.net.tipprint.intent.action.PRINT) e fecha na hora.
class PrintLinkActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val data = intent?.data
        // getSchemeSpecificPart() devolve tudo depois de "tipprint:" —
        // esperado no formato "base64,<dados>".
        val part = data?.schemeSpecificPart
        val b64 = part?.substringAfter("base64,", missingDelimiterValue = "")

        if (b64.isNullOrEmpty()) {
            Log.e(TAG, "PrintLinkActivity recebeu link sem payload base64: $data")
            Toast.makeText(this, "Link de impressão inválido", Toast.LENGTH_SHORT).show()
            finish()
            return
        }

        val serviceIntent = Intent(this, PrintService::class.java)
            .putExtra(PrintService.RAW_BYTES_KEY, b64)
        try {
            startForegroundService(serviceIntent)
        } catch (e: Exception) {
            Log.e(TAG, "Falha ao iniciar PrintService a partir do link", e)
            Toast.makeText(this, "Falha ao imprimir: ${e.message}", Toast.LENGTH_SHORT).show()
        }
        finish()
    }

    companion object {
        private const val TAG = "TipPrintPrintLink"
    }
}
