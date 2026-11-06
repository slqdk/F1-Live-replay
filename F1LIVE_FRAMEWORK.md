# F1LIVE_FRAMEWORK.md — SLQ F1 Live-system

Single source of truth for live-delen. Læses før ændringer i server, Core,
webvisning, Windows-app eller live-frontend på f1.slq.dk. (Historik-delen —
f1/index.html + js/f1.js mod OpenF1 — er uafhængig og beskrives ikke her.)

## Arkitektur (v2, nov. 2026)

```
F1 SignalR Core ─ws─▶ SlqF1Live.Server (Docker i Proxmox-LXC) ─SSE─▶ browser
livetiming.formula1.com  :8137 bag Nginx Proxy Manager (f1live.slq.dk)
                          ├─ optager hver session → data/recordings/*.f1rec
                          ├─ teamradio-mp3 → tekst (whisper.cpp) + F1-arkiv efter sessionen
                          ├─ egen visning: /  (live / forsinket / afspilning)
                          ├─ udvikling: /analyse (kval./løb pr. hold, OpenF1)
                          └─ admin: /admin   (kun LAN/WireGuard)
f1.slq.dk/live.html taler samme kontrakt (/state /events /recordings).
Windows-app'en (WinForms) kører stadig selvstændigt med samme Core.
```

## Filer

