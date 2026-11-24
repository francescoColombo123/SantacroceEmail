# Call Registry – app Android

App per telefoni aziendali dedicati (Android 14+). Registra le chiamate SIM (soprattutto **perse**:
chi ha chiamato quale telefono) e le invia a SCemail (`/api/mobile/*`, tabelle `MOBILE_*` su Oracle).

## Configurazione in uso

| Voce | Valore |
|---|---|
| Server | `http://192.168.200.12:800/` (IIS interno, già precompilato nell'app) |
| Raggiungibilità | Telefoni su rete mobile + **OpenVPN Connect** sempre attiva |
| HTTP in chiaro | Consentito nella release **solo** verso `192.168.200.12` (`network_security_config.xml` + `HTTP_ALLOWED_HOSTS` in `app/build.gradle.kts`): il traffico è cifrato dalla VPN |
| Chiave di firma | `callregistry.jks` + `keystore.properties` in questa cartella, **esclusi da git**. Farne un backup sicuro: senza la chiave gli aggiornamenti richiedono disinstallazione e riattivazione di ogni telefono |
| Impronta certificato (SHA-256) | `8A:BE:1E:96:5E:88:E4:1F:A3:1E:52:19:5D:14:5D:66:5D:7A:D8:4D:86:F6:DF:B5:F3:31:C5:31:7B:9C:15:56` |

Build da questo PC (variabili richieste dall'ambiente locale):

```powershell
$env:JAVA_HOME='C:\Program Files\Eclipse Adoptium\jdk-25.0.4.7-hotspot'
$env:JAVA_TOOL_OPTIONS='-Djdk.net.unixdomain.tmpdir=C:\Users\FP.Colombo\.uds'
.\gradlew.bat testDebugUnitTest assembleRelease
```

APK firmato: `app\build\outputs\apk\release\app-release.apk`. Per ogni aggiornamento incrementare
`versionCode`/`versionName` e installare sopra la versione precedente (stessa chiave → dati e attivazione conservati).

## Come funziona

| Fase | Meccanismo |
|---|---|
| Rilevamento | Broadcast `PHONE_STATE` (solo "sveglia" a fine chiamata) + trigger WorkManager sul registro chiamate + job ogni 6 h + riavvio. Nessun servizio permanente, nessun wakelock. |
| Fonte dati | `CallLog.Calls` (tipo, numero, presentazione, data, durata). Watermark sull'`_ID`: alla prima attivazione si parte dalle chiamate **successive**. |
| Identità evento | `eventUuid` = UUID v3 di `installazione + _ID + data`: rileggere il registro non crea duplicati, né in locale né sul server. |
| Coda locale | Room: `PENDING → SYNCING → SYNCED/FAILED`. Gli eventi non confermati non vengono mai cancellati. Quelli inviati da oltre 30 giorni sì. |
| Invio | WorkManager con vincolo di rete, expedited, backoff esponenziale 30 s → 5 h senza limite di tentativi. Lotti da 50. |
| Autenticazione | Codice di attivazione monouso → token del dispositivo, cifrato con chiave AES-GCM nel Keystore. Backup disattivato. |
| Errori server | 401 / 403: invii sospesi e banner "Inserisci nuovo codice". 5xx / 408 / 429 / rete: nuovo tentativo. 400: lotto segnato "rifiutato" (pulsante "Riprova rifiutate"). |

Mappatura esiti (conservativa, verificata su Samsung SM-A165F / Android 16):

| Tipo CallLog | Esito |
|---|---|
| MISSED | **MISSED** |
| REJECTED, BLOCKED | REJECTED |
| INCOMING con durata > 0 | ANSWERED |
| INCOMING con durata 0 | UNKNOWN |
| OUTGOING con durata > 0 | ANSWERED |
| OUTGOING con durata 0 | UNKNOWN |
| VOICEMAIL, ANSWERED_EXTERNALLY | UNKNOWN |

Numeri privati o sconosciuti: numero assente, solo presentazione (PRIVATO / SCONOSCIUTO).

## Struttura

```
com.company.callregistry
├─ CallRegistryApp          avvio processo, ripristino job, motivi di terminazione
├─ di/AppContainer          DI manuale
├─ domain/Models            enum di dominio
├─ telephony/               receiver PHONE_STATE e BOOT, lettura e mappatura CallLog, eventUuid
├─ data/local/              Room (eventi, log), DevicePrefs
├─ data/remote/             Retrofit/OkHttp, DTO, normalizzazione URL
├─ data/repository/         CallRepository (import + invio), DeviceRepository (attivazione, health)
├─ sync/                    SyncPolicy (regole pure), Scheduling, Worker
├─ security/TokenCipher     cifratura token con Keystore
├─ diag/                    log diagnostico, stato del dispositivo
└─ ui/                      Setup e Main (Compose, MVVM)
```

## Build

Requisiti: JDK 17 o superiore, Android SDK con platform 37.1 (`compileSdk`). Su questo PC occorrono anche le variabili d'ambiente usate durante lo sviluppo, vedi sotto.

```powershell
.\gradlew.bat testDebugUnitTest assembleDebug
```

L'APK di debug viene generato in `app\build\outputs\apk\debug\app-debug.apk`. Accetta anche server `http://` e certificati utente, quindi è adatto ai test in LAN.

### APK release firmato

1. Crea la chiave una sola volta e **conservala**: senza la stessa chiave non si possono installare aggiornamenti.
   ```powershell
   keytool -genkeypair -v -keystore callregistry.jks -alias callregistry -keyalg RSA -keysize 4096 -validity 10000
   ```
2. Crea `keystore.properties` nella root del progetto. Il file non va versionato, è già in `.gitignore`.
   ```
   storeFile=callregistry.jks
   storePassword=...
   keyAlias=callregistry
   keyPassword=...
   ```
3. Esegui `.\gradlew.bat assembleRelease`. L'APK viene generato in `app\build\outputs\apk\release\app-release.apk`.

Prima di ogni aggiornamento, incrementa `versionCode` e `versionName` in `app/build.gradle.kts`.

## Installazione su un telefono

1. Installa l'APK, consentendo l'installazione da origine sconosciuta.
2. Apri l'app e tocca **Concedi permessi** (Telefono, Registro chiamate). Se Android li mostra come "con restrizioni" (accade con gli APK installati fuori dallo store):
   - apri **Impostazioni app**;
   - tocca il menu **⋮** e scegli **Consenti impostazioni con restrizioni**;
   - torna all'app e concedi i permessi.
3. Nella pagina **Chiamate** di SCemail, tocca "Nuovo dispositivo" per generare un codice.
4. Nell'app inserisci nome, assegnatario, eventuale numero SIM, indirizzo server e codice, poi tocca **Attiva**.
5. Tocca **Escludi da ottimizzazione** (batteria). Sui Samsung imposta anche Batteria → **Senza restrizioni** e togli l'app da "App in sospensione".

Reinstallazione o telefono sostituito: dalla pagina Chiamate usa **Riassocia** sul dispositivo esistente. Il nuovo codice mantiene lo stesso ID e lo storico; il vecchio token smette di funzionare.

## Indirizzo del server ed esposizione IIS

Le API usano percorsi **relativi** (`api/mobile/...`), quindi l'app funziona anche se SCemail è una sotto-applicazione IIS. Nell'app va inserito l'indirizzo base completo, ad esempio `https://server.azienda.it/SCemail/`. Lo "/" finale viene aggiunto automaticamente.

Prova dal PC, dopo aver pubblicato SCemail e riavviato:

```powershell
Invoke-WebRequest https://server.azienda.it/SCemail/api/mobile/health -SkipHttpErrorCheck | Select-Object StatusCode, Content
```

Senza token la risposta attesa è **401** (`unauthorized`). Questo conferma che l'endpoint è raggiungibile. Con il token di un dispositivo attivo la risposta è 200.

**Telefoni solo su Wi-Fi aziendale o VPN.** Basta l'indirizzo interno di SCemail. Il certificato deve però essere considerato valido da Android: la build release si fida **solo delle CA di sistema**. Se il certificato interno è emesso da una CA aziendale ci sono due strade:
- usare un certificato pubblico;
- aggiungere la CA aziendale in `res/raw` e referenziarla in `res/xml/network_security_config.xml`.

**Telefoni su rete mobile (fuori dalla LAN).** Serve un nome pubblico con HTTPS e certificato pubblico. In quel caso va esposto **solo** `/api/mobile/`: le altre API di SCemail (`/api/mail/...`, `/api/attachments`, `/api/bozze`, `/api/email-tasks`) non hanno autenticazione. Con un sito IIS pubblico che fa da reverse proxy (URL Rewrite e ARR) la regola è:

```xml
<rewrite>
  <rules>
    <rule name="Solo API mobile" stopProcessing="true">
      <match url="^api/mobile/(.*)" />
      <action type="Rewrite" url="https://server-interno/SCemail/api/mobile/{R:1}" />
    </rule>
    <rule name="Blocca il resto" stopProcessing="true">
      <match url=".*" />
      <action type="CustomResponse" statusCode="404" statusReason="Not Found" statusDescription="Not Found" />
    </rule>
  </rules>
</rewrite>
```

Nota: `app.UseHttpsRedirection()` risponde 307 e conserva il metodo POST, ma conviene configurare comunque nell'app l'indirizzo `https://` diretto.

## Test

| Livello | Cosa | Stato |
|---|---|---|
| Unit (JVM) | Mappatura CallLog, eventUuid, SyncPolicy, normalizzazione URL | 19 test, eseguiti e superati (`testDebugUnitTest`) |
| Client HTTP contro server finto | Percorso sotto sotto-applicazione, Bearer, JSON, parsing errori (MockWebServer) | 3 test, eseguiti e superati |
| Backend contro Oracle reale | Registrazione, codice riusato, created / unchanged / updated / stale, riassociazione, disattivazione | Smoke test eseguito sullo schema SGAPP (dati di prova rimossi) |
| App ↔ SCemail end-to-end | Attivazione e invio reali | **Da fare** dopo la pubblicazione su IIS |
| Dispositivo reale, prova tecnica | Rilevamento persa / rifiutata / risposta, app chiusa | Fatto con il PoC su SM-A165F / Android 16 |
| Dispositivo reale, app completa | Vedi lista sotto | **Da fare** |

Prove sul telefono con l'app completa. Per ciascuna, verifica la pagina Chiamate e la diagnostica dell'app:

1. Chiamata persa con l'app chiusa (rimossa dalle recenti): arriva in pagina Chiamate entro pochi secondi.
2. Chiamata persa con lo schermo bloccato da più di 30 minuti.
3. Modalità aereo: chiamata persa prima di attivarla (resta "da inviare"), poi disattivazione e invio automatico.
4. Riavvio del telefono con chiamate in attesa: invio dopo l'avvio, senza aprire l'app.
5. Numero privato: in pagina compare "Privato" e nessun numero.
6. Dispositivo disattivato da SCemail: nell'app compare il banner e le chiamate restano in attesa. Dopo la riattivazione partono.
