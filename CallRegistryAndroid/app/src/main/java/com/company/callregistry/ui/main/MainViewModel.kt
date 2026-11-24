package com.company.callregistry.ui.main

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.work.WorkInfo
import androidx.work.WorkManager
import com.company.callregistry.BuildConfig
import com.company.callregistry.container
import com.company.callregistry.data.local.CallEventEntity
import com.company.callregistry.data.local.DiagLogEntity
import com.company.callregistry.data.repository.CallRepository.ImportResult
import com.company.callregistry.data.repository.CallRepository.SyncResult
import com.company.callregistry.data.repository.DeviceRepository.HealthResult
import com.company.callregistry.diag.DeviceStatus
import com.company.callregistry.diag.DiagLogger.Companion.formatTime
import com.company.callregistry.domain.AuthProblem
import com.company.callregistry.sync.Scheduling
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

data class DeviceInfo(
    val deviceName: String,
    val ownerName: String,
    val simNumber: String?,
    val serverUrl: String,
    val deviceId: Long,
    val installationUuid: String,
    val lastSyncAt: Long,
    val lastSyncError: String?,
    val lastImportAt: Long,
    val authProblem: AuthProblem?,
)

sealed interface ApiState {
    data object Checking : ApiState
    data object Ok : ApiState
    data class Problem(val message: String, val blocking: Boolean) : ApiState
}

