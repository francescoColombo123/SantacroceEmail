package com.company.callregistry.telephony

import android.content.ContentResolver
import android.database.Cursor
import android.provider.CallLog

/** Riga grezza di CallLog.Calls. */
data class RawCallLogRow(
    val id: Long,
    val number: String?,
    val presentation: Int,
    val type: Int,
    val date: Long,
    val durationSeconds: Long,
    val phoneAccountId: String?,
)

/** Lettura del registro chiamate di sistema. Richiede READ_CALL_LOG (altrimenti SecurityException). */
class CallLogReader(private val resolver: ContentResolver) {

    private val projection = arrayOf(
        CallLog.Calls._ID,
        CallLog.Calls.NUMBER,
        CallLog.Calls.NUMBER_PRESENTATION,
        CallLog.Calls.TYPE,
        CallLog.Calls.DATE,
        CallLog.Calls.DURATION,
        CallLog.Calls.PHONE_ACCOUNT_ID,
    )

    /** Righe con _ID maggiore di [lastId], in ordine crescente. */
    fun readAfter(lastId: Long): List<RawCallLogRow> =
        resolver.query(
            CallLog.Calls.CONTENT_URI, projection,
            "${CallLog.Calls._ID} > ?", arrayOf(lastId.toString()),
            "${CallLog.Calls._ID} ASC",
        ).use { it.toRows() }

    /** _ID più alto presente oggi nel registro (0 se vuoto). */
    fun maxId(): Long {
        val uri = CallLog.Calls.CONTENT_URI.buildUpon()
            .appendQueryParameter(CallLog.Calls.LIMIT_PARAM_KEY, "1").build()
        return resolver.query(uri, arrayOf(CallLog.Calls._ID), null, null, "${CallLog.Calls._ID} DESC")
            ?.use { c -> if (c.moveToFirst()) c.getLong(0) else 0L } ?: 0L
    }

    private fun Cursor?.toRows(): List<RawCallLogRow> {
        if (this == null) return emptyList()
        val iId = getColumnIndexOrThrow(CallLog.Calls._ID)
        val iNum = getColumnIndexOrThrow(CallLog.Calls.NUMBER)
        val iPres = getColumnIndexOrThrow(CallLog.Calls.NUMBER_PRESENTATION)
        val iType = getColumnIndexOrThrow(CallLog.Calls.TYPE)
        val iDate = getColumnIndexOrThrow(CallLog.Calls.DATE)
        val iDur = getColumnIndexOrThrow(CallLog.Calls.DURATION)
        val iAcc = getColumnIndexOrThrow(CallLog.Calls.PHONE_ACCOUNT_ID)
        val out = ArrayList<RawCallLogRow>(count.coerceAtLeast(0))
        while (moveToNext()) {
            out += RawCallLogRow(
                id = getLong(iId),
                number = if (isNull(iNum)) null else getString(iNum),
                presentation = if (isNull(iPres)) CallLog.Calls.PRESENTATION_UNKNOWN else getInt(iPres),
                type = getInt(iType),
                date = getLong(iDate),
                durationSeconds = getLong(iDur),
                phoneAccountId = if (isNull(iAcc)) null else getString(iAcc),
            )
        }
        return out
    }
}
