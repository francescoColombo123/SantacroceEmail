package com.company.callregistry.data.repository

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import com.company.callregistry.BuildConfig
import com.company.callregistry.data.local.DevicePrefs
import com.company.callregistry.data.remote.ApiJson
import com.company.callregistry.data.remote.MobileApi
import com.company.callregistry.data.remote.MobileApiFactory
import com.company.callregistry.data.remote.RegisterRequest
import com.company.callregistry.data.remote.ServerUrl
import com.company.callregistry.diag.DiagLogger
import com.company.callregistry.domain.AuthProblem
import com.company.callregistry.telephony.CallLogReader
import kotlinx.coroutines.CancellationException
import java.io.IOException
import javax.net.ssl.SSLException

/** Attivazione del dispositivo e verifica della connessione con il server. */
class DeviceRepository(
    private val context: Context,
    private val prefs: DevicePrefs,
    private val log: DiagLogger,
    private val apiProvider: (String) -> MobileApi,
) {
    data class RegistrationInput(
        val deviceName: String,
        val ownerName: String,
        val simNumber: String?,
        val serverUrl: String,
        val activationCode: String,
    )

    sealed interface RegistrationResult {
        data object Success : RegistrationResult
        data class Error(val message: String) : RegistrationResult
    }

    sealed interface HealthResult {
        data object Ok : HealthResult
        data object ServerDbDown : HealthResult
        data class AuthError(val problem: AuthProblem) : HealthResult
        data class Unreachable(val message: String) : HealthResult
        data object NotConfigured : HealthResult
    }

    suspend fun register(input: RegistrationInput): RegistrationResult {
        // Senza READ_CALL_LOG non si può fissare il punto di partenza: si importerebbe tutto lo storico.
        if (!hasCallLogPermission()) {
            return RegistrationResult.Error("Concedi prima i permessi Telefono e Registro chiamate.")
        }
        val allowHttp = ServerUrl.httpAllowed(input.serverUrl, BuildConfig.DEBUG, BuildConfig.HTTP_ALLOWED_HOSTS)
        val baseUrl = ServerUrl.normalize(input.serverUrl, allowHttp)
            ?: return RegistrationResult.Error(
                if (allowHttp) "Indirizzo del server non valido" else "Indirizzo del server non valido: deve iniziare con https://",
            )
        val request = RegisterRequest(
            activationCode = input.activationCode.trim(),
            deviceUuid = prefs.installationUuid,
            deviceName = input.deviceName.trim(),
            ownerName = input.ownerName.trim(),
            simNumber = input.simNumber?.trim()?.ifEmpty { null },
            appVersion = BuildConfig.VERSION_NAME,
            androidVersion = Build.VERSION.RELEASE,
            model = "${Build.MANUFACTURER} ${Build.MODEL}",
        )
        return try {
            val response = apiProvider(baseUrl).register(request)
            val body = response.body()
            if (response.isSuccessful && body != null) {
                prefs.completeRegistration(
                    deviceId = body.deviceId,
                    token = body.deviceToken,
                    deviceName = body.deviceName,
                    ownerName = body.ownerName,
                    simNumber = request.simNumber,
                    serverUrl = baseUrl,
                    callLogWatermark = currentCallLogMaxId(),
                )
                log.logNow("SETUP", "Dispositivo registrato (id ${body.deviceId}) su $baseUrl")
                RegistrationResult.Success
            } else {
                val err = ApiJson.parseError(runCatching { response.errorBody()?.string() }.getOrNull())
                val msg = when (err?.error) {
                    "activation_invalid" -> "Codice di attivazione non valido, scaduto o già usato."
                    "device_conflict" -> "Questo telefono risulta già associato a un altro dispositivo: chiedi all'amministratore un codice di riassociazione."
                    "db_unavailable" -> "Il server non riesce a raggiungere il database. Riprova più tardi."
                    else -> "Errore del server (HTTP ${response.code()})${err?.message?.let { ": $it" } ?: ""}"
                }
                log.logNow("SETUP", "Registrazione rifiutata: HTTP ${response.code()} ${err?.error.orEmpty()}")
                RegistrationResult.Error(msg)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: SSLException) {
            RegistrationResult.Error("Connessione sicura non riuscita (certificato del server non valido?): ${e.message}")
        } catch (e: IOException) {
            RegistrationResult.Error("Server non raggiungibile: ${e.message ?: e.javaClass.simpleName}")
        } catch (t: Throwable) {
            RegistrationResult.Error("Errore imprevisto: ${t.javaClass.simpleName} ${t.message.orEmpty()}")
        }
    }

    /** Chiamata solo con la UI aperta: nessun polling in background. */
    suspend fun checkHealth(): HealthResult {
        val baseUrl = prefs.serverUrl ?: return HealthResult.NotConfigured
        val token = prefs.deviceToken ?: return HealthResult.NotConfigured
        return try {
            val r = apiProvider(baseUrl).health(MobileApiFactory.bearer(token))
            val body = r.body()
            when {
                r.code() == 401 -> HealthResult.AuthError(AuthProblem.TOKEN_INVALID).also { prefs.authProblem = it.problem }
                r.code() == 503 -> HealthResult.ServerDbDown
                r.isSuccessful && body?.deviceActive == false ->
                    HealthResult.AuthError(AuthProblem.DEVICE_DISABLED).also { prefs.authProblem = it.problem }
                r.isSuccessful -> {
                    // Dispositivo riattivato dall'amministratore: si riprendono gli invii.
                    if (prefs.authProblem == AuthProblem.DEVICE_DISABLED) {
                        prefs.authProblem = null
                        log.logNow("HEALTH", "Dispositivo di nuovo attivo: invii ripresi")
                    }
                    HealthResult.Ok
                }
                else -> HealthResult.Unreachable("HTTP ${r.code()}")
            }
        } catch (e: CancellationException) {
            throw e
        } catch (t: Throwable) {
            HealthResult.Unreachable(t.message ?: t.javaClass.simpleName)
        }
    }

    private fun hasCallLogPermission() =
        ContextCompat.checkSelfPermission(context, Manifest.permission.READ_CALL_LOG) == PackageManager.PERMISSION_GRANTED

    /** Ultimo _ID presente ora: le chiamate precedenti all'attivazione non vengono inviate. */
    private fun currentCallLogMaxId(): Long = CallLogReader(context.contentResolver).maxId()
}
