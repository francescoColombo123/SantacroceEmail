# Modulo "Registro chiamate mobile" (SCemail)

Riceve le chiamate rilevate dall'app Android *Call Registry* installata sui telefoni
aziendali, le salva su Oracle (schema `SGAPP`) e le rende consultabili agli admin
nella pagina **/chiamate** (pulsante *Chiamate* nella home, visibile solo agli admin).

| File | Contenuto |
|---|---|
| `Database/Mobile/001_mobile_chiamate.sql` | DDL delle tabelle `MOBILE_DISPOSITIVI`, `MOBILE_ATTIVAZIONI`, `MOBILE_CHIAMATE` (solo nuove tabelle) |
| `Mobile/MobileController.cs` | API `/api/mobile/*` |
| `Mobile/MobileDeviceAuth.cs` | Autenticazione del telefono tramite token |
| `Mobile/MobileRepository.cs` | Accesso Oracle (Dapper, query parametrizzate, transazioni) |
| `Mobile/MobileValidation.cs`, `MobileTokens.cs`, `MobileTime.cs` | Validazione, token/hash, fuso orario |
| `Components/Pages/Chiamate.razor` | Pagina di consultazione e gestione dispositivi |

## Installazione

1. Eseguire `Database/Mobile/001_mobile_chiamate.sql` sullo schema `SGAPP` (Oracle 12c+).
2. Pubblicare SCemail come di consueto: non servono nuove impostazioni.
3. Rendere raggiungibile dai telefoni **solo** il percorso `/api/mobile/` in **HTTPS** con
   certificato valido (vedi *Sicurezza*).

## Flusso

