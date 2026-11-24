package com.company.callregistry.ui.main

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.LifecycleResumeEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.company.callregistry.R
import com.company.callregistry.data.local.CallEventEntity
import com.company.callregistry.diag.DeviceStatus
import com.company.callregistry.diag.DiagLogger.Companion.formatTime
import com.company.callregistry.ui.AppCard
import com.company.callregistry.ui.CardTitle
import com.company.callregistry.ui.GradientHeader
import com.company.callregistry.ui.IconBadge
import com.company.callregistry.ui.KeyValue
import com.company.callregistry.ui.StatTile
import com.company.callregistry.ui.StatusPill
import com.company.callregistry.ui.StatusRow
import com.company.callregistry.ui.callVisual
import com.company.callregistry.ui.durationLabel
import com.company.callregistry.ui.numberLabel
import com.company.callregistry.ui.relativeTime
import com.company.callregistry.ui.theme.StatusColors
import kotlinx.coroutines.delay

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun MainScreen(
    viewModel: MainViewModel,
    onRequestPermissions: () -> Unit,
    onOpenAppSettings: () -> Unit,
    onRequestBatteryExemption: () -> Unit,
    onShareDiagnostics: () -> Unit,
    onReactivate: () -> Unit,
) {
    LifecycleResumeEffect(Unit) {
        viewModel.onResume()
        onPauseOrDispose { }
    }
    val info by viewModel.info.collectAsStateWithLifecycle()
    val status by viewModel.status.collectAsStateWithLifecycle()
    val api by viewModel.api.collectAsStateWithLifecycle()
    val recent by viewModel.recent.collectAsStateWithLifecycle()
    val unsynced by viewModel.unsynced.collectAsStateWithLifecycle()
    val failed by viewModel.failed.collectAsStateWithLifecycle()
    val missedToday by viewModel.missedToday.collectAsStateWithLifecycle()
    val observerActive by viewModel.observerActive.collectAsStateWithLifecycle()
    val syncState by viewModel.syncState.collectAsStateWithLifecycle()
    val message by viewModel.message.collectAsStateWithLifecycle()
    val busy by viewModel.busy.collectAsStateWithLifecycle()
    val logs by viewModel.logs.collectAsStateWithLifecycle()
    var showDetails by remember { mutableStateOf(false) }
    var showLogs by remember { mutableStateOf(false) }

    // Aggiorna i tempi relativi ("5 min fa") mentre la schermata è aperta.
    val now by produceState(System.currentTimeMillis()) {
        while (true) { delay(30_000); value = System.currentTimeMillis() }
    }

    val s = status
    val issues = s?.monitoringIssues(observerActive).orEmpty()
    val apiOk = api is ApiState.Ok
    val allGood = s != null && issues.isEmpty() && info.authProblem == null && (apiOk || api is ApiState.Checking)

    LazyColumn(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background),
        contentPadding = PaddingValues(bottom = 24.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        // ---------------------------------------------------------------- intestazione
        item {
            GradientHeader {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("REGISTRO CHIAMATE", style = MaterialTheme.typography.labelMedium, color = Color.White.copy(alpha = 0.75f))
                        Text(
                            info.deviceName.ifEmpty { "Telefono aziendale" },
                            style = MaterialTheme.typography.headlineSmall,
                            color = Color.White,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Text(
                            info.ownerName + (info.simNumber?.let { "  ·  $it" } ?: ""),
                            style = MaterialTheme.typography.bodyMedium,
                            color = Color.White.copy(alpha = 0.85f),
                        )
                    }
                    IconBadge(R.drawable.ic_phone, Color.White, size = 52.dp, background = Color.White.copy(alpha = 0.16f))
                }
                Spacer(Modifier.height(16.dp))
                StatusPill(
                    text = when {
                        s == null -> "Verifica in corso…"
                        allGood -> "Monitoraggio attivo"
                        info.authProblem != null -> "Invii sospesi"
                        else -> "Richiede attenzione"
                    },
                    dot = when {
                        s == null -> Color.White
                        allGood -> Color(0xFF6BE38F)
                        else -> Color(0xFFFFB74D)
                    },
                    background = Color.White.copy(alpha = 0.16f),
                    textColor = Color.White,
                )
            }
        }

        // ---------------------------------------------------------------- numeri principali
        item {
            Row(Modifier.padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                StatTile(
                    "Perse oggi", missedToday.toString(), R.drawable.ic_call_missed,
                    if (missedToday > 0) StatusColors.error else StatusColors.neutral, Modifier.weight(1f),
                )
                StatTile(
                    "Da inviare", unsynced.toString(), R.drawable.ic_cloud,
                    if (unsynced > 0) StatusColors.warn else StatusColors.ok, Modifier.weight(1f),
                )
                StatTile("Ultimo invio", relativeTime(info.lastSyncAt, now), R.drawable.ic_sync, StatusColors.info, Modifier.weight(1f))
            }
        }

        // ---------------------------------------------------------------- invii sospesi
        info.authProblem?.let { problem ->
            item {
                AppCard(Modifier.padding(horizontal = 16.dp), color = MaterialTheme.colorScheme.errorContainer) {
                    StatusRow(R.drawable.ic_warning, StatusColors.error, "Invii sospesi", problem.message)
                    Text(
                        "Le chiamate continuano a essere registrate sul telefono e partiranno dopo la riattivazione.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onErrorContainer,
                    )
                    Button(onClick = onReactivate, modifier = Modifier.fillMaxWidth()) { Text("Inserisci nuovo codice") }
                }
            }
        }

        // ---------------------------------------------------------------- stato servizio
        item {
            AppCard(Modifier.padding(horizontal = 16.dp)) {
                CardTitle("Stato del servizio", R.drawable.ic_shield)
                StatusRow(
                    if (s != null && issues.isEmpty()) R.drawable.ic_check_circle else R.drawable.ic_warning,
                    when { s == null -> StatusColors.neutral; issues.isEmpty() -> StatusColors.ok; else -> StatusColors.error },
                    "Rilevamento chiamate",
                    when { s == null -> "verifica…"; issues.isEmpty() -> "Attivo anche con app chiusa e schermo bloccato"; else -> issues.joinToString("\n") },
                )
                val (apiIcon, apiColor, apiText) = when (val a = api) {
                    ApiState.Checking -> Triple(R.drawable.ic_schedule, StatusColors.neutral, "verifica in corso…")
                    ApiState.Ok -> Triple(R.drawable.ic_check_circle, StatusColors.ok, "Collegato")
                    is ApiState.Problem -> Triple(R.drawable.ic_warning, if (a.blocking) StatusColors.error else StatusColors.warn, a.message)
                }
                StatusRow(apiIcon, apiColor, "Server SCemail", apiText)
                when {
                    s?.airplaneMode == true ->
                        StatusRow(R.drawable.ic_warning, StatusColors.warn, "Modalità aereo attiva", "Gli invii ripartono da soli alla disattivazione")
                    s != null && !s.networkAvailable ->
                        StatusRow(R.drawable.ic_warning, StatusColors.warn, "Rete assente", "Le chiamate restano sul telefono fino al ritorno della rete")
                    s != null -> StatusRow(R.drawable.ic_check_circle, StatusColors.ok, "Rete", s.network)
                }
                if (unsynced > 0 || failed > 0) {
                    StatusRow(
                        R.drawable.ic_cloud, if (failed > 0) StatusColors.error else StatusColors.warn,
                        "Invio in background: $syncState",
                        buildString {
                            if (failed > 0) append("$failed chiamate rifiutate dal server. ")
                            info.lastSyncError?.let { append("Ultimo errore: $it") }
                        },
                    )
                }
            }
        }

        // ---------------------------------------------------------------- azioni
        item {
            Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = viewModel::syncNow,
                    enabled = !busy,
                    modifier = Modifier.fillMaxWidth().height(52.dp),
                    shape = RoundedCornerShape(16.dp),
                ) {
                    if (busy) {
                        CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                    } else {
                        Icon(painterResource(R.drawable.ic_sync), null, Modifier.size(20.dp))
                    }
                    Spacer(Modifier.width(10.dp))
                    Text(if (busy) "Sincronizzazione…" else "Sincronizza ora", style = MaterialTheme.typography.titleSmall)
                }
                if (failed > 0) {
                    OutlinedButton(onClick = viewModel::retryFailed, modifier = Modifier.fillMaxWidth()) { Text("Riprova le $failed rifiutate") }
                }
                message?.let {
                    Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }

        // ---------------------------------------------------------------- permessi (solo se serve)
        if (s != null && (s.missingPermissions.isNotEmpty() || !s.batteryOptimizationIgnored)) {
            item { PermissionsCard(s, onRequestPermissions, onOpenAppSettings, onRequestBatteryExemption) }
        }

        // ---------------------------------------------------------------- chiamate
        item {
            AppCard(Modifier.padding(horizontal = 16.dp)) {
                CardTitle("Chiamate recenti", R.drawable.ic_phone)
                if (recent.isEmpty()) {
                    Text(
                        "Nessuna chiamata dall'attivazione. Le nuove chiamate compariranno qui e in SCemail.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                recent.forEachIndexed { i, e ->
                    if (i > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
                    CallRow(e, now)
                }
            }
        }

        // ---------------------------------------------------------------- dettagli tecnici
        item {
            AppCard(Modifier.padding(horizontal = 16.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) { CardTitle("Dettagli e diagnostica", R.drawable.ic_info, MaterialTheme.colorScheme.onSurfaceVariant) }
                    TextButton(onClick = { showDetails = !showDetails }) { Text(if (showDetails) "Nascondi" else "Mostra") }
                }
                if (showDetails) {
                    KeyValue("Server", info.serverUrl)
                    KeyValue("Id dispositivo", info.deviceId.toString())
                    KeyValue("Installazione", info.installationUuid)
                    KeyValue("Ultima lettura registro", formatTime(info.lastImportAt))
                    KeyValue("Ultimo invio riuscito", formatTime(info.lastSyncAt))
                    KeyValue("Invio in background", syncState)
                    s?.let {
                        KeyValue("Telefono", "${it.device} · Android ${it.androidRelease}")
                        KeyValue("Standby", it.standbyBucket)
                        KeyValue("Ottimizzazione batteria", if (it.batteryOptimizationIgnored) "esclusa" else "attiva")
                    }
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilledTonalButton(onClick = onShareDiagnostics) { Text("Condividi diagnostica") }
                        OutlinedButton(onClick = { showLogs = !showLogs }) { Text(if (showLogs) "Nascondi log" else "Mostra log") }
                        TextButton(onClick = onReactivate) { Text("Riattiva / cambia server") }
                    }
                }
            }
        }

        if (showDetails && showLogs) {
            items(logs, key = { it.id }) { l ->
                Text(
                    "${formatTime(l.timestamp)} [${l.source}] ${l.message}",
                    fontFamily = FontFamily.Monospace,
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(horizontal = 20.dp),
                )
            }
        }

        item { Spacer(Modifier.navigationBarsPadding()) }
    }
}

