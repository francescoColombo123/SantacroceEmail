package com.company.callregistry.domain

/** Direzione della chiamata (valori identici al contratto API). */
enum class Direction { INCOMING, OUTGOING, UNKNOWN }

/** Esito della chiamata. UNKNOWN quando il sistema non fornisce informazioni affidabili. */
enum class CallStatus { ANSWERED, MISSED, REJECTED, UNKNOWN }

/** Presentazione del numero (valori identici al contratto API). */
enum class Presentation { ALLOWED, RESTRICTED, UNKNOWN, PAYPHONE }

/** Stato di sincronizzazione di un evento locale. */
enum class SyncStatus { PENDING, SYNCING, SYNCED, FAILED }

data class Classification(val direction: Direction, val status: CallStatus)

/** Problema di autenticazione che blocca gli invii finché l'amministratore non interviene. */
enum class AuthProblem(val message: String) {
    TOKEN_INVALID("Credenziali del dispositivo non più valide: serve un nuovo codice di attivazione."),
    DEVICE_DISABLED("Dispositivo disattivato dall'amministratore."),
    DEVICE_MISMATCH("Identificativo del dispositivo non corrispondente: serve un nuovo codice di attivazione."),
}