1. L'admin apre **/chiamate** → *Nuovo dispositivo* → ottiene un codice `XXXX-XXXX-XXXX`
   (monouso, valido 3 giorni; nel DB se ne salva solo l'hash).
2. Al primo avvio l'app invia codice + dati del telefono a `devices/register` e riceve un
   **token** personale (256 bit; nel DB solo l'hash SHA-256), conservato cifrato sul telefono.
3. Ogni chiamata viene inviata a `calls` con `Authorization: Bearer <token>`.
4. App reinstallata → *Riassocia* sul dispositivo: il nuovo codice collega la nuova
   installazione **allo stesso dispositivo**, lo storico resta e il vecchio token smette di valere.
5. *Disattiva* blocca subito il telefono (403 `device_disabled`).

## API

Tutte JSON (camelCase). Date in ISO-8601 con fuso (`2026-11-23T23:08:19.000+01:00`); il DB
salva in UTC. Errori sempre nel formato `{ "error": "<codice>", "message": "<testo>" }`.

### `GET /api/mobile/health`
Senza token: stato del server e del DB. Con `Authorization: Bearer <token>`: anche `deviceActive`.

```json
200 { "status": "ok", "database": true, "serverTimeUtc": "2026-11-23T22:12:30Z", "deviceActive": true }
```
| Codice | Significato |
|---|---|
| 200 | Server e DB raggiungibili |
| 401 | Token presente ma non valido |
| 503 | DB non raggiungibile (`status: db_unavailable`) |

### `POST /api/mobile/devices/register`
```json
{
  "activationCode": "ABCD-EFGH-JKMN",
  "deviceUuid": "6f1c3e2a-9b7d-4c1e-8a55-0d3f2b9e7c41",
  "deviceName": "Telefono Ufficio Tecnico",
  "ownerName": "Mario Rossi",
  "simNumber": "+39333...",
  "appVersion": "1.0.0",
  "androidVersion": "16",
  "model": "samsung SM-A165F"
}
```
```json
201 { "deviceId": 7, "deviceToken": "q2F…", "deviceName": "Telefono Ufficio Tecnico",
      "ownerName": "Mario Rossi", "serverTimeUtc": "2026-11-23T22:12:30Z" }
```
| Codice | `error` | Significato / azione dell'app |
|---|---|---|
| 201 | — | Registrato: salvare il token |
| 400 | `invalid_request` | Campi mancanti o UUID non valido |
| 403 | `activation_invalid` | Codice errato, scaduto, revocato o già usato → chiederne uno nuovo |
| 409 | `device_conflict` | UUID già associato a un altro dispositivo |
| 503 | `db_unavailable` | Riprovare più tardi |

### `POST /api/mobile/calls`  (richiede `Authorization: Bearer <token>`)
Da 1 a 100 chiamate per richiesta.
```json
{
  "deviceUuid": "6f1c3e2a-9b7d-4c1e-8a55-0d3f2b9e7c41",
  "calls": [{
    "eventUuid": "0b6d1f0e-3c2a-5b8e-9f4d-7a1c2e3b4d5f",
    "version": 1,
    "phoneNumber": "+393331234567",
    "presentation": "ALLOWED",
    "direction": "INCOMING",
    "status": "MISSED",
    "rawType": 3,
    "startedAt": "2026-11-23T23:08:19.000+01:00",
    "endedAt": "2026-11-23T23:08:19.000+01:00",
    "durationSeconds": 0,
    "phoneAccountId": "1"
  }]
}
```
- `presentation`: `ALLOWED` | `RESTRICTED` (privato) | `UNKNOWN` | `PAYPHONE`. Il numero si salva solo con `ALLOWED`.
- `direction`: `INCOMING` | `OUTGOING` | `UNKNOWN`.
- `status`: `ANSWERED` | `MISSED` | `REJECTED` | `UNKNOWN`.

```json
200 { "results": [ { "eventUuid": "0b6d1f0e-…", "result": "created", "error": null } ],
      "serverTimeUtc": "2026-11-23T22:12:30Z" }
```
Esito per singola chiamata:

| `result` | Significato | L'app… |
|---|---|---|
| `created` | Inserita | segna SYNCED |
| `updated` | Versione più recente salvata | segna SYNCED |
| `unchanged` | Già presente con la stessa versione (retry/duplicato) | segna SYNCED |
| `stale` | Il server ha già una versione più recente | segna SYNCED |
| `invalid` | Dati non validi (`error` spiega perché) | segna FAILED, non ritenta |
| `conflict` | `eventUuid` appartiene a un altro dispositivo | segna FAILED, non ritenta |

| Codice HTTP | `error` | L'app… |
|---|---|---|
| 200 | — | applica gli esiti per chiamata |
| 400 | `invalid_request` / `batch_too_large` | non ritenta il lotto così com'è |
| 401 | `unauthorized` | token mancante/errato → chiede la riattivazione |
| 403 | `device_disabled` / `device_mismatch` | dispositivo disattivato o UUID diverso → stop invii |
| 503 / timeout / rete assente | `db_unavailable` | conserva tutto e ritenta con backoff |

### Duplicati, timeout e ordine degli aggiornamenti
- **Idempotenza:** `EVENT_UUID` è `UNIQUE`. Se la risposta si perde per un timeout, il telefono
  rimanda lo stesso evento e il server risponde `unchanged`: nessun duplicato.
- **Versione:** si aggiorna solo se `version` in arrivo > `VERSIONE` salvata; un retry con dati
  vecchi non sovrascrive quelli nuovi (`stale`).
- **Concorrenza:** ogni chiamata è salvata in una transazione con `SELECT … FOR UPDATE`;
  l'inserimento concorrente dello stesso UUID (ORA-00001) viene riletto e ricalcolato.
- **Errori a metà lotto:** le chiamate già salvate restano; al nuovo invio risultano `unchanged`.
- Timeout comandi Oracle: 15 s. Il telefono dovrebbe usare un timeout HTTP di circa 30 s.

## Sicurezza

- Nessuna credenziale Oracle o segreto nell'APK: il telefono conosce solo il proprio token.
- Il dispositivo è identificato **dal token**; `deviceUuid` nel JSON deve corrispondere (403 altrimenti).
- Sul DB solo hash SHA-256 di token e codici. I numeri di telefono non vengono scritti nei log.
- ⚠️ Le altre API di SCemail (es. `/api/mail/fetch-now`, `/api/attachments/...`) **non hanno
  autenticazione**. Se SCemail viene esposto su Internet per i telefoni, pubblicare tramite
  reverse proxy/IIS **solo** il percorso `/api/mobile/`.