| Fil | Ansvar |
|---|---|
| Core/F1FeedClient.cs | SignalR Core-protokol, klient-ping, watchdog, Reconnect, token pr. forbindelse |
| Core/StateStore.cs | DeepMerge (inkl. index-patches og `_deleted`), .z-inflate, snapshots m. filter |
| Core/SseServer.cs | HttpListener: /health /state /events /recordings, kø pr. SSE-klient, route-hook |
| Core/SessionReplay.cs | SessionRecorder (append/genåbning, metadata) + SessionPlayer (WinForms) |
| Core/RecordingManager.cs | Sessionsgrænser: hvornår der åbnes/fortsættes/lukkes, navngivning, meta.json |
| Core/RecordingFilter.cs | Lette profiler af .f1rec til browseren + X-Next-Offset-haling |
| Core/F1Login.cs | F1 TV-login (OAuth2 + PKCE mod PingOne DaVinci, porteret fra FastF1) |
| Core/TokenManager.cs | Gyldigt token 24/7: fil, konfiguration, auto-login, backoff |
| Core/F1Auth.cs | Udtræk token af cookie/JSON/JWT, udløb/abonnement |
| Core/F1Schedule.cs | Jolpica-sæsonplan, StreamingStatus, sessionsvinduer |
| Core/F1Archive.cs | F1's arkiv (download, teamradio-stier) + ArchiveConverter (→ .f1rec) |
| Core/OpenF1Client.cs | api.openf1.org: 400 ms mellem kald, 28 kald/rullende minut, 429-backoff |
| Core/DevStats.cs | Development-punkter pr. kører (gab i s og m), holdsnit, standardvalg |
| Core/DevSeasonLoader.cs | Hel sæson kval./løb fra OpenF1 → DevSeason + web-JSON (server OG WinForms) |
| Server/ServerHost.cs | Ledningsføring, feed-styring efter plan, status |
| Server/WebApi.cs | Adgangskontrol, statiske filer, /media, /api/* |
| Server/MediaService.cs | Teamradio-kø, arkiv-jobs efter session, import/byg |
| Server/TranscriptService.cs | Teamradio → tekst: VAD + whisper.cpp, transcripts.json, modelhentning |
| Server/RadioText.cs | Prompt med kørernavne, VAD-parsing, lydkontekst, oprydning af whisper-output |
| Server/DevService.cs | /api/dev: cache (disk + hukommelse), deling af samtidige hent, opvarmning |
| Server/CircuitService.cs | /api/circuit: banens omrids, svingnumre og pittab (MultiViewer), cache på disk |
| Server/AccessControl.cs | CIDR, betroede proxies (NPM), admin-/visningsnøgler |
| Server/wwwroot/js/* | merge.js (samme regler som C#), player.js, render.js, app.js |
| Server/wwwroot/analyse.* + js/analyse.js, js/devchart.js | Udviklingssiden: filtre, hold-chips, SVG-graf, tabel |
| Server/wwwroot/admin.* | Adminside |
| (historisk) f1relay/* | Node-relayet — FORÆLDET (klassisk SignalR, virker ikke mod F1 i dag) |

## SignalR-protokol — SignalR *Core*

F1 nedlagde det åbne klassiske /signalr-endpoint (svarer 401). Aktuelt:

1. `POST https://livetiming.formula1.com/signalrcore/negotiate?negotiateVersion=1`
   → `{ connectionToken, connectionId, … }` + cookies (AWSALB).
2. `wss://livetiming.formula1.com/signalrcore?id=<connectionToken>`
3. Handshake: `{"protocol":"json","version":1}\x1e` — svar `{}\x1e`.
4. Subscribe: `{"type":1,"invocationId":"1","target":"Subscribe","arguments":[[topics]]}\x1e`
5. Records er JSON afsluttet med 0x1E (flere pr. frame, kan deles over frames):
   type 1 target "feed" `[topic, data, ts]` = patch · type 3 `result` = snapshot ·
   type 6 = ping · type 7 = close.
6. Auth: `Authorization: Bearer <subscriptionToken>` på negotiate + ws.
7. Klienten sender selv `{"type":6}` hvert 15. s; intet modtaget i 60 s →
   genforbind. `Reconnect()` = frisk forbindelse = frisk snapshot.
   401/403 med token → `AuthRejected`, og næste forsøg sker uden token,
   indtil et andet token leveres.
8. Test: `F1FeedClient.BaseUrl` / env `F1_LIVETIMING_URL` peger mod en mock.

## Topics (36)

Heartbeat, SessionInfo, SessionData, SessionStatus, ArchiveStatus, TrackStatus,
LapCount, DriverList, TimingData, TimingDataF1, TimingAppData, TimingStats,
WeatherData, WeatherDataSeries, RaceControlMessages, RcmSeries, TlaRcm,
CarData.z, Position.z, ExtrapolatedClock, TopThree, TeamRadio, ContentStreams,
AudioStreams, PitLaneTimeCollection, ChampionshipPrediction, TyreStintSeries,
CurrentTyres, DriverRaceInfo, SPFeed, LapSeries, PitStopSeries, PitStop,
OvertakeSeries, DriverScore, DriverTracker.

Verificeret 4/11-2026 uden token: snapshottet indeholdt 26 topics. Uden token
kom IKKE: CarData.z, Position.z, ChampionshipPrediction, DriverRaceInfo,
PitStopSeries, PitStop, RcmSeries, SPFeed, OvertakeSeries, DriverScore.
Ukendte topics i Subscribe afvises ikke. `.z` = base64 + raw-deflate → JSON;
serveren dekoder før broadcast ("CarData.z" → "CarData").

## Merge-regler (C# StateStore.DeepMerge = JS merge.js)

- Objekt + objekt → rekursiv fletning pr. nøgle (også F1's numeriske nøgler,
  fx `Lines: {"44": …}`).
- **Array + objekt med kun index-nøgler** (`{"Stints":{"2":{…}}}`,
  `{"Messages":{"12":{…}}}`) → arrayet opdateres pr. index; index = længde
  tilføjer; huller fyldes med null. F1 sender lister som array i snapshottet
  og som index-objekter i patches. Før v2 erstattede patchen arrayet, og
  race control, dæk-stints og teamradio mistede derfor historik.
- `"_deleted": [nøgler]` i en patch fjerner nøgler/indexer.
- Skalar/array i patch overskriver målet helt.
- Ugyldige .z-data springes over (ingen exception i feed-stien).

## SSE-kontrakt (server → browser)

| Event | Payload | Hvornår |
|---|---|---|
| snapshot | `{updatedAt, state}` | Ved tilslutning + efter nyt F1-snapshot |
| feed | `{topic, data, ts}` (data = dekodet patch) | Pr. F1-opdatering |
| status | `{status}` | Feed-statusskift |

- `?exclude=CarData,Position` / `?only=TimingData,DriverList` pr. klient (også på /state).
- `X-Accel-Buffering: no` (nginx/NPM), keepalive-kommentar hvert 20. s.
- Hver klient har egen kø (4000 frames); fuld kø → klienten smides af.
  Feed-tråden (og dermed optageren) blokeres aldrig af HTTP-klienter.
- Ny session (SessionInfo.Path skifter) → tilstanden nulstilles før snapshot.

## Optagelse (.f1rec) — format

JSON Lines. Linje 1 = header, derefter `{t, type:"snapshot"|"feed", …}` hvor
t = ms siden headerens `started` (vægur, monotont). **Feltrækkefølgen
`t, type, topic, data, ts` er fast**: RecordingFilter og webafspilleren
læser topic i linjens start uden fuld parse. Beskeder gemmes RÅT (inkl. .z).

Header-felter (v2, ekstra felter ignoreres af ældre læsere):
`type, version, started, session` + `meeting, sessionName, sessionType,
sessionKey, path, startDate, gmtOffset, source ("live"|"archive"), recorder`.

Genåbning: findes filen og har header → der fortsættes (append) med t regnet
fra `started`. En halv sidste linje (nedbrud) afsluttes med linjeskift først.
En ulæselig fil overskrives aldrig (nyt navn `_2`).

## RecordingManager (24/7-regler)

- Navn fra SessionInfo.Path `2026/2026-11-08_São_Paulo_Grand_Prix/2026-11-06_Practice_1/`
  → `2026-11-06_Sao_Paulo_Grand_Prix_Practice_1.f1rec` (diakritika foldes).
- Snapshot af afsluttet session (Finalised/Ends + ArchiveStatus Complete, eller
  slut + 12 t) → **ingen optagelse** (det serverer F1 mellem weekenderne).
- Samme session efter genforbindelse → samme fil + nyt snapshot-linje.
- SessionInfo skifter til ny session → luk, bed om frisk snapshot (Reconnect).
- Lukning: 10 min efter Ends · 30 min efter Finalised · 45 min uden data
  (Heartbeat tæller ikke) · 20 min efter StreamingStatus Offline · nedlukning
  (sidstnævnte uden Closed-event — genstart fortsætter filen).
- `recordings/<stem>/meta.json`: state (recording/closed/interrupted), linjer,
  bytes, topic-tællinger, telemetri ja/nej, arkivstatus.
- `recordings/<stem>/TeamRadio/*.mp3` (+ `transcripts.json`), `recordings/<stem>/archive/*`.

## Teamradio som tekst (whisper.cpp)

F1's feed har ingen udskrift — `TeamRadio.Captures[]` er kun `{Utc,
RacingNumber, Path}` til en mp3. Containeren transskriberer derfor selv,
lokalt på CPU'en (ingen sky, ingen API-nøgle):

1. MediaService henter klippet (live straks; arkiv/import bagefter) og
   melder `RadioSaved(mappe, Path, live)` → TranscriptService-køen.
   Live-klip går forrest; resten tages med lav CPU-prioritet (nice 10).
2. **VAD** (`whisper-vad-speech-segments`, Silero v5.1.2, ~40 ms): ingen tale
   → ingen whisper (whisper "hører" ellers fx "Thank you." i ren motorstøj).
3. **whisper-cli** (standard `small.en`): `-nt -np -sns -l en`, `--prompt`
   = "F1 team radio. Box, box. Copy that." + sessionens kørernavne fra
   DriverList (huskes i `data/models/driver-names.txt` til ældre klip), og
   `-ac` skåret til talens slutning + 1 s (halverer tiden; 0 over 25 s).
4. **Oprydning** (RadioText.Clean): lydbeskrivelser `[…] (…) *…*` fjernes,
   kendte støj-hallucinationer alene → tom tekst, samme sætning > 3 gange
   i træk → 3, højst 600 tegn.
5. **`recordings/<stem>/TeamRadio/transcripts.json`**, skrevet atomisk (tmp +
   omdøb): `{"version":1,"clips":{"<Path>":{"text","speech","model","ms","at"
   [,"error":true]}}}`. Nøgle = capture-`Path`. `speech:false` = kun støj.
   `error` = fejlede; prøves igen ved næste opstart (scanning efter klip uden
   tekst, nyeste optagelser først).

- Serveres via `/media/<stem>/TeamRadio/transcripts.json` med `no-cache`;
  findes filen ikke endnu → 200 `{"version":1,"clips":{}}` (ikke 404).
- Modeller hentes første gang til `data/models/` fra Hugging Face
  (`ggerganov/whisper.cpp`, `ggml-org/whisper-vad`), via `.part` + kontrol
  af ggml-magic "lmgg"; fejl → nyt forsøg om 2, 4 … 60 min.
- Målt på 2 kerner: VAD 0,04 s + whisper ~3 s pr. klip (small.en), ~700 MB
  RAM mens den kører. `large-v3-turbo` er bedre, men ~45 s pr. klip på CPU.
- `/api/public` → `transcribe: {enabled, ready, model}`; `/api/status` →
  også `state, done, failed, queued, avgMs, threads, lastError`.
- Slået fra hvis `TRANSCRIBE=false`, `TEAMRADIO_DOWNLOAD=false` eller
  programmerne mangler (fx `dotnet run` uden `WHISPER_DIR`).

**Visningen** henter `transcripts.json` hvert 4. s (live/forsinket), mens et
klip fra de sidste 3 min mangler tekst — ellers ikke. Afspilning: hvert 30. s
mens der mangler tekst. Kun klip i den (afspillede) tilstand vises, så filen
afslører intet om resten af sessionen. "Skriver det ned…" vises kun live/
forsinket. **Kørerfilter**: chips pr. kører (holdkammerater sammen), skjulte
TLA'er i localStorage `f1radioHidden`; filteret gælder også "Afspil nye
automatisk".

**Test** af hele kæden uden F1: `F1_LIVETIMING_URL` (mock af feedet) +
`F1_STATIC_URL` (mock af static/ med mp3'er). Tokenet sendes kun til
`*.formula1.com`.

## Visning af optagelser i browseren

- `/recordings/<fil>` rå + `Range: bytes=N-` → 206 (uændret, live.js).
- `/recordings/<fil>?profile=timing|map|tele&from=N` → kun komplette linjer
  fra rå-offset N; header `X-Next-Offset` = rå offset til næste kald.
  timing: uden CarData/Position. map: + Position dekodet, kun seneste
  sample, max 1/s. tele: + CarData på samme måde. Snapshot-linjer
  transformeres tilsvarende. Rå fil røres aldrig.
- Webafspilleren (player.js): gemmer linjer råt og parser ved anvendelse;
  checkpoints hvert 5. min (JSON-tekst); seek anvender kun SENESTE
  Position/CarData; forsinket live = følg filen (hale hvert 2. s) med
  position = hoved − forsinkelse; hoved = max(sidste t, serverur − started).
  Sessionsuret regnes på F1's ur (kalibreret via linjernes `ts`).
- Banekortet: banens rigtige omrids og svingnumre fra `GET /api/circuit?key=<Meeting.Circuit.Key>&year=<år>`
  (offentlig). CircuitService henter fra MultiViewer's åbne API
  (`api.multiviewer.app/api/v1/circuits/<key>/<år>`, samme koordinater som
  F1's Position-data; nyeste layout til og med året), slanker til
  `{key, year, name, rotation, x[], y[], corners[{n,x,y,angle}], pitLoss}` og gemmer i
  `data/cache/circuits/circuit_v2_<key>_<år>.json` (v2 = med pittab; ældre
  filer uden versions-tag ignoreres). Fejl huskes i 10 min.
  `pitLoss` = `{normal, sc, vsc}` i sekunder som tal (MultiViewer sender
  tekst), eller null. Mangler sc/vsc, anslås de til 65 %/70 % af normal.
  Visningen tager udgangspunkt i TV-retningen (drej rotation + 90° om midten
  og BYT x og y, som f1-dash; tegnet med y nedad). Ombytningen er en spejling
  og nødvendig: F1's koordinater har y opad, så uden den kører bilerne den
  forkerte vej rundt (test: Interlagos mod uret, Spa med uret). Derefter
  drejes banen højst ±90°, så den fylder kortfeltet mest
  muligt (2°-trin; inden for 3 % af det bedste foretrækkes TV-retningen eller
  vinkelret på den). Beregnes kun når feltets størrelse ændres; biler og
  svingnumre (540 enheder ude ad `angle`) følger samme drejning. Navne tegnes kun hvor
  der er plads (P1 først) — i pitten holder flere biler samme sted.
  Uden svar: omridset opbygges af positioner og gemmes i localStorage pr. bane.
- "Omgang x / y" vises kun når `SessionInfo.Type` er Race (LapCount i træning
  og kvalifikation er en rest fra forrige løb i F1's cache).
- **Pitprognose** (pit.js, kun når `SessionInfo.Type` er Race, dvs. løb og
  sprint): klik (Enter/mellemrum) på en kører i tårnet → en stiplet række
  viser, hvor han kommer ud, hvis han pitter nu: placering, afstand til bilen
  foran og bagved og pittabet. Klik igen, på rækken eller Esc lukker; ny
  session nulstiller.
  - Gab: `GapToLeader` i sekunder; for overhalede ("1 L") lægges
    `IntervalToPositionAhead` sammen ned gennem feltet (ukendt interval =
    1 s, markeret "ca."). Udgåede/stoppede tæller ikke.
  - Pittab efter `TrackStatus`: 1/2 normal, 4 sc, 6/7 vsc, 5 rødt flag
    (dækskift uden tab). Fra banens `pitLoss`; uden den 22/14/15 s
    ("anslået"). **Tallet indeholder allerede et normalt stop på 2,5 s**
    (målt mod 65 stop i Interlagos 2023+2025 via Jolpica: median 22,1 s
    samlet tab mod MultiViewer's 21,8 s), så stoppet lægges ikke til igen.
    Har holdet stoppet i løbet, bruges holdets median `PitStopTime`
    (PitStopSeries, 1,5–15 s) i stedet for de 2,5 s.
  - Placering: kørerens gab + pittab sammenlignet med de andres gab nu
    (antager at ingen andre pitter samtidig). Rækken sættes efter bilen han
    lander bag; tårnets striber følger pladsen, ikke DOM-rækkefølgen.
  - Banekortet (kun med MultiViewer-omrids): hvid ring om køreren og en
    stiplet ring i holdfarve, hvor han ville være: bilen foran's nærmeste
    omridspunkt minus afstanden i s × punkter pr. s (punkter / median
    `LastLapTime`, ellers 90 s), ellers ud fra bilen bagved. Omridset
    ligger i kørselsretningen.

## F1 TV-auth

- 2026: telemetri m.m. kun med token (se Topics). **Siden ~15/10-2026 svarer
  hele static/<år>/… 401 uden token** (CloudFront-funktion); med
  `Authorization: Bearer <subscriptionToken>` → 200. `static/StreamingStatus.json`
  og `static/SessionInfo.json` (seneste session) er stadig åbne.
- Token = claim `subscriptionToken` i access-tokenet fra F1's IdP (eller i
  `login-session`-cookien). Udløber efter **ca. 4 dage**; ingen refresh-token.
- F1Login: `account.formula1.com/scripts/main.min.js` → `davinci:{clientId,
  acrValues}` → `GET id.formula1.com/as/authorize?response_mode=pi.flow…`
  (X-Requested-With: ping-sdk) → POST formular (SIGNIN, username/password) →
  `{status:"COMPLETED", authorizeResponse:{code,state}}` → `POST /as/token`
  (authorization_code + PKCE) → access_token → subscriptionToken.
  Adgangskoden sendes kun til `https://id.formula1.com/`.
- TokenManager: forny ved < 12 t tilbage, ved afvisning straks; backoff
  10→20→40→…→120 min ved fejl; token i data/f1token.json (600).
- Nyt token midt i en live-session tages i brug efter sessionen (undgår hul);
  uden token i forbindelsen skiftes straks.

## Feed-styring og plan

- FEED_MODE=auto: forbind når en planlagt session er i vinduet
  [start − 75 min, slut + 90 min (+90 for løb)], når StreamingStatus =
  Available, eller mens der optages. Afbryd 10 min efter. always/off findes.
- Plan: Jolpica `api.jolpi.ca/ergast/f1/<år>.json` (+ næste år fra nov.),
  hver 6. t, cache i data/schedule.json. Varigheder: FP/Q 60, SQ/Sprint 45,
  løb 120 min.

## Arkiv og import

- Efter lukning: arkivjob (første forsøg +20 min, derefter hver 30. min,
  opgives efter 36 t). Henter alle feeds fra sessionens Index.json
  (`Feeds{…{KeyFramePath, StreamPath}}`) til `<stem>/archive/` + teamradio
  fra TeamRadio.json. Genoptages efter genstart (meta.json uden arkiv, < 3 døgn).
- jsonStream: UTF-8 m. BOM, CRLF, linjer `HH:MM:SS.mmm<json>` (eller
  `D.HH:MM:SS.mmm…`), tid relativ til arkivets start. t0 = første Heartbeat.Utc − offset.
- ArchiveConverter: k-vejs fletning af alle streams → .f1rec (source "archive",
  tomt snapshot, `ts` = t0 + offset). `<stem>_arkiv.f1rec` hvis en live-optagelse findes.
- Admin: sæsonindeks (`static/<år>/Index.json`) → Importér.

## Udvikling over sæsonen (/analyse, OpenF1)

Sammenligner holdenes tider i kvalifikation og løb GP for GP — som
Development-fanen på den gamle F1 Analyse-side og i Windows-app'en.
Datakilden er OpenF1 (gratis, 2023→), ikke F1's arkiv (lukket bag F1 TV
siden okt. 2026) — så siden virker også for sessioner, containeren ikke optog.

**DevSeasonLoader (Core, delt med WinForms)**
- `/meetings?year=` + `/sessions?year=`; pr. møde sessionerne med
  `session_type` qualifying/race; hovedsessionen (`session_name`
  "Qualifying"/"Race") foretrækkes frem for sprint. Testdage (Practice) og
  fremtidige sessioner springes over.
- Kørerlister: ét kald for hele sæsonen (`/drivers?session_key>=første&session_key<=sidste`),
  nyeste række pr. nummer vinder (holdskifte, reserver får deres hold).
  Tomt/fejl → kun sidste sessions liste (den oprindelige websideadfærd).
- `/session_result` pr. GP → DevStats.DevPointsForSession. Tomt svar/404 =
  **skipped** ("Uden resultatdata", fx aflyste Sakhir/Jeddah 2026). Fejl
  (rate limit, 5xx) = **failed** — aldrig forvekslet med "ingen data".
- Diskcache: `openf1_result_<session_key>.json`, kun ikke-tomme resultater
  for sessioner der sluttede for mere end 2 timer siden.

**OpenF1Client** — min. 400 ms mellem kald og højst 28 kald pr. rullende
minut (OpenF1's gratis grænse er pr. sekund OG pr. minut; en sæson er ~25
kald). 429 → Retry-After (højst 60 s), ellers 2 s·2ⁿ (loft 30 s), højst 6
forsøg, ingen ventetid efter sidste forsøg. 404 → tomt array. Alle grænser
kan sættes (tests).

**DevService (server)** — diskcache i `data/cache/openf1/`; hele svaret i
hukommelsen 20 min for indeværende sæson, 24 t for ældre. Svar med failed
GP'er holdes kun 2 min, så næste besøg prøver igen. Samtidige forespørgsler
på samme år/tilstand deler ét hent. En kold sæson tager ~1 min, så API'et
venter højst 8 s og svarer ellers **202** med fremdrift; siden spørger igen
hvert 1,5 s (ingen forespørgsel rammer en proxy-timeout). En fejlet hentning
returneres som 502 i 20 s, derefter prøves igen. Indeværende sæson varmes op
ved start og 45 min efter hver afsluttet optagelse (OpenF1 er typisk
opdateret da).

**Kontrakt** — `GET /api/dev?year=2026&mode=quali|race` (år 2023–nu, ellers
400; OpenF1-fejl → 502 `{ok:false,error}`; hentes stadig → 202
`{"loading":true,"progress":"Henter resultater… (7/24)"}`; klar → 200):

```json
{ "year": 2026, "mode": "quali",
  "gps": [{ "label": "Melbourne", "name": "Australian Grand Prix", "date": "2026-03-07" }],
  "skipped": ["Sakhir"], "failed": [],
  "drivers": [{ "dn": "44", "tla": "HAM", "name": "Lewis HAMILTON", "team": "Ferrari",
                "colour": "#ED1131", "points": [{ "s": 0.3, "m": 21.4, "note": null }, null] }],
  "teams": [{ "name": "Mercedes", "colour": "#00D7B6", "avg": 0.26, "count": 38 }],
  "defaultHidden": ["…"], "generatedUtc": "…" }
```

`points[i]` hører til `gps[i]`; `null` = kørte ikke; `s` null + `note`
("DNF", "+1 omg.", "–") = hul i grafen. `teams` er sorteret bedst først
(snit af alle kørernes gyldige punkter); `defaultHidden` = alle undtagen
bedste og dårligste (som den gamle side). I tilstanden Hold viser siden i
stedet snittet af bedste bil pr. GP (regnes i browseren).

**Siden (wwwroot/analyse.html, js/analyse.js, js/devchart.js)**
- Filtre i én række: Sæson · Kvalifikation|Løb · Kørere|Hold (bedste bil) ·
  Zoom · Graf|Tabel. Hold-chips (klik vælg/fravælg; Vis alle · Bedste og
  dårligste · Ryd). Tilstand i URL'en (`?year&mode&per&view&zoom&teams`),
  valget pr. sæson i localStorage `f1dev:sel:<år>`, zoom i `f1dev:zoom`.
- Kørere: én linje pr. kører i holdfarve; holdets bedste kører over sæsonen
  fuld linje, makker stiplet. Hold: bedste bil pr. GP.
- Y = gab til pole/vinderen, 0 øverst (højere er bedre). Zoom klipper ved
  max(p90·1,25, min+0,5); punkter udenfor tegnes som en pil (med stilk) på
  kanten, i retning af værdien. Aksedecimaler følger trinnet (0,25 → "+0.25 s").
- **Graf 2: ændring siden første Grand Prix** (`deltaSeries` i devchart.js):
  pr. serie gab_N − gab ved seriens første GP med tid (normalt sæsonens
  første; ellers kørerens/holdets første). Negativ = tættere på end dengang
  og tegnes øverst; 0-linjen fremhævet; aksen med fortegn ("−0.25 s").
  Zoom: symmetrisk ±max(p90(|Δ|)·1,25, 0,25). Udgået/noter beholdes som
  huller. Tooltip: ændring + dagens gab; basispunktet vises som "Basis".
  Tabel: Δ pr. GP + "Seneste". Bemærk: den der havde pole/vandt i første GP,
  kan ikke komme over 0 (gab kan ikke blive negativt).
- Skift mellem kvalifikation og løb beholder holdvalget (samme sæson).
- Mærker: linje 2 px, punkt r 4,5 med 2 px ring i fladens farve. Endelabels
  med føringslinje når ≤ 8 serier. Krydsmarkør + tooltip med alle synlige
  serier (s og meter); ←/→ flytter markøren; tabelvisning med alle værdier.
- **Punktformer (sekundær kodning):** holdfarverne kolliderer (Ferrari/Audi
  ΔE 1,5; Haas/Cadillac 4,8). Afstand = mindste OKLab-ΔE under normalt syn,
  protan og deutan (Machado; CVD vægtet ×1,6); "farvelignende" < 25.
  Formerne er ● ■ ◆ ▲ ▼ + × (pile er forbeholdt punkter uden for zoom). 7
  hold i 2026 er parvis farvelignende — med 7 former får de alle hver sin.
  Generelt vælges den største tærskel, hvor alle par under den kan få hver
  sin form (graffarvning med backtracking); må nogen dele, er det de mest
  forskellige.
  Formen følger holdet for hele sæsonen — aldrig filteret eller rækkefølgen.
- Tekst bærer aldrig seriefarven; identitet = farvestreg + form ved siden af.

## Adgang

- Admin (/admin, /api/* undtagen /api/public, /api/dev og /api/circuit): klient-IP i
  ADMIN_ALLOW eller ADMIN_TOKEN (?key / Bearer / cookie f1admin).
- /analyse og /api/dev følger visningens regler (offentlige data fra OpenF1).
- Forespørgsler med X-Forwarded-For/X-Real-IP fra ikke-betroet afsender regnes
  som udefra (aldrig admin via IP). Fra TRUSTED_PROXIES bruges X-Real-IP,
  ellers højre-mest ikke-betroede hop i X-Forwarded-For.
- Visning åben, medmindre VIEW_ALLOW/VIEW_TOKEN (cookie f1key sættes ved ?key).

## Designbeslutninger

- **SSE frem for WebSocket** til browseren: envejsdata, auto-reconnect, let
  gennem proxies.
- **Rå optagelse**: alt gemmes som modtaget; nye visninger kan bygges på
  gamle optagelser uden at mangle data. Filtrering sker ved servering.
- **Ingen NuGet-pakker**: HttpListener, System.Text.Json og ClientWebSocket
  er indbygget; Kestrel ville kræve ASP.NET-runtime og en anden SSE-kode end
  Windows-app'en.
- **Core = net8.0** (VS2022/WinForms), **server = net10.0** (LTS; .NET 8's
  support slutter 10/11-2026). Docker-runtime: `dotnet/runtime:10.0`,
  ikke-root (UID 1654).
- **Browser-afspilning** (valgt af Søren): hver browser afspiller selv; ingen
  serverside-afspillertilstand; ubegrænset antal klienter ved vilkårlige offsets.
- **Teamradio-tekst med whisper.cpp som separate programmer** (ikke Whisper.net/
  NuGet): bygges i Docker-image'et, kaldes pr. klip. Tekst i en sidefil pr.
  optagelse frem for i .f1rec — optagelsen forbliver rå F1-data, og ældre/
  importerede optagelser kan få tekst bagefter.

## Validering

- `tests/CoreTests` — 277 checks: merge (inkl. index-patches), .z, protokol-
  parsing, SseServer (filtre, X-Accel-Buffering, profiler, Range), optager-
  genåbning, RecordingManager-livscyklus, RecordingFilter, F1Login-parsing
  (inkl. RFC 7636 PKCE-vektor), TokenManager (auto-login, fornyelse, backoff,
  persistens), F1Schedule, ArchiveConverter, OpenF1Client (429/Retry-After/
  backoff/vindue/afstand) og DevSeasonLoader mod falsk OpenF1 (sessionsvalg,
  sprint fravalgt, skipped vs. failed, kørerlister, diskcache, JSON-form),
  + v1-tests.
- `tests/ServerTests` — CIDR, betroede proxies, X-Forwarded-For-spoofing;
  RadioText (prompt, VAD, lydkontekst, oprydning), transcripts.json og
  TranscriptService mod falske whisper-programmer (shell-scripts).
- `tests/web/*.test.mjs` (`node --test tests/web/*.test.mjs`) — JS-merge =
  C#-merge; udviklingsgrafens formtildeling, aksedecimaler, formatering og
  ændring siden første Grand Prix.
- Live-verificeret 4/11-2026 herfra: negotiate/ws/subscribe mod F1, snapshot
  (26 topics), login trin 1–2 mod id.formula1.com. End-to-end mod lokal mock
  af SignalR Core: optagelse åbnes, skrives, forsinket live/afspilning i browser.
- Teamradio-tekst 5/11-2026: mock-feed + mock-static med syntetiske, støjede
  radioklip (TTS): hentet → VAD → small.en → tekst i visningen live og i
  afspilning (ingen klip efter afspilningspositionen). Rigtig F1-radio er
  endnu ikke prøvet (static kræver token).
- Ikke verificeret endnu (kræver live-session/legitimation): fuldt login med
  kode, telemetri-topics med token, arkiv-download med token. Første rigtige
  test: Brasilien FP1 fre. 6/11-2026 16:30.

## Windows-app (SlqF1Live, C#/.NET 8 WinForms)

Alternativ/supplement til serveren: samme Core, samme kontrakt + eget UI.
Feed-livscyklus styres af UI-knappen. HttpListener kræver `netsh http add
urlacl` for at lytte udadtil (falder tilbage til localhost og advarer).
Se docs/README_APP.md.

| Fil | Ansvar |
|---|---|
| src/SlqF1Live.Core/TimingView.cs | State-JSON → sorterede grid-rækker (headless-testbar) |
| src/SlqF1Live.Core/LiveHistory.cs | LapTimeParser/GapParser + LapHistory + GapHistory |
| src/SlqF1Live/ChartPanel.cs | Håndrullet linjegraf |
| src/SlqF1Live/MainForm.cs | WinForms-UI; faner Timing/Omgangstider/Intervaller/Historik/Development |

### App-grafernes datamodel

- Omgangstider: LapHistory.UpdateFrom(TimingData) pr. UI-tick (idempotent).
- Intervaller: GapHistory.SampleFrom hvert sekund; Y inverteret.
- Stiplet linje: kører nr. 2 pr. hold.

### Optagelse & afspilning i app'en

SessionRecorder (Core) optager til recordings/*.f1rec ved første snapshot;
SessionPlayer er clock-agnostisk (Tick/AdvanceTo/Seek; Seek = Reset + hurtig
gen-emission). Live og afspilning er gensidigt udelukkende i MainForm.
Sync mod TV: Play/Pause + slider + ±10/±60 s + hastighed 0,5–10×.

### Historik-fane (OpenF1) og Development-fane

OpenF1Client (se Udvikling), HistoryStats/DevStats er 1:1-porte af websidens
f1.js. Development-fanen henter via DevSeasonLoader — samme kode som
containerens /analyse — med diskcache i `<app>/cache`. PARITETSREGEL: ændres
beregninger/labels på websiden, skal HistoryStats.cs/DevStats.cs opdateres
tilsvarende — og omvendt.

## Næste pas (forslag)

- Windows-app: brug TokenManager/F1Login (auto-login) og "Hent fra server"
  (afspil containerens optagelser).
- live.js på f1.slq.dk: hent med `?profile=timing` + X-Next-Offset-haling.
- Telemetri-panel i visningen (fart/gear/gas/bremse for valgt kører, profile=tele).
- Komprimering af ældre optagelser (zstd/gzip) når de ikke længere afspilles ofte.
- Teamradio-tekst på GPU'en (SLQ Hub) via et OpenAI-kompatibelt
  `/v1/audio/transcriptions`-endpoint — giver plads til `large-v3-turbo`.
