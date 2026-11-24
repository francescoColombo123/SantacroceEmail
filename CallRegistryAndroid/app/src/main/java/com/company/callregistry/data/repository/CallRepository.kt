package com.company.callregistry.data.repository

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import androidx.core.content.ContextCompat
import com.company.callregistry.data.local.CallEventDao
import com.company.callregistry.data.local.CallEventEntity
import com.company.callregistry.data.local.DevicePrefs
import com.company.callregistry.data.remote.ApiJson
import com.company.callregistry.data.remote.CallBatchRequest
import com.company.callregistry.data.remote.CallEventDto
import com.company.callregistry.data.remote.MobileApi
import com.company.callregistry.data.remote.MobileApiFactory
import com.company.callregistry.diag.DiagLogger
import com.company.callregistry.domain.AuthProblem
import com.company.callregistry.domain.SyncStatus
import com.company.callregistry.sync.SyncPolicy
import com.company.callregistry.telephony.CallLogMapper
import com.company.callregistry.telephony.CallLogReader
import com.company.callregistry.telephony.EventIds
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.IOException
import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * Cuore dell'app: importa le chiamate dal registro di sistema in Room e le invia al server.
 * Le due operazioni sono serializzate da mutex, quindi possono essere invocate da più
 * sorgenti (broadcast, trigger, job periodico, UI) senza duplicazioni.
 */
