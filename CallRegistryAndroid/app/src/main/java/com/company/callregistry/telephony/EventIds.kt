package com.company.callregistry.telephony

import java.util.UUID

/**
 * eventUuid deterministico: la stessa riga del CallLog produce sempre lo stesso UUID
 * per questa installazione. Rileggere il registro (riconciliazione, riavvio, retry)
 * non può quindi generare duplicati né in locale né sul server.
 */
object EventIds {
    fun forCallLogRow(installationUuid: String, callLogId: Long, dateMillis: Long): String =
        UUID.nameUUIDFromBytes("$installationUuid|$callLogId|$dateMillis".toByteArray(Charsets.UTF_8)).toString()
}
