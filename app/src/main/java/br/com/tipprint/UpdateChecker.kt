package br.com.tipprint

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.FileProvider
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL

data class UpdateInfo(
    val versionCode: Int,
    val versionName: String,
    val apkUrl: String,
    val notes: String
)

object UpdateChecker {

    const val PREFS = "tipprint_updates"
    const val KEY_SERVER = "update_server_url"
    const val KEY_AUTO_UPDATE = "auto_update"
    const val DEFAULT_SERVER = "https://tipprint.vercel.app"

    fun serverUrl(context: Context): String =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY_SERVER, DEFAULT_SERVER) ?: DEFAULT_SERVER

    fun autoUpdateEnabled(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getBoolean(KEY_AUTO_UPDATE, true)

    fun setAutoUpdate(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .putBoolean(KEY_AUTO_UPDATE, enabled)
            .apply()
    }

    fun installApk(context: Context, apk: File): Boolean = runCatching {
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", apk)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        context.startActivity(intent)
        true
    }.getOrDefault(false)

    fun installedVersionCode(context: Context): Int = runCatching {
        val info = context.packageManager.getPackageInfo(context.packageName, 0)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            info.longVersionCode.toInt()
        } else {
            @Suppress("DEPRECATION")
            info.versionCode
        }
    }.getOrDefault(0)

    suspend fun check(context: Context): UpdateInfo? = withContext(Dispatchers.IO) {
        runCatching {
            val base = serverUrl(context).trimEnd('/')
            val conn = URL("$base/update.json").openConnection() as HttpURLConnection
            conn.connectTimeout = 5_000
            conn.readTimeout = 5_000
            try {
                val raw = conn.inputStream.bufferedReader().readText()
                val json = JSONObject(raw)
                UpdateInfo(
                    versionCode = json.getInt("versionCode"),
                    versionName = json.getString("versionName"),
                    apkUrl = URI("$base/").resolve(json.getString("apkPath")).toString(),
                    notes = json.optString("notes", "")
                )
            } finally {
                conn.disconnect()
            }
        }.getOrNull()?.takeIf { it.versionCode > installedVersionCode(context) }
    }

    suspend fun downloadApk(context: Context, url: String): File? = withContext(Dispatchers.IO) {
        runCatching {
            val conn = URL(url).openConnection() as HttpURLConnection
            conn.connectTimeout = 10_000
            conn.readTimeout = 30_000
            try {
                val dir = File(context.cacheDir, "update").apply { mkdirs() }
                val target = File(dir, "update.apk")
                conn.inputStream.use { input ->
                    target.outputStream().use { out -> input.copyTo(out) }
                }
                target
            } finally {
                conn.disconnect()
            }
        }.getOrNull()
    }
}