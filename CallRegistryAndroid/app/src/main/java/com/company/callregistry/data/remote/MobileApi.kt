package com.company.callregistry.data.remote

import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import retrofit2.Response
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.Header
import retrofit2.http.POST
import java.util.concurrent.TimeUnit

/** Percorsi relativi (senza "/" iniziale): funzionano anche se SCemail è in una sotto-applicazione IIS. */
interface MobileApi {
    @GET("api/mobile/health")
    suspend fun health(@Header("Authorization") authorization: String?): Response<HealthResponse>

    @POST("api/mobile/devices/register")
    suspend fun register(@Body body: RegisterRequest): Response<RegisterResponse>

    @POST("api/mobile/calls")
    suspend fun postCalls(
        @Header("Authorization") authorization: String,
        @Body body: CallBatchRequest,
    ): Response<CallBatchResponse>
}

object ApiJson {
    val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
        explicitNulls = false
    }

    fun parseError(body: String?): ErrorResponse? =
        body?.takeIf { it.isNotBlank() }?.let { runCatching { json.decodeFromString(ErrorResponse.serializer(), it) }.getOrNull() }
}

object ServerUrl {
    /** Host (senza porta) dell'URL inserito, minuscolo; null se non ricavabile. */
    fun host(input: String?): String? {
        val s = input?.trim().orEmpty()
        if (s.isEmpty()) return null
        val afterScheme = if (s.contains("://")) s.substringAfter("://") else s
        return afterScheme.substringBefore('/').substringBefore('?').substringBefore(':').lowercase().ifBlank { null }
    }

    /** HTTP consentito in debug ovunque, in release solo verso gli host esplicitamente autorizzati. */
    fun httpAllowed(input: String?, debug: Boolean, allowedHosts: String): Boolean =
        debug || host(input)?.let { h -> allowedHosts.split(',').map { it.trim().lowercase() }.contains(h) } == true

    /**
     * Normalizza l'URL inserito dall'utente in una base per Retrofit (termina con "/").
     * Restituisce null se non valido. HTTP in chiaro solo se [allowHttp] (build debug).
     */
    fun normalize(input: String?, allowHttp: Boolean): String? {
        var s = input?.trim().orEmpty()
        if (s.isEmpty()) return null
        if (!s.contains("://")) s = "https://$s"
        val lower = s.lowercase()
        val okScheme = lower.startsWith("https://") || (allowHttp && lower.startsWith("http://"))
        if (!okScheme) return null
        val host = s.substringAfter("://").substringBefore('/').substringBefore('?')
        if (host.isBlank() || host.contains(' ')) return null
        s = s.substringBefore('?').substringBefore('#')
        return if (s.endsWith("/")) s else "$s/"
    }
}

/** Crea il client HTTP: timeout espliciti, nessun log dei contenuti (contengono numeri di telefono). */
object MobileApiFactory {
    private val cache = mutableMapOf<String, MobileApi>()

    @Synchronized
    fun create(baseUrl: String, userAgent: String): MobileApi = cache.getOrPut(baseUrl) {
        val client = OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .writeTimeout(30, TimeUnit.SECONDS)
            .callTimeout(45, TimeUnit.SECONDS)
            .retryOnConnectionFailure(true)
            .addInterceptor { chain ->
                chain.proceed(chain.request().newBuilder().header("User-Agent", userAgent).build())
            }
            .build()
        Retrofit.Builder()
            .baseUrl(baseUrl)
            .client(client)
            .addConverterFactory(ApiJson.json.asConverterFactory("application/json; charset=UTF-8".toMediaType()))
            .build()
            .create(MobileApi::class.java)
    }

    fun bearer(token: String) = "Bearer $token"
}
