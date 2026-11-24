package com.company.callregistry.ui

import androidx.annotation.DrawableRes
import androidx.compose.ui.graphics.Color
import com.company.callregistry.R
import com.company.callregistry.data.local.CallEventEntity
import com.company.callregistry.ui.theme.StatusColors
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale

/** Come mostrare una chiamata: etichetta, icona e colore. */
data class CallVisual(val label: String, @DrawableRes val icon: Int, val color: Color)

fun callVisual(e: CallEventEntity): CallVisual = when (e.direction) {
    "INCOMING" -> when (e.status) {
        "MISSED" -> CallVisual("Persa", R.drawable.ic_call_missed, StatusColors.error)
        "REJECTED" -> CallVisual("Rifiutata", R.drawable.ic_block, StatusColors.warn)
        "ANSWERED" -> CallVisual("Ricevuta", R.drawable.ic_call_received, StatusColors.ok)
        else -> CallVisual("In arrivo", R.drawable.ic_call_received, StatusColors.neutral)
    }
    "OUTGOING" -> if (e.status == "ANSWERED") CallVisual("Effettuata", R.drawable.ic_call_made, StatusColors.info)
    else CallVisual("Effettuata, senza risposta", R.drawable.ic_call_made, StatusColors.neutral)
    else -> CallVisual("Sconosciuta", R.drawable.ic_phone, StatusColors.neutral)
}

fun numberLabel(e: CallEventEntity): String = e.phoneNumber ?: when (e.presentation) {
    "RESTRICTED" -> "Numero privato"
    "PAYPHONE" -> "Telefono pubblico"
    else -> "Numero sconosciuto"
}

fun durationLabel(seconds: Long): String = when {
    seconds <= 0 -> ""
    seconds < 60 -> "${seconds}s"
    else -> "${seconds / 60}m ${seconds % 60}s"
}

/** "adesso", "5 min fa", "oggi 14:32", "ieri 09:10", "12/11 18:05". */
fun relativeTime(ts: Long, now: Long = System.currentTimeMillis()): String {
    if (ts <= 0) return "mai"
    val diff = now - ts
    if (diff in 0 until 60_000) return "adesso"
    if (diff in 60_000 until 3_600_000) return "${diff / 60_000} min fa"
    val time = SimpleDateFormat("HH:mm", Locale.ITALY).format(Date(ts))
    val startToday = startOfDay(now)
    return when {
        ts >= startToday -> "oggi $time"
        ts >= startToday - 86_400_000 -> "ieri $time"
        else -> SimpleDateFormat("dd/MM HH:mm", Locale.ITALY).format(Date(ts))
    }
}

fun startOfDay(now: Long = System.currentTimeMillis()): Long = Calendar.getInstance().apply {
    timeInMillis = now
    set(Calendar.HOUR_OF_DAY, 0); set(Calendar.MINUTE, 0); set(Calendar.SECOND, 0); set(Calendar.MILLISECOND, 0)
}.timeInMillis
