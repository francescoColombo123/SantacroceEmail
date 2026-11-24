package com.company.callregistry.diag

import android.util.Log
import com.company.callregistry.data.local.DiagLogDao
import com.company.callregistry.data.local.DiagLogEntity
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Log diagnostico persistente (Room) + Logcat, consultabile e condivisibile dalla UI.
 * Non contiene token né credenziali; contiene solo tipo/esito delle chiamate, non i numeri.
 */
class DiagLogger(
    private val dao: DiagLogDao,
    private val scope: CoroutineScope,
    private val serialIo: CoroutineDispatcher,
) {
    /** Versione "fire and forget" per receiver e callback non sospendibili. */
    fun log(source: String, message: String) {
        Log.i(TAG, "[$source] $message")
        val ts = System.currentTimeMillis()
        scope.launch(serialIo) { write(ts, source, message) }
    }

    /** Versione sospendibile: ritorna dopo la scrittura. */
    suspend fun logNow(source: String, message: String) {
        Log.i(TAG, "[$source] $message")
        write(System.currentTimeMillis(), source, message)
    }

    suspend fun trim() = runCatching { dao.trim(KEEP) }

    private suspend fun write(ts: Long, source: String, message: String) {
        runCatching { dao.insert(DiagLogEntity(timestamp = ts, source = source, message = message)) }
            .onFailure { Log.e(TAG, "Scrittura log fallita", it) }
    }

    companion object {
        private const val TAG = "CallRegistry"
        private const val KEEP = 2000

        fun formatTime(ts: Long): String =
            if (ts <= 0) "—" else SimpleDateFormat("dd/MM/yyyy HH:mm:ss", Locale.ITALY).format(Date(ts))
    }
}
