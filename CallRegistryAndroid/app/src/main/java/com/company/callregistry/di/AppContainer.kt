package com.company.callregistry.di

import android.content.Context
import android.os.Build
import com.company.callregistry.BuildConfig
import com.company.callregistry.data.local.AppDatabase
import com.company.callregistry.data.local.DevicePrefs
import com.company.callregistry.data.remote.MobileApi
import com.company.callregistry.data.remote.MobileApiFactory
import com.company.callregistry.data.repository.CallRepository
import com.company.callregistry.data.repository.DeviceRepository
import com.company.callregistry.diag.DiagLogger
import com.company.callregistry.security.TokenCipher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob

/** Dependency injection manuale: un'unica istanza per processo, creata pigramente. */
class AppContainer(context: Context) {
    private val appContext = context.applicationContext

    /** Scope del processo per operazioni brevi avviate dai receiver. */
    val appScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    @OptIn(ExperimentalCoroutinesApi::class)
    val serialIo = Dispatchers.IO.limitedParallelism(1)

    val database: AppDatabase by lazy { AppDatabase.create(appContext) }
    val prefs: DevicePrefs by lazy { DevicePrefs(appContext, TokenCipher()) }
    val logger: DiagLogger by lazy { DiagLogger(database.diagLog(), appScope, serialIo) }

    private val userAgent = "CallRegistry/${BuildConfig.VERSION_NAME} (Android ${Build.VERSION.RELEASE})"
    private val apiProvider: (String) -> MobileApi = { baseUrl -> MobileApiFactory.create(baseUrl, userAgent) }

    val callRepository: CallRepository by lazy {
        CallRepository(appContext, database.callEvents(), prefs, logger, apiProvider)
    }
    val deviceRepository: DeviceRepository by lazy {
        DeviceRepository(appContext, prefs, logger, apiProvider)
    }
}
