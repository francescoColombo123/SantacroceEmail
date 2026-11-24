package com.company.callregistry.sync

import android.content.Context
import android.provider.CallLog
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkInfo
import androidx.work.WorkManager
import androidx.work.workDataOf
import java.time.Duration
import java.util.concurrent.TimeUnit

/**
 * Tutta la pianificazione in background passa da qui. Nessun servizio permanente,
 * nessun wakelock: solo job WorkManager con nomi univoci (niente duplicati).
 */
object Scheduling {
    const val IMPORT = "import"
    const val SYNC = "sync"
    const val OBSERVER = "calllog-observer"
    const val MAINTENANCE = "maintenance"

    /** Lettura del registro chiamate (expedited quando la quota lo consente). */
    fun enqueueImport(context: Context, source: String, afterCallEnd: Boolean) {
        val request = OneTimeWorkRequestBuilder<ImportWorker>()
            .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
            .setBackoffCriteria(BackoffPolicy.LINEAR, 10, TimeUnit.SECONDS)
            .setInputData(workDataOf(ImportWorker.KEY_SOURCE to source, ImportWorker.KEY_AFTER_CALL_END to afterCallEnd))
            .build()
        // KEEP: un import già in coda leggerà anche le righe nuove; il trigger sul CallLog copre il resto.
        WorkManager.getInstance(context).enqueueUniqueWork(IMPORT, ExistingWorkPolicy.KEEP, request)
    }

    /**
     * Invio al server non appena c'è rete. Se un invio è in esecuzione se ne accoda uno dopo
     * (per gli eventi arrivati nel frattempo); altrimenti si sostituisce quello in attesa,
     * azzerando un eventuale backoff lungo: una nuova chiamata viene inviata subito.
     */
    fun enqueueSync(context: Context, source: String) {
        val wm = WorkManager.getInstance(context)
        val infos = runCatching { wm.getWorkInfosForUniqueWork(SYNC).get() }.getOrDefault(emptyList())
        val running = infos.any { it.state == WorkInfo.State.RUNNING }
        val queuedAfterRunning = infos.any { it.state == WorkInfo.State.BLOCKED }
        if (running && queuedAfterRunning) return
        val policy = if (running) ExistingWorkPolicy.APPEND_OR_REPLACE else ExistingWorkPolicy.REPLACE
        wm.enqueueUniqueWork(SYNC, policy, syncRequest(source))
    }

    private fun syncRequest(source: String) = OneTimeWorkRequestBuilder<SyncWorker>()
        .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
        .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
        .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
        .setInputData(workDataOf(SyncWorker.KEY_SOURCE to source))
        .build()

    /** Installa trigger sul CallLog e manutenzione periodica se mancano (idempotente). */
    fun ensureBackgroundWork(context: Context) {
        val wm = WorkManager.getInstance(context)
        wm.enqueueUniqueWork(OBSERVER, ExistingWorkPolicy.KEEP, observerRequest())
        wm.enqueueUniquePeriodicWork(
            MAINTENANCE,
            ExistingPeriodicWorkPolicy.KEEP,
            PeriodicWorkRequestBuilder<MaintenanceWorker>(6, TimeUnit.HOURS).build(),
        )
    }

    /** Un trigger vale per una sola esecuzione: si ri-arma accodando una nuova istanza. */
    fun rearmObserver(context: Context) {
        WorkManager.getInstance(context)
            .enqueueUniqueWork(OBSERVER, ExistingWorkPolicy.APPEND_OR_REPLACE, observerRequest())
    }

    private fun observerRequest() = OneTimeWorkRequestBuilder<CallLogObserverWorker>()
        .setConstraints(
            Constraints.Builder()
                .addContentUriTrigger(CallLog.Calls.CONTENT_URI, true)
                .setTriggerContentUpdateDelay(Duration.ofSeconds(2))
                .setTriggerContentMaxDelay(Duration.ofSeconds(10))
                .build(),
        )
        .build()
}
