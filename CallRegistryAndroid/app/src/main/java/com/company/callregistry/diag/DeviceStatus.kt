package com.company.callregistry.diag

import android.Manifest
import android.app.ActivityManager
import android.app.usage.UsageStatsManager
import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.core.content.ContextCompat
import com.company.callregistry.telephony.CallStateReceiver

data class PermissionStatus(val name: String, val label: String, val granted: Boolean)

/** Istantanea di ciò che influenza rilevamento e invio, mostrata nella UI ed esportata. */
data class DeviceStatus(
    val androidRelease: String,
    val sdkInt: Int,
    val device: String,
    val permissions: List<PermissionStatus>,
    val batteryOptimizationIgnored: Boolean,
    val powerSaveMode: Boolean,
    val backgroundRestricted: Boolean,
    val standbyBucket: String,
    val network: String,
    val networkAvailable: Boolean,
    val airplaneMode: Boolean,
    val receiverEnabled: Boolean,
) {
    val missingPermissions get() = permissions.filterNot { it.granted }

    /** Problemi che impediscono o degradano il monitoraggio (vuoto = tutto a posto). */
    fun monitoringIssues(observerScheduled: Boolean): List<String> = buildList {
        missingPermissions.forEach { add("Permesso mancante: ${it.label}") }
        if (!receiverEnabled) add("Ricevitore stato telefono disabilitato")
        if (!observerScheduled) add("Controllo del registro chiamate non pianificato")
        if (backgroundRestricted) add("Attività in background limitata: impostare la batteria su \"Senza restrizioni\"")
        if (standbyBucket == "RESTRICTED") add("App in standby limitato dal sistema")
    }

    companion object {
        val REQUIRED_PERMISSIONS = listOf(
            Manifest.permission.READ_PHONE_STATE to "Telefono (stato chiamate)",
            Manifest.permission.READ_CALL_LOG to "Registro chiamate",
        )

        fun collect(ctx: Context): DeviceStatus {
            val pm = ctx.getSystemService(PowerManager::class.java)
            val am = ctx.getSystemService(ActivityManager::class.java)
            val usm = ctx.getSystemService(UsageStatsManager::class.java)
            val receiverState = ctx.packageManager.getComponentEnabledSetting(
                ComponentName(ctx, CallStateReceiver::class.java),
            )
            val (network, available) = networkInfo(ctx)
            return DeviceStatus(
                androidRelease = Build.VERSION.RELEASE,
                sdkInt = Build.VERSION.SDK_INT,
                device = "${Build.MANUFACTURER} ${Build.MODEL}",
                permissions = REQUIRED_PERMISSIONS.map { (p, label) ->
                    PermissionStatus(p, label, ContextCompat.checkSelfPermission(ctx, p) == PackageManager.PERMISSION_GRANTED)
                },
                batteryOptimizationIgnored = pm?.isIgnoringBatteryOptimizations(ctx.packageName) == true,
                powerSaveMode = pm?.isPowerSaveMode == true,
                backgroundRestricted = am?.isBackgroundRestricted == true,
                standbyBucket = bucketLabel(usm?.appStandbyBucket),
                network = network,
                networkAvailable = available,
                airplaneMode = Settings.Global.getInt(ctx.contentResolver, Settings.Global.AIRPLANE_MODE_ON, 0) != 0,
                receiverEnabled = receiverState != PackageManager.COMPONENT_ENABLED_STATE_DISABLED &&
                    receiverState != PackageManager.COMPONENT_ENABLED_STATE_DISABLED_USER,
            )
        }

        private fun bucketLabel(bucket: Int?): String = when (bucket) {
            UsageStatsManager.STANDBY_BUCKET_ACTIVE -> "ACTIVE"
            UsageStatsManager.STANDBY_BUCKET_WORKING_SET -> "WORKING_SET"
            UsageStatsManager.STANDBY_BUCKET_FREQUENT -> "FREQUENT"
            UsageStatsManager.STANDBY_BUCKET_RARE -> "RARE"
            UsageStatsManager.STANDBY_BUCKET_RESTRICTED -> "RESTRICTED"
            null -> "—"
            else -> "bucket $bucket"
        }

        private fun networkInfo(ctx: Context): Pair<String, Boolean> {
            val cm = ctx.getSystemService(ConnectivityManager::class.java) ?: return "—" to false
            val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return "assente" to false
            val type = when {
                caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "Wi-Fi"
                caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "Rete mobile"
                caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) -> "VPN"
                else -> "Altro"
            }
            val validated = caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
            return "$type${if (validated) "" else " (Internet non verificato)"}" to true
        }
    }
}
