package br.com.tipprint

import android.content.pm.PackageManager
import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity

class SettingsActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)

        val version = findViewById<TextView>(R.id.settingsVersion)
        version.text = getString(
            R.string.settings_version,
            runCatching {
                packageManager.getPackageInfo(packageName, 0).versionName
            }.getOrNull() ?: "1.0.0"
        )

        val serverInput = findViewById<EditText>(R.id.updateServerInput)
        val saveServer = findViewById<Button>(R.id.saveUpdateServer)
        serverInput.setText(UpdateChecker.serverUrl(this))
        saveServer.setOnClickListener {
            val url = serverInput.text.toString().trim().trimEnd('/')
            if (url.isEmpty()) {
                serverInput.error = getString(R.string.update_server_invalid)
                return@setOnClickListener
            }
            getSharedPreferences(UpdateChecker.PREFS, MODE_PRIVATE)
                .edit()
                .putString(UpdateChecker.KEY_SERVER, url)
                .apply()
            Toast.makeText(this, R.string.update_server_saved, Toast.LENGTH_SHORT).show()
        }
    }
}