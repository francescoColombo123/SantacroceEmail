package com.company.callregistry.data.local

import android.content.Context
import androidx.core.content.edit
import com.company.callregistry.domain.AuthProblem
import com.company.callregistry.security.TokenCipher
import java.util.UUID

/** Configurazione del dispositivo e piccolo stato operativo (SharedPreferences, escluse dal backup). */
class DevicePrefs(context: Context, private val cipher: TokenCipher) {
    private val sp = context.getSharedPreferences("device", Context.MODE_PRIVATE)

    /** UUID stabile dell'installazione: creato una volta, cambia solo reinstallando o cancellando i dati. */
    val installationUuid: String
        get() = sp.getString(K_INSTALL_UUID, null) ?: UUID.randomUUID().toString().also {
            sp.edit(commit = true) { putString(K_INSTALL_UUID, it) }
        }

    val isConfigured: Boolean get() = sp.getBoolean(K_CONFIGURED, false) && deviceToken != null

    var deviceName: String?
        get() = sp.getString("device_name", null)
        set(v) = sp.edit { putString("device_name", v) }

    var ownerName: String?
        get() = sp.getString("owner_name", null)
        set(v) = sp.edit { putString("owner_name", v) }

    var simNumber: String?
        get() = sp.getString("sim_number", null)
        set(v) = sp.edit { putString("sim_number", v) }

    var serverUrl: String?
        get() = sp.getString("server_url", null)
        set(v) = sp.edit { putString("server_url", v) }

    var deviceId: Long
        get() = sp.getLong("device_id", 0L)
        set(v) = sp.edit { putLong("device_id", v) }

    /** Token in chiaro solo in memoria; su disco è cifrato con la chiave Keystore. */
    val deviceToken: String?
        get() = sp.getString(K_TOKEN, null)?.let { cipher.decrypt(it) }

    fun completeRegistration(deviceId: Long, token: String, deviceName: String, ownerName: String,
                             simNumber: String?, serverUrl: String, callLogWatermark: Long) {
        sp.edit(commit = true) {
            putLong("device_id", deviceId)
            putString(K_TOKEN, cipher.encrypt(token))
            putString("device_name", deviceName)
            putString("owner_name", ownerName)
            putString("sim_number", simNumber)
            putString("server_url", serverUrl)
            // Alla prima attivazione si monitorano solo le chiamate successive;
            // in caso di riattivazione si mantiene il punto raggiunto.
            if (!sp.contains(K_WATERMARK)) putLong(K_WATERMARK, callLogWatermark)
            remove(K_AUTH_PROBLEM)
            putBoolean(K_CONFIGURED, true)
        }
    }

    /** Ultimo _ID del CallLog importato. */
    var callLogWatermark: Long
        get() = sp.getLong(K_WATERMARK, Long.MAX_VALUE)
        set(v) = sp.edit(commit = true) { putLong(K_WATERMARK, v) }

    var authProblem: AuthProblem?
        get() = sp.getString(K_AUTH_PROBLEM, null)?.let { runCatching { AuthProblem.valueOf(it) }.getOrNull() }
        set(v) = sp.edit(commit = true) { if (v == null) remove(K_AUTH_PROBLEM) else putString(K_AUTH_PROBLEM, v.name) }

    var lastSyncAt: Long
        get() = sp.getLong("last_sync_at", 0L)
        set(v) = sp.edit { putLong("last_sync_at", v) }

    var lastSyncError: String?
        get() = sp.getString("last_sync_error", null)
        set(v) = sp.edit { putString("last_sync_error", v) }

    var lastImportAt: Long
        get() = sp.getLong("last_import_at", 0L)
        set(v) = sp.edit { putLong("last_import_at", v) }

    var lastExitInfoTimestamp: Long
        get() = sp.getLong("last_exit_ts", 0L)
        set(v) = sp.edit { putLong("last_exit_ts", v) }

    private companion object {
        const val K_INSTALL_UUID = "installation_uuid"
        const val K_CONFIGURED = "configured"
        const val K_TOKEN = "device_token_enc"
        const val K_WATERMARK = "calllog_watermark"
        const val K_AUTH_PROBLEM = "auth_problem"
    }
}