class CallRepository(
    private val context: Context,
    private val dao: CallEventDao,
    private val prefs: DevicePrefs,
    private val log: DiagLogger,
    private val apiProvider: (String) -> MobileApi,
) {
    sealed interface ImportResult {
        data class Imported(val count: Int) : ImportResult
        data object NotConfigured : ImportResult
        data object MissingPermission : ImportResult
        data class Failed(val error: String) : ImportResult
    }

    enum class SyncResult { DONE, NOTHING_TO_DO, RETRY, STOPPED, NOT_CONFIGURED }

    // ------------------------------------------------------------------ import dal CallLog

    suspend fun importNewCalls(source: String): ImportResult = importMutex.withLock { importLocked(source) }

    private suspend fun importLocked(source: String): ImportResult {
        if (!prefs.isConfigured) return ImportResult.NotConfigured
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.READ_CALL_LOG) != PackageManager.PERMISSION_GRANTED) {
            log.logNow("IMPORT", "[$source] READ_CALL_LOG non concesso: impossibile leggere il registro")
            return ImportResult.MissingPermission
        }
        return try {
            val watermark = prefs.callLogWatermark
            val rows = CallLogReader(context.contentResolver).readAfter(watermark)
            val now = System.currentTimeMillis()
            val install = prefs.installationUuid
            val entities = rows.map { r ->
                val presentation = CallLogMapper.presentation(r.presentation)
                val c = CallLogMapper.classify(r.type, r.durationSeconds)
                CallEventEntity(
                    eventUuid = EventIds.forCallLogRow(install, r.id, r.date),
                    callLogId = r.id,
                    deviceUuid = install,
                    phoneNumber = CallLogMapper.normalizeNumber(r.number, presentation),
                    presentation = presentation.name,
                    direction = c.direction.name,
                    status = c.status.name,
                    rawType = r.type,
                    startedAt = r.date,
                    endedAt = r.date + r.durationSeconds * 1000,
                    durationSeconds = r.durationSeconds,
                    phoneAccountId = r.phoneAccountId,
                    version = 1,
                    syncStatus = SyncStatus.PENDING.name,
                    retryCount = 0,
                    lastError = null,
                    createdAt = now,
                    updatedAt = now,
                    syncedAt = null,
                )
            }
            val inserted = dao.insertIgnore(entities).count { it != -1L }
            rows.maxOfOrNull { it.id }?.let { if (it > watermark) prefs.callLogWatermark = it }
            prefs.lastImportAt = now
            entities.forEach {
                log.logNow("IMPORT", "[$source] Chiamata ${it.direction}/${it.status} durata ${it.durationSeconds}s " +
                    "(CallLog #${it.callLogId}), rilevata ${(now - (it.endedAt ?: it.startedAt)) / 1000}s dopo la fine")
            }
            ImportResult.Imported(inserted)
        } catch (e: CancellationException) {
            throw e
        } catch (t: Throwable) {
            val msg = "${t.javaClass.simpleName}: ${t.message}"
            log.logNow("IMPORT", "[$source] Errore lettura registro: $msg")
            ImportResult.Failed(msg)
        }
    }

    // ------------------------------------------------------------------ invio al server

    suspend fun syncPending(source: String): SyncResult = syncMutex.withLock { syncLocked(source) }

    private suspend fun syncLocked(source: String): SyncResult {
        val baseUrl = prefs.serverUrl
        val token = prefs.deviceToken
        if (!prefs.isConfigured || baseUrl == null || token == null) return SyncResult.NOT_CONFIGURED
        // Token non valido: inutile ritentare finché non arriva un nuovo codice.
        // Dispositivo disattivato: si ritenta (costa un 403) per ripartire da soli se l'amministratore lo riattiva.
        prefs.authProblem?.takeIf { it != AuthProblem.DEVICE_DISABLED }?.let {
            log.logNow("SYNC", "[$source] Invii sospesi: ${it.message}")
            return SyncResult.STOPPED
        }

        val api = apiProvider(baseUrl)
        var sentAny = false
        while (true) {
            val batch = dao.pendingBatch(BATCH_SIZE)
            if (batch.isEmpty()) break
            val ids = batch.map { it.eventUuid }
            val now = System.currentTimeMillis()
            dao.markSyncing(ids, now)

            val response = try {
                api.postCalls(MobileApiFactory.bearer(token), CallBatchRequest(prefs.installationUuid, batch.map { it.toDto() }))
            } catch (e: CancellationException) {
                // Lo scope è già cancellato: la scrittura deve comunque avvenire.
                withContext(NonCancellable) { dao.revertToPending(ids, "interrotto", System.currentTimeMillis()) }
                throw e
            } catch (e: IOException) {
                return failTransient(ids, "rete: ${e.javaClass.simpleName}${e.message?.let { m -> " $m" } ?: ""}", source)
            } catch (t: Throwable) {
                return failTransient(ids, "${t.javaClass.simpleName}: ${t.message}", source)
            }

            val errorBody = if (response.isSuccessful) null else runCatching { response.errorBody()?.string() }.getOrNull()
            val error = ApiJson.parseError(errorBody)
            when (val action = SyncPolicy.actionForHttp(response.code(), error?.error)) {
                SyncPolicy.BatchAction.ApplyResults -> {
                    val byId = response.body()?.results.orEmpty().associateBy { it.eventUuid.lowercase() }
                    val t = System.currentTimeMillis()
                    val missing = mutableListOf<String>()
                    for (id in ids) {
                        val r = byId[id.lowercase()]
                        when (r?.let { SyncPolicy.statusForResult(it.result) }) {
                            SyncStatus.SYNCED -> dao.markSynced(id, t)
                            SyncStatus.FAILED -> dao.markFailed(id, "${r.result}: ${r.error.orEmpty()}", t)
                            else -> missing += id
                        }
                    }
                    if (missing.isNotEmpty()) return failTransient(missing, "esito mancante nella risposta", source)
                    sentAny = true
                    if (prefs.authProblem != null) {
                        prefs.authProblem = null
                        log.logNow("SYNC", "[$source] Dispositivo di nuovo attivo: invii ripresi")
                    }
                    prefs.lastSyncAt = t
                    prefs.lastSyncError = null
                    log.logNow("SYNC", "[$source] Inviate ${ids.size} chiamate: " +
                        byId.values.groupingBy { it.result }.eachCount().entries.joinToString { "${it.key}=${it.value}" })
                }
                SyncPolicy.BatchAction.Retry ->
                    return failTransient(ids, "HTTP ${response.code()} ${error?.error.orEmpty()}".trim(), source)
                SyncPolicy.BatchAction.FailBatch -> {
                    val t = System.currentTimeMillis()
                    ids.forEach { dao.markFailed(it, "HTTP ${response.code()} ${error?.message.orEmpty()}".trim(), t) }
                    prefs.lastSyncError = "HTTP ${response.code()}"
                    log.logNow("SYNC", "[$source] Lotto rifiutato (HTTP ${response.code()} ${error?.error.orEmpty()}): eventi segnati FAILED")
                }
                is SyncPolicy.BatchAction.Stop -> {
                    dao.revertToPending(ids, action.problem.name, System.currentTimeMillis())
                    prefs.authProblem = action.problem
                    prefs.lastSyncError = action.problem.message
                    log.logNow("SYNC", "[$source] Invii sospesi: ${action.problem.message}")
                    return SyncResult.STOPPED
                }
            }
        }
        return if (sentAny) SyncResult.DONE else SyncResult.NOTHING_TO_DO
    }

    private suspend fun failTransient(ids: List<String>, error: String, source: String): SyncResult {
        dao.revertToPending(ids, error, System.currentTimeMillis())
        prefs.lastSyncError = error
        log.logNow("SYNC", "[$source] Invio non riuscito ($error): ${ids.size} eventi conservati, nuovo tentativo pianificato")
        return SyncResult.RETRY
    }

    suspend fun recoverAfterProcessStart() {
        val n = dao.resetStuckSyncing(System.currentTimeMillis())
        if (n > 0) log.logNow("SYNC", "$n eventi rimasti in SYNCING riportati in PENDING")
    }

    suspend fun retryFailed(): Int = dao.retryFailed(System.currentTimeMillis())

    suspend fun cleanupOldSynced(): Int =
        dao.deleteSyncedBefore(System.currentTimeMillis() - KEEP_SYNCED_MS)

    suspend fun hasUnsynced(): Boolean = dao.unsyncedCount() > 0

    private fun CallEventEntity.toDto() = CallEventDto(
        eventUuid = eventUuid,
        version = version,
        phoneNumber = phoneNumber,
        presentation = presentation,
        direction = direction,
        status = status,
        rawType = rawType,
        startedAt = iso(startedAt),
        endedAt = endedAt?.let(::iso),
        durationSeconds = durationSeconds,
        phoneAccountId = phoneAccountId,
    )

    companion object {
        private const val BATCH_SIZE = 50
        private const val KEEP_SYNCED_MS = 30L * 24 * 60 * 60 * 1000
        private val importMutex = Mutex()
        private val syncMutex = Mutex()

        fun iso(epochMillis: Long): String =
            OffsetDateTime.ofInstant(Instant.ofEpochMilli(epochMillis), ZoneId.systemDefault())
                .format(DateTimeFormatter.ISO_OFFSET_DATE_TIME)
    }
}
