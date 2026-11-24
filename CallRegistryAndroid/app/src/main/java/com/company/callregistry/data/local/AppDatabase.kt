package com.company.callregistry.data.local

import android.content.Context
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import kotlinx.coroutines.flow.Flow

/**
 * Evento di chiamata in attesa o già confermato dal server.
 * Gli eventi non SYNCED non vengono mai eliminati.
 */
@Entity(
    tableName = "call_events",
    indices = [Index(value = ["callLogId"], unique = true), Index(value = ["syncStatus"]), Index(value = ["startedAt"])],
)
data class CallEventEntity(
    @PrimaryKey val eventUuid: String,
    val callLogId: Long,
    val deviceUuid: String,
    val phoneNumber: String?,
    val presentation: String,
    val direction: String,
    val status: String,
    val rawType: Int,
    val startedAt: Long,
    val endedAt: Long?,
    val durationSeconds: Long,
    val phoneAccountId: String?,
    val version: Int,
    val syncStatus: String,
    val retryCount: Int,
    val lastError: String?,
    val createdAt: Long,
    val updatedAt: Long,
    val syncedAt: Long?,
)

@Entity(tableName = "diag_log")
data class DiagLogEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val timestamp: Long,
    val source: String,
    val message: String,
)

@Dao
interface CallEventDao {
    /** Restituisce -1 per le righe già presenti (stesso eventUuid o callLogId). */
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(events: List<CallEventEntity>): List<Long>

    @Query("SELECT * FROM call_events WHERE syncStatus = 'PENDING' ORDER BY startedAt LIMIT :limit")
    suspend fun pendingBatch(limit: Int): List<CallEventEntity>

    @Query("UPDATE call_events SET syncStatus = 'SYNCING', updatedAt = :now WHERE eventUuid IN (:ids)")
    suspend fun markSyncing(ids: List<String>, now: Long)

    @Query("UPDATE call_events SET syncStatus = 'SYNCED', lastError = NULL, updatedAt = :now, syncedAt = :now WHERE eventUuid = :id")
    suspend fun markSynced(id: String, now: Long)

    @Query("UPDATE call_events SET syncStatus = 'FAILED', lastError = :error, updatedAt = :now WHERE eventUuid = :id")
    suspend fun markFailed(id: String, error: String?, now: Long)

    @Query("UPDATE call_events SET syncStatus = 'PENDING', retryCount = retryCount + 1, lastError = :error, updatedAt = :now WHERE eventUuid IN (:ids)")
    suspend fun revertToPending(ids: List<String>, error: String?, now: Long)

    /** Dopo un crash o un'interruzione del processo nessun evento deve restare bloccato in SYNCING. */
    @Query("UPDATE call_events SET syncStatus = 'PENDING', updatedAt = :now WHERE syncStatus = 'SYNCING'")
    suspend fun resetStuckSyncing(now: Long): Int

    @Query("UPDATE call_events SET syncStatus = 'PENDING', retryCount = 0, updatedAt = :now WHERE syncStatus = 'FAILED'")
    suspend fun retryFailed(now: Long): Int

    @Query("SELECT COUNT(*) FROM call_events WHERE syncStatus IN ('PENDING', 'SYNCING')")
    fun observeUnsyncedCount(): Flow<Int>

    @Query("SELECT COUNT(*) FROM call_events WHERE syncStatus IN ('PENDING', 'SYNCING')")
    suspend fun unsyncedCount(): Int

    @Query("SELECT COUNT(*) FROM call_events WHERE syncStatus = 'FAILED'")
    fun observeFailedCount(): Flow<Int>

    @Query("SELECT * FROM call_events ORDER BY startedAt DESC LIMIT :limit")
    fun observeRecent(limit: Int): Flow<List<CallEventEntity>>

    @Query("SELECT COUNT(*) FROM call_events WHERE status = 'MISSED' AND startedAt >= :since")
    fun observeMissedSince(since: Long): Flow<Int>

    /** Pulizia: solo eventi già confermati dal server. */
    @Query("DELETE FROM call_events WHERE syncStatus = 'SYNCED' AND syncedAt < :before")
    suspend fun deleteSyncedBefore(before: Long): Int
}

@Dao
interface DiagLogDao {
    @Insert
    suspend fun insert(entry: DiagLogEntity)

    @Query("SELECT * FROM diag_log ORDER BY id DESC LIMIT :limit")
    fun observe(limit: Int): Flow<List<DiagLogEntity>>

    @Query("SELECT * FROM diag_log ORDER BY id DESC LIMIT :limit")
    suspend fun recent(limit: Int): List<DiagLogEntity>

    @Query("DELETE FROM diag_log WHERE id <= (SELECT MAX(id) FROM diag_log) - :keep")
    suspend fun trim(keep: Int)
}

@Database(entities = [CallEventEntity::class, DiagLogEntity::class], version = 1, exportSchema = false)
abstract class AppDatabase : RoomDatabase() {
    abstract fun callEvents(): CallEventDao
    abstract fun diagLog(): DiagLogDao

    companion object {
        fun create(context: Context): AppDatabase =
            Room.databaseBuilder(context, AppDatabase::class.java, "call-registry.db").build()
    }
}
