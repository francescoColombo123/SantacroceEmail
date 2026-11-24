package com.company.callregistry.ui.setup

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.LifecycleResumeEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.company.callregistry.R
import com.company.callregistry.ui.AppCard
import com.company.callregistry.ui.GradientHeader
import com.company.callregistry.ui.IconBadge
import com.company.callregistry.ui.StatusRow
import com.company.callregistry.ui.theme.StatusColors

@Composable
fun SetupScreen(
    viewModel: SetupViewModel,
    onRequestPermissions: () -> Unit,
    onOpenAppSettings: () -> Unit,
    onDone: () -> Unit,
    onCancel: (() -> Unit)?,
) {
    LifecycleResumeEffect(Unit) {
        viewModel.refreshPermissions()
        onPauseOrDispose { }
    }
    val s by viewModel.state.collectAsStateWithLifecycle()

    Column(
        Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            .imePadding()
            .verticalScroll(rememberScrollState()),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        GradientHeader {
            Row(verticalAlignment = Alignment.CenterVertically) {
                IconBadge(R.drawable.ic_phone, Color.White, size = 52.dp, background = Color.White.copy(alpha = 0.16f))
                Spacer(Modifier.width(14.dp))
                Column {
                    Text(
                        if (s.reactivation) "Riattivazione" else "Benvenuto",
                        style = MaterialTheme.typography.headlineSmall,
                        color = Color.White,
                    )
                    Text("Registro chiamate aziendale", style = MaterialTheme.typography.bodyMedium, color = Color.White.copy(alpha = 0.85f))
                }
            }
            Spacer(Modifier.height(14.dp))
            Text(
                "Tre passaggi e il telefono inizierà a registrare le chiamate su SCemail. " +
                    "Il codice di attivazione si genera dalla pagina Chiamate (amministratori).",
                style = MaterialTheme.typography.bodySmall,
                color = Color.White.copy(alpha = 0.85f),
            )
        }

        // ---------------------------------------------------------------- 1. permessi
        AppCard(Modifier.padding(horizontal = 16.dp)) {
            StepHeader(1, "Permessi", done = s.permissionsGranted)
            if (s.permissionsGranted) {
                StatusRow(R.drawable.ic_check_circle, StatusColors.ok, "Telefono e Registro chiamate", "Concessi")
            } else {
                StatusRow(
                    R.drawable.ic_block, StatusColors.error, "Telefono e Registro chiamate",
                    "Servono per sapere quando finisce una chiamata e chi ha chiamato.",
                )
                Text(
                    "Se il Registro chiamate risulta \"con restrizioni\": Impostazioni app → ⋮ → " +
                        "\"Consenti impostazioni con restrizioni\", poi torna qui.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = onRequestPermissions) { Text("Concedi permessi") }
                    TextButton(onClick = onOpenAppSettings) { Text("Impostazioni app") }
                }
            }
        }

        // ---------------------------------------------------------------- 2. dati
        AppCard(Modifier.padding(horizontal = 16.dp)) {
            StepHeader(2, "Questo telefono", done = s.deviceName.isNotBlank() && s.ownerName.isNotBlank())
            Field("Nome dispositivo", s.deviceName, viewModel::onDeviceName, KeyboardCapitalization.Sentences, supporting = "Es. \"Centralino magazzino\"")
            Field("Assegnatario", s.ownerName, viewModel::onOwnerName, KeyboardCapitalization.Words)
            Field("Numero SIM (facoltativo)", s.simNumber, viewModel::onSimNumber, keyboardType = KeyboardType.Phone)
        }

        // ---------------------------------------------------------------- 3. server
        AppCard(Modifier.padding(horizontal = 16.dp)) {
            StepHeader(3, "Collegamento a SCemail", done = false)
            Field(
                "Indirizzo server", s.serverUrl, viewModel::onServerUrl,
                keyboardType = KeyboardType.Uri,
                supporting = "Già compilato: cambialo solo se te lo chiede l'amministratore",
            )
            Field(
                "Codice di attivazione", s.activationCode, viewModel::onActivationCode,
                KeyboardCapitalization.Characters,
                supporting = "Formato XXXX-XXXX-XXXX, valido una sola volta",
                imeAction = ImeAction.Done,
            )
        }

        s.error?.let { err ->
            AppCard(Modifier.padding(horizontal = 16.dp), color = MaterialTheme.colorScheme.errorContainer) {
                StatusRow(R.drawable.ic_warning, StatusColors.error, "Attivazione non riuscita", err)
            }
        }

        Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Button(
                onClick = { viewModel.submit(onDone) },
                enabled = s.canSubmit,
                modifier = Modifier.fillMaxWidth().height(54.dp),
                shape = RoundedCornerShape(16.dp),
            ) {
                if (s.busy) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                    Spacer(Modifier.width(10.dp))
                    Text("Collegamento in corso…")
                } else {
                    Icon(painterResource(R.drawable.ic_check_circle), null, Modifier.size(20.dp))
                    Spacer(Modifier.width(10.dp))
                    Text(if (s.reactivation) "Riattiva" else "Attiva telefono", style = MaterialTheme.typography.titleSmall)
                }
            }
            if (!s.permissionsGranted) {
                Text(
                    "Concedi prima i permessi (passaggio 1).",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (onCancel != null && !s.busy) {
                FilledTonalButton(onClick = onCancel, modifier = Modifier.fillMaxWidth()) { Text("Annulla") }
            }
        }
        Spacer(Modifier.navigationBarsPadding().height(16.dp))
    }
}

@Composable
private fun StepHeader(number: Int, title: String, done: Boolean) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(
            Modifier
                .size(30.dp)
                .clip(CircleShape)
                .background(if (done) StatusColors.ok else MaterialTheme.colorScheme.primary),
            contentAlignment = Alignment.Center,
        ) {
            if (done) Icon(painterResource(R.drawable.ic_check_circle), null, tint = Color.White, modifier = Modifier.size(18.dp))
            else Text(number.toString(), color = MaterialTheme.colorScheme.onPrimary, fontWeight = FontWeight.Bold)
        }
        Spacer(Modifier.width(12.dp))
        Text(title, style = MaterialTheme.typography.titleMedium)
    }
}

@Composable
private fun Field(
    label: String,
    value: String,
    onChange: (String) -> Unit,
    capitalization: KeyboardCapitalization = KeyboardCapitalization.None,
    keyboardType: KeyboardType = KeyboardType.Text,
    supporting: String? = null,
    imeAction: ImeAction = ImeAction.Next,
) {
    OutlinedTextField(
        value = value,
        onValueChange = onChange,
        label = { Text(label) },
        singleLine = true,
        shape = RoundedCornerShape(14.dp),
        supportingText = supporting?.let { { Text(it) } },
        keyboardOptions = KeyboardOptions(capitalization = capitalization, keyboardType = keyboardType, imeAction = imeAction),
        modifier = Modifier.fillMaxWidth(),
    )
}
