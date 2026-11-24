package com.company.callregistry.data.remote

import kotlinx.serialization.Serializable

// Contratto delle API SCemail /api/mobile/* (vedi SCemail/Mobile/README.md).

@Serializable
data class RegisterRequest(
    val activationCode: String,
    val deviceUuid: String,
    val deviceName: String,
    val ownerName: String,
    val simNumber: String? = null,
    val appVersion: String? = null,
    val androidVersion: String? = null,
    val model: String? = null,
)

@Serializable
data class RegisterResponse(
    val deviceId: Long,
    val deviceToken: String,
    val deviceName: String,
    val ownerName: String,
    val serverTimeUtc: String? = null,
)

@Serializable
data class CallEventDto(
    val eventUuid: String,
    val version: Int,
    val phoneNumber: String? = null,
    val presentation: String,
    val direction: String,
    val status: String,
    val rawType: Int? = null,
    /** ISO-8601 con offset, es. 2026-11-23T23:08:19.000+01:00 */
    val startedAt: String,
    val endedAt: String? = null,
    val durationSeconds: Long,
    val phoneAccountId: String? = null,
)

@Serializable
data class CallBatchRequest(val deviceUuid: String, val calls: List<CallEventDto>)

@Serializable
data class CallResultDto(val eventUuid: String, val result: String, val error: String? = null)

@Serializable
data class CallBatchResponse(val results: List<CallResultDto>, val serverTimeUtc: String? = null)

@Serializable
data class HealthResponse(
    val status: String,
    val database: Boolean = false,
    val serverTimeUtc: String? = null,
    val deviceActive: Boolean? = null,
)

@Serializable
data class ErrorResponse(val error: String = "", val message: String = "")
