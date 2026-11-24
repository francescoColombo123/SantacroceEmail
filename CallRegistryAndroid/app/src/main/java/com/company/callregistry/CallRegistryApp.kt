package com.company.callregistry

import android.app.ActivityManager
import android.app.Application
import android.app.ApplicationExitInfo
import android.content.Context
import com.company.callregistry.di.AppContainer
import com.company.callregistry.diag.DiagLogger
import com.company.callregistry.sync.Scheduling
import kotlinx.coroutines.launch

/**
 * Punto di ingresso del processo, eseguito anche quando Android avvia l'app "a freddo"
 * per un broadcast o un job WorkManager.
 */
class CallRegistryApp : Application() {

    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()
        container = AppContainer(this)
        val c = container
        c.appScope.launch(c.serialIo) {
            c.logger.logNow("APP", "Processo avviato (pid=${android.os.Process.myPid()})")
            logPreviousExitReasons()
            // Un invio interrotto dalla terminazione del processo non deve restare bloccato.
            c.callRepository.recoverAfterProcessStart()
        }
        Scheduling.ensureBackgroundWork(this)
    }

    /** Motivi delle precedenti terminazioni (es. arresto forzato dal produttore): utili in diagnosi. */
    private suspend fun logPreviousExitReasons() {
        val am = getSystemService(ActivityManager::class.java) ?: return
        val lastSeen = container.prefs.lastExitInfoTimestamp
        val infos = runCatching { am.getHistoricalProcessExitReasons(packageName, 0, 5) }
            .getOrElse { return }
            .filter { it.timestamp > lastSeen }
            .sortedBy { it.timestamp }
        for (info in infos) {
            container.logger.logNow(
                "EXIT",
                "Terminazione precedente: ${exitReasonLabel(info.reason)} alle ${DiagLogger.formatTime(info.timestamp)}",
            )
        }
        infos.maxOfOrNull { it.timestamp }?.let { container.prefs.lastExitInfoTimestamp = it }
    }

    private fun exitReasonLabel(reason: Int): String = when (reason) {
        ApplicationExitInfo.REASON_EXIT_SELF -> "uscita volontaria"
        ApplicationExitInfo.REASON_SIGNALED -> "segnale del sistema"
        ApplicationExitInfo.REASON_LOW_MEMORY -> "memoria insufficiente"
        ApplicationExitInfo.REASON_CRASH -> "crash"
        ApplicationExitInfo.REASON_CRASH_NATIVE -> "crash nativo"
        ApplicationExitInfo.REASON_ANR -> "ANR"
        ApplicationExitInfo.REASON_PERMISSION_CHANGE -> "modifica permessi"
        ApplicationExitInfo.REASON_EXCESSIVE_RESOURCE_USAGE -> "uso eccessivo risorse"
        ApplicationExitInfo.REASON_USER_REQUESTED -> "arresto richiesto (forza arresto / produttore)"
        ApplicationExitInfo.REASON_USER_STOPPED -> "utente arrestato"
        ApplicationExitInfo.REASON_DEPENDENCY_DIED -> "dipendenza terminata"
        ApplicationExitInfo.REASON_OTHER -> "altro (sistema)"
        else -> "codice $reason"
    }
}

val Context.container: AppContainer get() = (applicationContext as CallRegistryApp).container