@Composable
private fun CallRow(e: CallEventEntity, now: Long) {
    val v = callVisual(e)
    Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        IconBadge(v.icon, v.color)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(
                numberLabel(e),
                style = MaterialTheme.typography.bodyLarge,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                listOf(v.label, relativeTime(e.startedAt, now), durationLabel(e.durationSeconds)).filter { it.isNotEmpty() }.joinToString("  ·  "),
                style = MaterialTheme.typography.bodySmall,
                color = if (e.status == "MISSED") v.color else MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        val (icon, color) = when (e.syncStatus) {
            "SYNCED" -> R.drawable.ic_check_circle to StatusColors.ok
            "FAILED" -> R.drawable.ic_warning to StatusColors.error
            else -> R.drawable.ic_schedule to StatusColors.warn
        }
        Icon(painterResource(icon), contentDescription = when (e.syncStatus) {
            "SYNCED" -> "inviata"; "FAILED" -> "rifiutata dal server"; else -> "da inviare"
        }, tint = color, modifier = Modifier.size(18.dp))
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun PermissionsCard(
    s: DeviceStatus,
    onRequestPermissions: () -> Unit,
    onOpenAppSettings: () -> Unit,
    onRequestBatteryExemption: () -> Unit,
) {
    AppCard(Modifier.padding(horizontal = 16.dp)) {
        CardTitle("Da sistemare", R.drawable.ic_warning, StatusColors.warn)
        s.permissions.filterNot { it.granted }.forEach {
            StatusRow(R.drawable.ic_block, StatusColors.error, "Permesso mancante", it.label)
        }
        if (!s.batteryOptimizationIgnored) {
            StatusRow(R.drawable.ic_battery, StatusColors.warn, "Ottimizzazione batteria attiva", "Android potrebbe ritardare il rilevamento")
        }
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            if (s.missingPermissions.isNotEmpty()) Button(onClick = onRequestPermissions) { Text("Concedi permessi") }
            if (!s.batteryOptimizationIgnored) FilledTonalButton(onClick = onRequestBatteryExemption) { Text("Escludi da ottimizzazione") }
            TextButton(onClick = onOpenAppSettings) { Text("Impostazioni app") }
        }
    }
}
