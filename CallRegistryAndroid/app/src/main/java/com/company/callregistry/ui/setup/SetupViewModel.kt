package com.company.callregistry.ui.setup

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.company.callregistry.container
import com.company.callregistry.data.repository.DeviceRepository
import com.company.callregistry.diag.DeviceStatus
import com.company.callregistry.sync.Scheduling
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

data class SetupUiState(
    val deviceName: String = "",
    val ownerName: String = "",
    val simNumber: String = "",
    val serverUrl: String = "",
    val activationCode: String = "",
    val permissionsGranted: Boolean = false,
    val busy: Boolean = false,
    val error: String? = null,
    /** true se il dispositivo era già configurato (riattivazione con nuovo codice). */
    val reactivation: Boolean = false,
) {
    val canSubmit get() = !busy && permissionsGranted && deviceName.isNotBlank() && ownerName.isNotBlank() &&
        serverUrl.isNotBlank() && activationCode.isNotBlank()
}

class SetupViewModel(application: Application) : AndroidViewModel(application) {
    private val c = application.container

    private val _state = MutableStateFlow(
        SetupUiState(
            deviceName = c.prefs.deviceName.orEmpty(),
            ownerName = c.prefs.ownerName.orEmpty(),
            simNumber = c.prefs.simNumber.orEmpty(),
            serverUrl = c.prefs.serverUrl ?: com.company.callregistry.BuildConfig.DEFAULT_SERVER_URL,
            reactivation = c.prefs.deviceId > 0,
        ),
    )
    val state: StateFlow<SetupUiState> = _state.asStateFlow()

    fun refreshPermissions() {
        val s = DeviceStatus.collect(getApplication())
        _state.update { it.copy(permissionsGranted = s.missingPermissions.isEmpty()) }
    }

    fun onDeviceName(v: String) = _state.update { it.copy(deviceName = v.take(100), error = null) }
    fun onOwnerName(v: String) = _state.update { it.copy(ownerName = v.take(100), error = null) }
    fun onSimNumber(v: String) = _state.update { it.copy(simNumber = v.take(30), error = null) }
    fun onServerUrl(v: String) = _state.update { it.copy(serverUrl = v.trim(), error = null) }
    fun onActivationCode(v: String) = _state.update { it.copy(activationCode = v.uppercase().take(20), error = null) }

    fun submit(onSuccess: () -> Unit) {
        val s = _state.value
        if (!s.canSubmit) return
        _state.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            val result = withContext(Dispatchers.IO) {
                c.deviceRepository.register(
                    DeviceRepository.RegistrationInput(s.deviceName, s.ownerName, s.simNumber, s.serverUrl, s.activationCode),
                )
            }
            when (result) {
                DeviceRepository.RegistrationResult.Success -> {
                    Scheduling.ensureBackgroundWork(getApplication())
                    // Eventuali eventi rimasti in attesa (riattivazione) partono subito.
                    withContext(Dispatchers.IO) { Scheduling.enqueueSync(getApplication(), "ATTIVAZIONE") }
                    _state.update { it.copy(busy = false, activationCode = "") }
                    onSuccess()
                }
                is DeviceRepository.RegistrationResult.Error ->
                    _state.update { it.copy(busy = false, error = result.message) }
            }
        }
    }
}
