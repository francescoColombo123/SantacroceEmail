package com.company.callregistry.sync

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import com.company.callregistry.container
import com.company.callregistry.data.repository.CallRepository.ImportResult
import com.company.callregistry.data.repository.CallRepository.SyncResult

/**
 * Importa le nuove righe del registro chiamate e, se ce ne sono, avvia l'invio.
 * Dopo la fine di una chiamata il dialer può scrivere la riga con qualche istante di
 * ritardo: in quel caso ritenta poche volte; il trigger sul CallLog resta la rete di sicurezza.
 */
class ImportWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val c = applicationContext.container
        val source = inputData.getString(KEY_SOURCE) ?: "?"
        val afterCallEnd = inputData.getBoolean(KEY_AFTER_CALL_END, false)
        return when (val r = c.callRepository.importNewCalls(source)) {
            is ImportResult.Imported -> {
                if (r.count > 0 || c.callRepository.hasUnsynced()) Scheduling.enqueueSync(applicationContext, source)
                if (r.count == 0 && afterCallEnd && runAttemptCount < MAX_RETRY_EMPTY) Result.retry() else Result.success()
            }
            ImportResult.NotConfigured, ImportResult.MissingPermission -> Result.success()
            is ImportResult.Failed -> if (runAttemptCount < MAX_RETRY_ERROR) Result.retry() else Result.failure()
        }
    }

    companion object {
        const val KEY_SOURCE = "source"
        const val KEY_AFTER_CALL_END = "after_call_end"
        private const val MAX_RETRY_EMPTY = 2
        private const val MAX_RETRY_ERROR = 5
    }
}

/**
 * Invia gli eventi in attesa. Parte solo con rete disponibile; in caso di errore temporaneo
 * WorkManager ritenta con backoff esponenziale (30 s, 1 min, 2 min, … fino a 5 h) senza limite
 * di tentativi: gli eventi restano in Room finché il server non li conferma.
 */
class SyncWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val c = applicationContext.container
        val source = inputData.getString(KEY_SOURCE) ?: "?"
        return when (c.callRepository.syncPending(source)) {
            SyncResult.RETRY -> Result.retry()
            SyncResult.DONE, SyncResult.NOTHING_TO_DO, SyncResult.STOPPED, SyncResult.NOT_CONFIGURED -> Result.success()
        }
    }

    companion object {
        const val KEY_SOURCE = "source"
    }
}

/** Avviato da Android quando il CallLog cambia: funziona anche se il broadcast è andato perso. */
class CallLogObserverWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        Scheduling.enqueueImport(applicationContext, source = "TRIGGER_CALLLOG", afterCallEnd = false)
        Scheduling.rearmObserver(applicationContext)
        return Result.success()
    }
}

/**
 * Ogni 6 ore: importa eventuali righe perse, riprova gli invii in sospeso, pulisce i dati
 * vecchi già confermati e ripristina i job se sono stati cancellati (es. arresto forzato).
 */
class MaintenanceWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val c = applicationContext.container
        c.callRepository.importNewCalls("PERIODICO")
        if (c.callRepository.hasUnsynced()) Scheduling.enqueueSync(applicationContext, "PERIODICO")
        val deleted = c.callRepository.cleanupOldSynced()
        if (deleted > 0) c.logger.logNow("MAINT", "Rimossi $deleted eventi già inviati da oltre 30 giorni")
        c.logger.trim()
        Scheduling.ensureBackgroundWork(applicationContext)
        return Result.success()
    }
}
