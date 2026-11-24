package com.company.callregistry

import com.company.callregistry.data.remote.ApiJson
import com.company.callregistry.data.remote.CallBatchRequest
import com.company.callregistry.data.remote.CallEventDto
import com.company.callregistry.data.remote.MobileApiFactory
import com.company.callregistry.data.remote.RegisterRequest
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * Verifica il client HTTP contro un server finto (MockWebServer): percorsi relativi sotto una
 * sotto-applicazione IIS, header Authorization, JSON inviato e parsing di risposte ed errori.
 * NON è un test contro il backend reale.
 */
class MobileApiClientTest {
    private lateinit var server: MockWebServer

    @Before fun setUp() { server = MockWebServer(); server.start() }
    @After fun tearDown() { server.close() }

    private fun api() = MobileApiFactory.create(server.url("/SCemail/").toString(), "test-agent")

    @Test fun postCallsUsesSubPathBearerAndContract() = runTest {
        server.enqueue(
            MockResponse.Builder().code(200)
                .body("""{"results":[{"eventUuid":"e1","result":"created"}],"serverTimeUtc":"2026-11-24T10:00:00Z","extra":1}""")
                .build(),
        )
        val dto = CallEventDto(
            eventUuid = "e1", version = 1, phoneNumber = null, presentation = "RESTRICTED",
            direction = "INCOMING", status = "MISSED", rawType = 3,
            startedAt = "2026-11-24T10:00:00+01:00", endedAt = null, durationSeconds = 0, phoneAccountId = null,
        )
        val r = api().postCalls(MobileApiFactory.bearer("tok"), CallBatchRequest("dev-1", listOf(dto)))

        assertTrue(r.isSuccessful)
        assertEquals("created", r.body()!!.results.single().result)
        val req = server.takeRequest()
        assertEquals("/SCemail/api/mobile/calls", req.url.encodedPath)
        assertEquals("Bearer tok", req.headers["Authorization"])
        assertEquals("test-agent", req.headers["User-Agent"])
        val json = Json.parseToJsonElement(req.body!!.utf8()).jsonObject
        assertEquals("dev-1", json["deviceUuid"]!!.jsonPrimitive.content)
        val call = json["calls"]!!.jsonArray.single().jsonObject
        assertEquals("MISSED", call["status"]!!.jsonPrimitive.content)
        assertFalse("i null non vengono inviati", call.containsKey("phoneNumber"))
    }

    @Test fun errorBodyIsParsed() = runTest {
        server.enqueue(MockResponse.Builder().code(403).body("""{"error":"device_disabled","message":"Dispositivo disattivato"}""").build())
        val r = api().register(RegisterRequest("AAAA-BBBB-CCCC", "u", "n", "o"))
        assertEquals(403, r.code())
        assertEquals("device_disabled", ApiJson.parseError(r.errorBody()?.string())?.error)
    }

    @Test fun healthWithoutBody() = runTest {
        server.enqueue(MockResponse.Builder().code(200).body("""{"status":"ok","database":true,"deviceActive":true}""").build())
        val r = api().health(MobileApiFactory.bearer("tok"))
        assertEquals(true, r.body()!!.deviceActive)
        assertEquals("/SCemail/api/mobile/health", server.takeRequest().url.encodedPath)
    }
}
