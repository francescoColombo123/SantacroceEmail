package com.company.callregistry.telephony

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.company.callregistry.container
import com.company.callregistry.sync.Scheduling

/**
 * Dopo un riavvio o un aggiornamento dell'app: ripristina i job e recupera eventuali
 * chiamate avvenute mentre l'app non poteva girare.
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val source = when (intent.action) {
            Intent.ACTION_BOOT_COMPLETED -> "AVVIO"
            Intent.ACTION_MY_PACKAGE_REPLACED -> "AGGIORNAMENTO"
            else -> return
        }
        context.container.logger.log("BOOT", "Evento $source: ripristino job e lettura registro")
        Scheduling.ensureBackgroundWork(context)
        // L'import accoda a sua volta l'invio se trova eventi non inviati.
        Scheduling.enqueueImport(context, source = source, afterCallEnd = false)
    }
}