class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val c = application.container
    private val dao = c.database.callEvents()
    private val wm = WorkManager.getInstance(application)

    private val _info = MutableStateFlow(readInfo())
    val info: StateFlow<DeviceInfo> = _info.asStateFlow()

    private val _status = MutableStateFlow<DeviceStatus?>(null)
    val status: StateFlow<DeviceStatus?> = _status.asStateFlow()

    private val _api = MutableStateFlow<ApiState>(ApiState.Checking)
    val api: StateFlow<ApiState> = _api.asStateFlow()

    private val _message = MutableStateFlow<String?>(null)
    val message: StateFlow<String?> = _message.asStateFlow()

    private val _busy = MutableStateFlow(false)
    val busy: StateFlow<Boolean> = _busy.asStateFlow()

    val recent: StateFlow<List<CallEventEntity>> =
        dao.observeRecent(20).stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())
    val unsynced: StateFlow<Int> =
        dao.observeUnsyncedCount().stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), 0)
    val failed: StateFlow<Int> =
        dao.observeFailedCount().stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), 0)

    /** Inizio della giornata corrente: ricalcolato a ogni ritorno sulla schermata (cambio di giorno). */
    private val today = MutableStateFlow(com.company.callregistry.ui.startOfDay())

    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    val missedToday: StateFlow<Int> = today
        .flatMapLatest { dao.observeMissedSince(it) }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), 0)
    val logs: StateFlow<List<DiagLogEntity>> =
        c.database.diagLog().observe(100).stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())

    val observerActive: StateFlow<Boolean> = wm.getWorkInfosForUniqueWorkFlow(Scheduling.OBSERVER)
        .map { infos -> infos.any { it.state in ACTIVE_STATES } }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), true)
    val syncState: StateFlow<String> = wm.getWorkInfosForUniqueWorkFlow(Scheduling.SYNC)
        .map { infos -> syncLabel(infos) }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), "—")

    init {
        // Ogni nuovo log può corrispondere a un cambio di stato (invio, import): aggiorna i dati.
        viewModelScope.launch { c.database.diagLog().observe(1).collect { _info.value = readInfo() } }
    }

    /** Alla riapertura della schermata: stato locale + verifica connessione (solo con UI visibile). */
    fun onResume() {
        today.value = com.company.callregistry.ui.startOfDay()
        viewModelScope.launch {
            _status.value = withContext(Dispatchers.Default) { DeviceStatus.collect(getApplication()) }
            _info.value = readInfo()
            checkApi()
        }
    }

    private suspend fun checkApi() {
        _api.value = ApiState.Checking
        val r = withContext(Dispatchers.IO) { c.deviceRepository.checkHealth() }
        _api.value = when (r) {
            HealthResult.Ok -> ApiState.Ok
            HealthResult.ServerDbDown -> ApiState.Problem("Server raggiungibile, database non disponibile", false)
            is HealthResult.AuthError -> ApiState.Problem(r.problem.message, true)
            is HealthResult.Unreachable -> ApiState.Problem("Server non raggiungibile: ${r.message}", false)
            HealthResult.NotConfigured -> ApiState.Problem("Dispositivo non configurato", true)
        }
        _info.value = readInfo()
    }

    fun syncNow() {
        if (_busy.value) return
        _busy.value = true
        _message.value = null
        viewModelScope.launch {
            try {
                val (imp, sync) = withContext(Dispatchers.IO) {
                    val imp = c.callRepository.importNewCalls("MANUALE")
                    val sync = c.callRepository.syncPending("MANUALE")
                    // Se l'invio non è riuscito, WorkManager riprova da solo appena possibile.
                    if (sync == SyncResult.RETRY) Scheduling.enqueueSync(getApplication(), "MANUALE")
                    imp to sync
                }
                val importText = when (imp) {
                    is ImportResult.Imported -> if (imp.count > 0) "${imp.count} nuove chiamate rilevate. " else ""
                    ImportResult.MissingPermission -> "Permesso registro chiamate mancante. "
                    is ImportResult.Failed -> "Lettura registro non riuscita. "
                    ImportResult.NotConfigured -> ""
                }
                val syncText = when (sync) {
                    SyncResult.DONE -> "Invio completato."
                    SyncResult.NOTHING_TO_DO -> "Nessuna chiamata da inviare."
                    SyncResult.RETRY -> "Invio non riuscito: nuovo tentativo automatico appena possibile."
                    SyncResult.STOPPED -> "Invii sospesi: ${c.prefs.authProblem?.message.orEmpty()}"
                    SyncResult.NOT_CONFIGURED -> "Dispositivo non configurato."
                }
                _message.value = importText + syncText
                checkApi()
            } finally {
                _busy.value = false
            }
        }
    }

    fun retryFailed() {
        viewModelScope.launch {
            val n = withContext(Dispatchers.IO) {
                c.callRepository.retryFailed().also { if (it > 0) Scheduling.enqueueSync(getApplication(), "RIPROVA_FALLITI") }
            }
            _message.value = if (n > 0) "$n chiamate rimesse in coda di invio." else "Nessuna chiamata in errore."
        }
    }

    suspend fun buildReport(): String = withContext(Dispatchers.IO) {
        val s = DeviceStatus.collect(getApplication())
        val i = readInfo()
        buildString {
            appendLine("CALL REGISTRY – diagnostica del ${formatTime(System.currentTimeMillis())}")
            appendLine("App ${BuildConfig.VERSION_NAME} – ${s.device} – Android ${s.androidRelease} (API ${s.sdkInt})")
            appendLine("Dispositivo: ${i.deviceName} (id ${i.deviceId}) – assegnatario ${i.ownerName}")
            appendLine("Installazione: ${i.installationUuid}")
            appendLine("Server: ${i.serverUrl}")
            appendLine("Problema autenticazione: ${i.authProblem?.message ?: "nessuno"}")
            s.permissions.forEach { appendLine("Permesso ${it.label}: ${if (it.granted) "concesso" else "MANCANTE"}") }
            appendLine("Esenzione batteria: ${s.batteryOptimizationIgnored} – Background limitato: ${s.backgroundRestricted} – Bucket: ${s.standbyBucket}")
            appendLine("Rete: ${s.network} – Modalità aereo: ${s.airplaneMode}")
            appendLine("Ultimo import: ${formatTime(i.lastImportAt)} – Ultimo invio riuscito: ${formatTime(i.lastSyncAt)}")
            appendLine("Ultimo errore invio: ${i.lastSyncError ?: "—"}")
            appendLine("In attesa: ${unsynced.value} – In errore: ${failed.value} – Job invio: ${syncState.value}")
            appendLine()
            appendLine("LOG (più recenti in alto)")
            c.database.diagLog().recent(500).forEach { appendLine("${formatTime(it.timestamp)} [${it.source}] ${it.message}") }
        }
    }

    private fun readInfo() = DeviceInfo(
        deviceName = c.prefs.deviceName.orEmpty(),
        ownerName = c.prefs.ownerName.orEmpty(),
        simNumber = c.prefs.simNumber,
        serverUrl = c.prefs.serverUrl.orEmpty(),
        deviceId = c.prefs.deviceId,
        installationUuid = c.prefs.installationUuid,
        lastSyncAt = c.prefs.lastSyncAt,
        lastSyncError = c.prefs.lastSyncError,
        lastImportAt = c.prefs.lastImportAt,
        authProblem = c.prefs.authProblem,
    )

    private fun syncLabel(infos: List<WorkInfo>): String {
        val w = infos.firstOrNull { it.state == WorkInfo.State.RUNNING } ?: infos.lastOrNull() ?: return "nessuno"
        return when (w.state) {
            WorkInfo.State.RUNNING -> "invio in corso"
            WorkInfo.State.ENQUEUED ->
                if (w.runAttemptCount > 0) "in attesa di nuovo tentativo (${w.runAttemptCount})" else "in attesa di rete"
            WorkInfo.State.BLOCKED -> "in coda"
            WorkInfo.State.SUCCEEDED -> "completato"
            WorkInfo.State.FAILED -> "fallito"
            WorkInfo.State.CANCELLED -> "annullato"
        }
    }

    companion object {
        private val ACTIVE_STATES = setOf(WorkInfo.State.ENQUEUED, WorkInfo.State.RUNNING, WorkInfo.State.BLOCKED)
    }
}
