# F1LIVE_FRAMEWORK.md — SLQ F1 Live-system

Single source of truth for live-delen. Læses før ændringer i server, Core,
webvisning, Windows-app eller live-frontend på f1.slq.dk. (Historik-delen —
f1/index.html + js/f1.js mod OpenF1 — er uafhængig og beskrives ikke her.)

## Arkitektur (v2, nov. 2026)

```
F1 SignalR Core ─ws─▶ SlqF1Live.Server (Docker i Proxmox-LXC) ─SSE─▶ browser
livetiming.formula1.com  :8137 bag Nginx Proxy Manager (f1live.slq.dk)
                          ├─ optager hver session → data/recordings/*.f1rec
                          ├─ teamradio-mp3 + F1-arkiv efter sessionen
                          ├─ egen visning: /  (live / forsinket / afspilning)
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
| Server/ServerHost.cs | Ledningsføring, feed-styring efter plan, status |
| Server/WebApi.cs | Adgangskontrol, statiske filer, /media, /api/* |
| Server/MediaService.cs | Teamradio-kø, arkiv-jobs efter session, import/byg |
| Server/AccessControl.cs | CIDR, betroede proxies (NPM), admin-/visningsnøgler |
| Server/wwwroot/js/* | merge.js (samme regler som C#), player.js, render.js, app.js |
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
- `recordings/<stem>/TeamRadio/*.mp3`, `recordings/<stem>/archive/*`.

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
- Banekortets omrids opbygges af positioner og gemmes i localStorage pr. bane.

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

## Adgang

- Admin (/admin, /api/* undtagen /api/public): klient-IP i ADMIN_ALLOW eller
  ADMIN_TOKEN (?key / Bearer / cookie f1admin).
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

## Validering

- `tests/CoreTests` — 240 checks: merge (inkl. index-patches), .z, protokol-
  parsing, SseServer (filtre, X-Accel-Buffering, profiler, Range), optager-
  genåbning, RecordingManager-livscyklus, RecordingFilter, F1Login-parsing
  (inkl. RFC 7636 PKCE-vektor), TokenManager (auto-login, fornyelse, backoff,
  persistens), F1Schedule, ArchiveConverter, + v1-tests.
- `tests/ServerTests` — CIDR, betroede proxies, X-Forwarded-For-spoofing.
- `tests/web/merge.test.mjs` — JS-merge = C#-merge.
- Live-verificeret 4/11-2026 herfra: negotiate/ws/subscribe mod F1, snapshot
  (26 topics), login trin 1–2 mod id.formula1.com. End-to-end mod lokal mock
  af SignalR Core: optagelse åbnes, skrives, forsinket live/afspilning i browser.
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

OpenF1Client (throttle 400 ms, 429-retry), HistoryStats/DevStats er 1:1-porte
af websidens f1.js. PARITETSREGEL: ændres beregninger/labels på websiden,
skal HistoryStats.cs/DevStats.cs opdateres tilsvarende — og omvendt.

## Næste pas (forslag)

- Windows-app: brug TokenManager/F1Login (auto-login) og "Hent fra server"
  (afspil containerens optagelser).
- live.js på f1.slq.dk: hent med `?profile=timing` + X-Next-Offset-haling.
- Telemetri-panel i visningen (fart/gear/gas/bremse for valgt kører, profile=tele).
- Komprimering af ældre optagelser (zstd/gzip) når de ikke længere afspilles ofte.
