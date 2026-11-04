# SLQ F1 Live Replay

Optager F1 live timing døgnet rundt i en container på Proxmox og afspiller
det i browseren, synkront med TV'et, også når du ser løbet forskudt.

- **Optager selv hver session** (træning, kval., sprint og løb). Serveren
  forbinder efter sæsonplanen og F1's stream-status og laver én fil pr. session.
- **Så meget data som muligt.** Alle 36 kendte topics optages råt, også
  CarData/Position, teamradio og race control. Efter sessionen hentes F1's
  officielle arkiv, og teamradio hentes som mp3.
- **Egen visning til TV, tablet og telefon** med timing-tårn, gab, dæk,
  sektorer, race control, vejr, teamradio og banekort.
- **Tre tilstande:** Live, Forsinket (x sekunder bag live) og Afspilning
  (søg, hastighed, ±1/10/60 s og *Sync til start*).
- **Auto-login til F1 TV.** Tokenet holder kun ca. 4 dage, og containeren
  fornyer det selv.
- **live.html på f1.slq.dk virker uændret** (samme `/state`, `/events`,
  `/recordings`).

> **Vigtigt om F1 i 2026:** Siden ca. 15. oktober 2026 svarer F1's arkiv
> (`livetiming.formula1.com/static/<år>/…`) **401** uden F1 TV-login, for
> alle årgange. Live-feedet virker stadig uden login, men kun med de lette
> topics. Telemetri (CarData/Position), teamradio, ChampionshipPrediction
> m.fl. kræver F1 TV Access eller højere. Det, du ikke selv optager live,
> kan derfor kun hentes bagefter med et F1 TV-abonnement.

## Installation på Proxmox

