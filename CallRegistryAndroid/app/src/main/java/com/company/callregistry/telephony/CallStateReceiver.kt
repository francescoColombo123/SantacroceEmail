package com.company.callregistry.telephony

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.telephony.TelephonyManager
import com.company.callregistry.sync.Scheduling

/**
 * Riceve android.intent.action.PHONE_STATE anche con l'app chiusa e lo usa solo come
 * "sveglia": al ritorno in IDLE (chiamata terminata) accoda la lettura del registro
 * chiamate, che resta l'unica fonte di verità. Nessun dato viene letto dal broadcast.
 */
class CallStateReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != TelephonyManager.ACTION_PHONE_STATE_CHANGED) return
        if (intent.getStringExtra(TelephonyManager.EXTRA_STATE) != TelephonyManager.EXTRA_STATE_IDLE) return
        Scheduling.enqueueImport(context, source = "FINE_CHIAMATA", afterCallEnd = true)
    }
}
