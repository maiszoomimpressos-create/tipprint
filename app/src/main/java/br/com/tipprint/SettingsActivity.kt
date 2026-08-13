package br.com.tipprint

import android.app.AlertDialog
import android.content.pm.PackageManager
import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class SettingsActivity : AppCompatActivity() {

    private val scope = CoroutineScope(Dispatchers.Default)

    private fun autoConnectEnabled(): Boolean =
        getSharedPreferences("tipprint", MODE_PRIVATE).getBoolean("auto_connect", true)

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

        val autoUpdate = findViewById<Switch>(R.id.autoUpdateSwitch)
        autoUpdate.isChecked = UpdateChecker.autoUpdateEnabled(this)
        autoUpdate.setOnCheckedChangeListener { _, checked -> UpdateChecker.setAutoUpdate(this, checked) }

        val autoConnect = findViewById<Switch>(R.id.autoConnectSwitch)
        autoConnect.isChecked = autoConnectEnabled()
        autoConnect.setOnCheckedChangeListener { _, checked ->
            getSharedPreferences("tipprint", MODE_PRIVATE)
                .edit()
                .putBoolean("auto_connect", checked)
                .apply()
        }

        val checkUpdates = findViewById<Button>(R.id.checkUpdatesButton)
        checkUpdates.setOnClickListener { checkUpdatesNow(checkUpdates) }

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

    private fun checkUpdatesNow(button: Button) {
        button.isEnabled = false
        scope.launch {
            val info = UpdateChecker.check(this@SettingsActivity)
            withContext(Dispatchers.Main) {
                button.isEnabled = true
                if (info == null) {
                    Toast.makeText(this@SettingsActivity, R.string.settings_up_to_date, Toast.LENGTH_SHORT).show()
                } else {
                    AlertDialog.Builder(this@SettingsActivity)
                        .setTitle(R.string.update_available_title)
                        .setMessage(getString(R.string.update_available_message, info.versionName, info.notes))
                        .setPositiveButton(R.string.update_button) { _, _ -> downloadAndInstall(info.apkUrl) }
                        .setNegativeButton(R.string.cancel, null)
                        .show()
                }
            }
        }
    }

    private fun downloadAndInstall(url: String) {
        Toast.makeText(this, R.string.update_downloading, Toast.LENGTH_SHORT).show()
        scope.launch {
            val apk = UpdateChecker.downloadApk(this@SettingsActivity, url)
            withContext(Dispatchers.Main) {
                when {
                    apk == null ->
                        Toast.makeText(this@SettingsActivity, R.string.update_download_failed, Toast.LENGTH_SHORT).show()
                    !UpdateChecker.installApk(this@SettingsActivity, apk) ->
                        Toast.makeText(this@SettingsActivity, R.string.update_open_installer_failed, Toast.LENGTH_SHORT).show()
                    else ->
                        Toast.makeText(this@SettingsActivity, R.string.update_waiting_install, Toast.LENGTH_SHORT).show()
                }
            }
        }
    }
}