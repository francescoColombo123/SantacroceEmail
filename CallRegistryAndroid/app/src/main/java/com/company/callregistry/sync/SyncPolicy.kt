package com.company.callregistry.sync

import com.company.callregistry.domain.AuthProblem
import com.company.callregistry.domain.SyncStatus

/**
 * Regole (pure, testabili) che traducono le risposte del server in azioni locali.
 * Vedi la tabella degli esiti in SCemail/Mobile/README.md.
 */
object SyncPolicy {

    /** Esito per singola chiamata → nuovo stato locale. null = risultato sconosciuto: si ritenta. */
    fun statusForResult(result: String): SyncStatus? = when (result) {
        "created", "updated", "unchanged", "stale" -> SyncStatus.SYNCED
        "invalid", "conflict" -> SyncStatus.FAILED
        else -> null
    }

    sealed interface BatchAction {
        /** 200: applicare gli esiti per chiamata. */
        data object ApplyResults : BatchAction
        /** Errore temporaneo (rete, timeout, 5xx, 408, 429): conservare e ritentare con backoff. */
        data object Retry : BatchAction
        /** Errore definitivo sul lotto (400, 413, 422): segnare FAILED, non ritentare in automatico. */
        data object FailBatch : BatchAction
        /** Credenziali non valide o dispositivo disattivato: fermare gli invii. */
        data class Stop(val problem: AuthProblem) : BatchAction
    }

    fun actionForHttp(code: Int, errorCode: String?): BatchAction = when {
        code in 200..299 -> BatchAction.ApplyResults
        code == 401 -> BatchAction.Stop(AuthProblem.TOKEN_INVALID)
        code == 403 && errorCode == "device_mismatch" -> BatchAction.Stop(AuthProblem.DEVICE_MISMATCH)
        code == 403 -> BatchAction.Stop(AuthProblem.DEVICE_DISABLED)
        code == 408 || code == 429 || code >= 500 -> BatchAction.Retry
        code in 400..499 -> BatchAction.FailBatch
        else -> BatchAction.Retry
    }
}
