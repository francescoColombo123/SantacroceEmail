package com.company.callregistry

import android.provider.CallLog
import com.company.callregistry.data.remote.ServerUrl
import com.company.callregistry.domain.AuthProblem
import com.company.callregistry.domain.CallStatus
import com.company.callregistry.domain.Direction
import com.company.callregistry.domain.Presentation
import com.company.callregistry.domain.SyncStatus
import com.company.callregistry.sync.SyncPolicy
import com.company.callregistry.telephony.CallLogMapper
import com.company.callregistry.telephony.EventIds
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Test

class CallLogMapperTest {
    @Test fun incomingWithDurationIsAnswered() =
        assertEquals(Direction.INCOMING to CallStatus.ANSWERED, CallLogMapper.classify(CallLog.Calls.INCOMING_TYPE, 12).pair())

    @Test fun incomingZeroDurationIsUnknown() =
        assertEquals(CallStatus.UNKNOWN, CallLogMapper.classify(CallLog.Calls.INCOMING_TYPE, 0).status)

    @Test fun missedIsMissed() =
        assertEquals(Direction.INCOMING to CallStatus.MISSED, CallLogMapper.classify(CallLog.Calls.MISSED_TYPE, 0).pair())

    @Test fun rejectedAndBlockedAreRejected() {
        assertEquals(CallStatus.REJECTED, CallLogMapper.classify(CallLog.Calls.REJECTED_TYPE, 0).status)
        assertEquals(CallStatus.REJECTED, CallLogMapper.classify(CallLog.Calls.BLOCKED_TYPE, 0).status)
    }

    @Test fun outgoingZeroDurationIsUnknown() =
        assertEquals(Direction.OUTGOING to CallStatus.UNKNOWN, CallLogMapper.classify(CallLog.Calls.OUTGOING_TYPE, 0).pair())

    @Test fun voicemailAndUnknownTypes() {
        assertEquals(CallStatus.UNKNOWN, CallLogMapper.classify(CallLog.Calls.VOICEMAIL_TYPE, 30).status)
        assertEquals(Direction.UNKNOWN to CallStatus.UNKNOWN, CallLogMapper.classify(999, 5).pair())
    }

    @Test fun numberKeptOnlyWhenPresentationAllowed() {
        assertEquals("+39021234567", CallLogMapper.normalizeNumber(" +39021234567 ", Presentation.ALLOWED))
        assertNull(CallLogMapper.normalizeNumber("", Presentation.ALLOWED))
        assertNull(CallLogMapper.normalizeNumber("+39021234567", Presentation.RESTRICTED))
    }

    @Test fun presentationMapping() {
        assertEquals(Presentation.ALLOWED, CallLogMapper.presentation(CallLog.Calls.PRESENTATION_ALLOWED))
        assertEquals(Presentation.RESTRICTED, CallLogMapper.presentation(CallLog.Calls.PRESENTATION_RESTRICTED))
        assertEquals(Presentation.UNKNOWN, CallLogMapper.presentation(-42))
    }

    private fun com.company.callregistry.domain.Classification.pair() = direction to status
}

class EventIdsTest {
    @Test fun sameRowSameId() = assertEquals(
        EventIds.forCallLogRow("inst-1", 10, 1_700_000_000_000),
        EventIds.forCallLogRow("inst-1", 10, 1_700_000_000_000),
    )

    @Test fun differentInputsDifferentIds() {
        val base = EventIds.forCallLogRow("inst-1", 10, 1_700_000_000_000)
        assertNotEquals(base, EventIds.forCallLogRow("inst-2", 10, 1_700_000_000_000))
        assertNotEquals(base, EventIds.forCallLogRow("inst-1", 11, 1_700_000_000_000))
        assertNotEquals(base, EventIds.forCallLogRow("inst-1", 10, 1_700_000_000_001))
    }

    @Test fun isValidUuid() {
        val id = EventIds.forCallLogRow("x", 1, 2)
        assertEquals(id, java.util.UUID.fromString(id).toString())
    }
}

class SyncPolicyTest {
    @Test fun perCallResults() {
        listOf("created", "updated", "unchanged", "stale").forEach { assertEquals(SyncStatus.SYNCED, SyncPolicy.statusForResult(it)) }
        listOf("invalid", "conflict").forEach { assertEquals(SyncStatus.FAILED, SyncPolicy.statusForResult(it)) }
        assertNull(SyncPolicy.statusForResult("boh"))
    }

    @Test fun httpActions() {
        assertEquals(SyncPolicy.BatchAction.ApplyResults, SyncPolicy.actionForHttp(200, null))
        assertEquals(SyncPolicy.BatchAction.Stop(AuthProblem.TOKEN_INVALID), SyncPolicy.actionForHttp(401, "unauthorized"))
        assertEquals(SyncPolicy.BatchAction.Stop(AuthProblem.DEVICE_DISABLED), SyncPolicy.actionForHttp(403, "device_disabled"))
        assertEquals(SyncPolicy.BatchAction.Stop(AuthProblem.DEVICE_MISMATCH), SyncPolicy.actionForHttp(403, "device_mismatch"))
        assertEquals(SyncPolicy.BatchAction.FailBatch, SyncPolicy.actionForHttp(400, "invalid_request"))
        listOf(408, 429, 500, 502, 503).forEach { assertEquals(SyncPolicy.BatchAction.Retry, SyncPolicy.actionForHttp(it, null)) }
    }
}

class ServerUrlTest {
    @Test fun addsSchemeAndSlash() =
        assertEquals("https://srv.azienda.it/SCemail/", ServerUrl.normalize(" srv.azienda.it/SCemail ", allowHttp = false))

    @Test fun keepsExistingSlash() =
        assertEquals("https://srv/", ServerUrl.normalize("https://srv/", allowHttp = false))

    @Test fun rejectsHttpInRelease() = assertNull(ServerUrl.normalize("http://srv/", allowHttp = false))

    @Test fun allowsHttpInDebug() = assertEquals("http://10.0.0.5:5000/", ServerUrl.normalize("http://10.0.0.5:5000", allowHttp = true))

    @Test fun rejectsGarbage() {
        assertNull(ServerUrl.normalize("", allowHttp = true))
        assertNull(ServerUrl.normalize("ftp://srv", allowHttp = true))
        assertNull(ServerUrl.normalize("https://", allowHttp = true))
    }

    @Test fun stripsQuery() = assertEquals("https://srv/app/", ServerUrl.normalize("https://srv/app?x=1", allowHttp = false))

    @Test fun hostExtraction() {
        assertEquals("192.168.200.12", ServerUrl.host("http://192.168.200.12:800/"))
        assertEquals("srv.azienda.it", ServerUrl.host("SRV.azienda.it/SCemail"))
        assertNull(ServerUrl.host(" "))
    }

    @Test fun httpAllowedOnlyForListedHostsInRelease() {
        val hosts = "192.168.200.12"
        org.junit.Assert.assertTrue(ServerUrl.httpAllowed("http://192.168.200.12:800", debug = false, allowedHosts = hosts))
        org.junit.Assert.assertFalse(ServerUrl.httpAllowed("http://192.168.200.13:800", debug = false, allowedHosts = hosts))
        org.junit.Assert.assertFalse(ServerUrl.httpAllowed("http://evil.com/?h=192.168.200.12", debug = false, allowedHosts = hosts))
        org.junit.Assert.assertTrue(ServerUrl.httpAllowed("http://qualsiasi", debug = true, allowedHosts = hosts))
    }
}
