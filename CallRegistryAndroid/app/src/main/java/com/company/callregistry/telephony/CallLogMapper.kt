package com.company.callregistry.telephony

import android.provider.CallLog
import com.company.callregistry.domain.CallStatus
import com.company.callregistry.domain.Classification
import com.company.callregistry.domain.Direction
import com.company.callregistry.domain.Presentation

/**
 * Traduce i valori del registro chiamate di sistema nel modello del progetto.
 * Validato su Samsung SM-A165F / Android 16 (risposta, persa, rifiutata, uscita).
 *
 * Scelte conservative, mai inventare un esito:
 * - OUTGOING con durata 0 → UNKNOWN (nessuna risposta / occupato / chiusa: indistinguibili);
 * - INCOMING con durata 0 → UNKNOWN (alcuni dialer registrano così i rifiuti);
 * - BLOCKED → REJECTED; VOICEMAIL / ANSWERED_EXTERNALLY → UNKNOWN.
 */
object CallLogMapper {

    fun classify(rawType: Int, durationSeconds: Long): Classification = when (rawType) {
        CallLog.Calls.INCOMING_TYPE -> Classification(
            Direction.INCOMING, if (durationSeconds > 0) CallStatus.ANSWERED else CallStatus.UNKNOWN,
        )
        CallLog.Calls.OUTGOING_TYPE -> Classification(
            Direction.OUTGOING, if (durationSeconds > 0) CallStatus.ANSWERED else CallStatus.UNKNOWN,
        )
        CallLog.Calls.MISSED_TYPE -> Classification(Direction.INCOMING, CallStatus.MISSED)
        CallLog.Calls.REJECTED_TYPE -> Classification(Direction.INCOMING, CallStatus.REJECTED)
        CallLog.Calls.BLOCKED_TYPE -> Classification(Direction.INCOMING, CallStatus.REJECTED)
        CallLog.Calls.VOICEMAIL_TYPE -> Classification(Direction.INCOMING, CallStatus.UNKNOWN)
        CallLog.Calls.ANSWERED_EXTERNALLY_TYPE -> Classification(Direction.INCOMING, CallStatus.UNKNOWN)
        else -> Classification(Direction.UNKNOWN, CallStatus.UNKNOWN)
    }

    fun presentation(raw: Int): Presentation = when (raw) {
        CallLog.Calls.PRESENTATION_ALLOWED -> Presentation.ALLOWED
        CallLog.Calls.PRESENTATION_RESTRICTED -> Presentation.RESTRICTED
        CallLog.Calls.PRESENTATION_PAYPHONE -> Presentation.PAYPHONE
        else -> Presentation.UNKNOWN
    }

    /** Il numero si conserva solo se la presentazione è "consentita" e non vuoto. */
    fun normalizeNumber(raw: String?, presentation: Presentation): String? =
        if (presentation == Presentation.ALLOWED) raw?.trim()?.takeIf { it.isNotEmpty() } else null
}
