package com.company.callregistry.ui

import android.content.Intent
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.core.net.toUri
import com.company.callregistry.ui.theme.CallRegistryTheme
import androidx.lifecycle.lifecycleScope
import com.company.callregistry.container
import com.company.callregistry.diag.DeviceStatus
import com.company.callregistry.ui.main.MainScreen
import com.company.callregistry.ui.main.MainViewModel
import com.company.callregistry.ui.setup.SetupScreen
import com.company.callregistry.ui.setup.SetupViewModel
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {

    private val mainViewModel: MainViewModel by viewModels()
    private val setupViewModel: SetupViewModel by viewModels()

    private val permissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
            val summary = result.entries.joinToString { "${it.key.substringAfterLast('.')}=${if (it.value) "OK" else "negato"}" }
            container.logger.log("UI", "Risposta permessi: $summary")
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Icone della barra di stato chiare: sopra c'è sempre l'intestazione blu.
        enableEdgeToEdge(statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT))
        val configured = container.prefs.isConfigured
        setContent {
            var showSetup by rememberSaveable { mutableStateOf(!configured) }
            CallRegistryTheme {
                if (showSetup) {
                    SetupScreen(
                        viewModel = setupViewModel,
                        onRequestPermissions = ::requestCallPermissions,
                        onOpenAppSettings = ::openAppSettings,
                        onDone = { showSetup = false },
                        onCancel = if (container.prefs.isConfigured) ({ showSetup = false }) else null,
                    )
                } else {
                    MainScreen(
                        viewModel = mainViewModel,
                        onRequestPermissions = ::requestCallPermissions,
                        onOpenAppSettings = ::openAppSettings,
                        onRequestBatteryExemption = ::requestBatteryExemption,
                        onShareDiagnostics = ::shareDiagnostics,
                        onReactivate = { showSetup = true },
                    )
                }
            }
        }
    }

    private fun requestCallPermissions() {
        permissionLauncher.launch(DeviceStatus.REQUIRED_PERMISSIONS.map { it.first }.toTypedArray())
    }

    /** Permessi negati definitivamente o "con restrizioni" (APK installato fuori dallo store) si concedono da qui. */
    private fun openAppSettings() {
        startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, "package:$packageName".toUri()))
    }

    private fun requestBatteryExemption() {
        val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, "package:$packageName".toUri())
        runCatching { startActivity(intent) }
            .onFailure { startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)) }
    }

    private fun shareDiagnostics() {
        lifecycleScope.launch {
            val text = mainViewModel.buildReport()
            val send = Intent(Intent.ACTION_SEND).apply {
                type = "text/plain"
                putExtra(Intent.EXTRA_SUBJECT, "Call Registry – diagnostica")
                putExtra(Intent.EXTRA_TEXT, text)
            }
            startActivity(Intent.createChooser(send, "Condividi diagnostica"))
        }
    }
}
