# SLQ F1 Live — Windows-app (README_APP)

Én app der gør begge dele: viser F1 live timing i eget vindue OG kører
SSE-serveren som f1.slq.dk's Live-side taler med (samme endpoints som
Node-relayet: /state, /events, /health — `live.html` virker uændret).

.NET 8, WinForms, ingen NuGet-pakker.

## Byg i VS2022

1. Åbn `SlqF1Live.sln` (Windows-app + Core + CoreTests — alt .NET 8,
   så VS2022 kan bygge det). Serveren til Proxmox ligger i
   `SlqF1Live.Server.sln` (.NET 10) og bygges med Docker — se README.md.
2. Sæt `SlqF1Live` som startprojekt → F5.

Udgivelse som enkelt exe: højreklik `SlqF1Live` → Publish → Folder →
win-x64, self-contained efter smag.

CoreTests køres med: `dotnet run --project tests/CoreTests`

## App'en

- Feed starter automatisk ved opstart (kan slås fra i settings.json);
  Start/Stop-knap øverst til højre.
- Grid: Pos, kører (i holdfarve), hold, seneste/bedste omgang, interval,
  gab til P1, omgange, status (Pit/Ude). Opdateres 2×/sek.
- Statusbar: feed-status, serverport, antal tilsluttede webklienter.
- Der kommer kun data mens en F1-session kører — udenfor sessioner står
  feedet blot "forbundet" og venter.

## settings.json (valgfri, ved siden af exe'en)

```json
{ "port": 8137, "allowOrigin": "https://f1.slq.dk", "autoStartFeed": true, "authToken": "" }
```

`authToken` (valgfri): F1 TV-bearer-token. Uden token bruges gratis,
uautentificeret adgang, som kan være begrænset for nogle sessioner.

## Adgang fra f1.slq.dk (B-delen)

Skal websiden kunne nå app'en, kræves tre engangs-ting på Windows-maskinen:

**1. URL-ACL** (ellers må ikke-admin-processer kun lytte på localhost —
app'en advarer selv i statusbaren hvis den mangler). Kør som administrator:

```
netsh http add urlacl url=http://+:8137/ user=Everyone
```

**2. Firewall:**

```
netsh advfirewall firewall add rule name="SLQ F1 Live" dir=in action=allow protocol=TCP localport=8137
```

**3. TLS.** Siden er https, så SSE-kaldet skal også være https. Nemmest
på Windows: [Caddy](https://caddyserver.com) som exe + `Caddyfile`:

```
f1live.slq.dk {
    reverse_proxy localhost:8137 {
        flush_interval -1
    }
}
```

`flush_interval -1` er vigtig (ellers bufres SSE-strømmen). Kør Caddy
som service: `caddy run` via Task Scheduler eller `sc create`. Dertil:
DNS A-record `f1live.slq.dk` → din offentlige IP, og port forward
TCP 80+443 → Windows-maskinen.

Bruger du kun app'en lokalt (A-delen), kan du springe hele dette afsnit
over — den virker uden.

## Test

- Lokalt: åbn `http://localhost:8137/health` i en browser på maskinen.
- Udefra: åbn `live.html` på f1.slq.dk, indtast `https://f1live.slq.dk`,
  tryk Forbind.

Se F1LIVE_FRAMEWORK.md for protokol, SSE-kontrakt og designbeslutninger.


## F1 TV-login (banekort + DRS-telemetri)

F1 kræver fra 2026 et F1 TV-abonnement for telemetri-topics (CarData/
Position). Uden token kører alt andet som normalt. Sådan sætter du det op:

1. Log ind på https://account.formula1.com (eller f1tv.formula1.com) i
   Chrome med din F1 TV-konto.
2. Tryk F12 (DevTools) → fanen **Application** → **Cookies** →
   `https://account.formula1.com` (eller formula1.com).
3. Find cookien **`login-session`** og kopiér HELE værdien
   (den lange tekst der starter med `%7B%22data%22...`).
4. Åbn `settings.json` ved siden af `SlqF1Live.exe` og indsæt værdien:

   ```json
   { "authToken": "%7B%22data%22%3A%7B%22subscriptionToken%22...HELE VÆRDIEN..." }
   ```

   App'en udtrækker selv tokenet — du kan også indsætte det rå JWT
   direkte, hvis du foretrækker det.
5. Genstart app'en. Loggen viser status ved start:
   "F1-login: token OK · F1 TV Pro · active · udløber ..." — eller en
   klar besked hvis tokenet mangler/er udløbet.

Tokenet udløber efter ca. 4 dage (F1 udsteder ingen refresh-token);
fornyelse = gentag trin 1-4. Ingen adgangskode gemmes — kun tokenet,
lokalt i settings.json. (Containeren på Proxmox fornyer selv tokenet med
auto-login — se README.md.)
