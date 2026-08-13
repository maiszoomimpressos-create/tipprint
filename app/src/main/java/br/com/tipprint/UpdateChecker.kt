package br.com.tipprint

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
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
    const val DEFAULT_SERVER = "https://tipprint.vercel.app"

    fun serverUrl(context: Context): String =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY_SERVER, DEFAULT_SERVER) ?: DEFAULT_SERVER

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