Containeren kører med Docker i en LXC, på samme måde som dk-live. Brug en
eksisterende Docker-LXC, eller opret en ny Debian-LXC med *nesting* og
*keyctl* slået til (1 vCPU og 1 GB RAM er rigeligt). Diskplads, cirka:
1–2 GB pr. weekend med telemetri og arkiv (se [Data](#data-og-diskplads)).

```bash
git clone https://github.com/slqdk/F1-Live-replay /opt/f1-live-replay
cd /opt/f1-live-replay
cp .env.example .env
nano .env            # F1_USERNAME, F1_PASSWORD, ADMIN_ALLOW, TRUSTED_PROXIES
sh update.sh         # bygger image'et og starter containeren
```

Tjek:

```bash
docker compose logs -f --tail 50                                     # log
docker compose exec f1live dotnet SlqF1Live.Server.dll --login-check  # test F1-login
```

- Visning: `http://<lxc-ip>:8137/`
- Admin: `http://<lxc-ip>:8137/admin` (kun fra `ADMIN_ALLOW`, dvs. LAN og WireGuard)

Opdatering senere: `cd /opt/f1-live-replay && sh update.sh`.

### Nginx Proxy Manager (f1live.slq.dk)

1. *Proxy Host*: `f1live.slq.dk` → `http://<lxc-ip>:8137`, SSL med Let's
   Encrypt, *Force SSL*.
2. Under *Advanced*:
   ```nginx
   proxy_read_timeout 1h;
   proxy_buffering off;
   ```
   Serveren sender også `X-Accel-Buffering: no` på SSE, så strømmen ikke
   bufres.
3. Sæt `TRUSTED_PROXIES=<NPM's IP>` i `.env`. Ellers kan admin ikke nås via
   domænet, kun direkte på LAN. Det er bevidst: Uden den indstilling kan
   serveren ikke skelne en besøgende udefra fra NPM selv.
4. DNS: `*.slq.dk` peger allerede på din IP via wildcard.

Caddy fra Windows-opsætningen skal ikke bruges længere.

## Brug

| Tilstand | Til |
|---|---|
| **Live** | Data i realtid. Live-timing ligger typisk 10–60 s *foran* TV/streaming. |
| **Forsinket** | Den igangværende session afspillet x sekunder bag live. Tryk **Start set på TV nu**, når sessionen starter på TV, så beregnes forsinkelsen automatisk. Pause virker også, og forsinkelsen vokser så tilsvarende. |
| **Afspilning** | Alle optagelser. **Sync til start** springer til sessionens start og venter. Tryk ▶, når lyset slukker (løb), eller når pit exit bliver grøn (træning/kval.). |

Taster i Forsinket/Afspilning: mellemrum = pause/afspil, ←/→ = ±1 s,
Shift+←/→ = ±10 s (i Forsinket justerer pilene forsinkelsen).

Bogmærke til TV'et: `https://f1live.slq.dk/?mode=delay`, eller
`?mode=replay&file=<fil>.f1rec&t=<sekunder>`.

### Afspilning på f1.slq.dk (live.html)

Kontrakten er uændret: Indtast `https://f1live.slq.dk` og tryk Forbind.
Optagelser med telemetri kan være flere hundrede MB. Tilføj derfor
`?profile=timing` i `js/live.js`, når filen hentes (`/recordings/<fil>?profile=timing&from=0`).
Svaret har headeren `X-Next-Offset`, som bruges som `from=` ved næste
hale-kald. Det gamle `Range: bytes=N-` virker fortsat.

## F1 TV-login

- **Auto (anbefalet):** Sæt `F1_USERNAME`/`F1_PASSWORD` i `.env`. Serveren
  logger ind ved start og fornyer tokenet, når der er under 12 t tilbage.
  Afviser F1 tokenet, logges der straks ind igen. Fejler login, venter den
  10 min, derefter 20, 40 og op til 2 t, så en forkert kode ikke hamrer løs
  på F1.
- **Manuelt:** Indsæt `login-session`-cookien (eller rå JWT) på adminsiden.
  Det virker uden genstart.
- Tokenet gemmes i `data/f1token.json` (kun læsbar for containeren).
  Adgangskoden sendes kun til `id.formula1.com` og gemmes kun i `.env`.
- Et nyt token midt i en session tages først i brug efter sessionen, fordi
  en genforbindelse giver et hul på 1–2 s. Undtagelsen er, hvis forbindelsen
  kører uden token. Så skiftes der straks, så telemetrien kommer med.

Login-flowet (OAuth/PingOne) er porteret fra [FastF1](https://github.com/theOehrly/Fast-F1)
(MIT, se `THIRD_PARTY_NOTICES.md`).

## Data og diskplads

```
data/
  f1token.json                     F1 TV-token (chmod 600)
  schedule.json                    sæsonplan-cache (Jolpica)
  logs/server-YYYY-MM-DD.log       14 dages log
  recordings/
    2026-11-08_Sao_Paulo_Grand_Prix_Race.f1rec      selve optagelsen (JSON Lines, rå)
    2026-11-08_Sao_Paulo_Grand_Prix_Race/
      meta.json                    status, linjer, topics, arkivstatus
      TeamRadio/*.mp3              teamradio (hentet live)
      archive/*.jsonStream, *.json F1's officielle arkiv (efter sessionen)
```

Skøn: uden F1 TV 15–30 MB pr. session. Med telemetri ca. 100–250 MB pr.
løb. Arkivet fylder nogenlunde det samme igen. `ARCHIVE_DOWNLOAD=false`
halverer forbruget. Adminsiden viser forbrug og fri plads.

**Sessioner du ikke optog** (fx tidligere 2026-løb) kan hentes under
*Arkiv: importér sessioner* på adminsiden (kræver F1 TV). Serveren henter
F1's arkiv og bygger en `.f1rec`, der afspilles som alle andre optagelser.

## Adgang

| Hvad | Hvem |
|---|---|
| Visning, `/events`, `/recordings`, `/media` | alle, medmindre `VIEW_ALLOW`/`VIEW_TOKEN` er sat |
| `/admin`, `/api/*` | `ADMIN_ALLOW` (LAN/WireGuard), eller `?key=<ADMIN_TOKEN>` |

Optagelser med F1 TV-data er til personlig brug. Gør dem ikke offentlige:
Sæt `VIEW_ALLOW=30.11.0.0/20,10.0.0.0/20`, eller brug en *Access List* i
NPM, hvis f1live.slq.dk skal være tilgængelig udefra.

## Endpoints

| Sti | Indhold |
|---|---|
| `GET /health` | status (bruges af Docker-healthcheck) |
| `GET /state?exclude=CarData` | fuld tilstand |
| `GET /events?exclude=CarData,Position` | SSE: `snapshot`, `feed`, `status` |
| `GET /recordings` | liste (fil, session, varighed, størrelse, `live`, header-felter) |
| `GET /recordings/<fil>` | rå fil, `Range: bytes=N-` |
| `GET /recordings/<fil>?profile=timing\|map\|tele&from=N` | let udgave + `X-Next-Offset` |
| `GET /media/<session>/TeamRadio/<fil>.mp3` | teamradio |
| `GET /api/public` | feed/optagelse/næste sessioner |
| `/admin`, `/api/status`, `/api/log`, `POST /api/token`, `/api/login`, `/api/feed`, `/api/archive/*` | admin |

## Udvikling

```bash
dotnet run --project tests/CoreTests            # 240 checks (Core)
dotnet run --project tests/ServerTests          # adgangskontrol
node --test tests/web/merge.test.mjs            # webklientens merge-regler
dotnet run --project src/SlqF1Live.Server       # lokalt: DATA_DIR=./data PORT=8137
```

- `SlqF1Live.sln`: Windows-app + Core (.NET 8, VS2022). Se `docs/README_APP.md`.
- `SlqF1Live.Server.sln`: server + tests (.NET 10).
- `F1LIVE_FRAMEWORK.md`: protokol, merge-regler, filformat og designbeslutninger.
  Læs det før ændringer.
