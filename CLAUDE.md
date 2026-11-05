# CLAUDE.md — SLQ F1 Live Replay

Søren's F1 live-timing system: a 24/7 recorder + SSE server + web viewer in a
Docker container on Proxmox, plus the original Windows (WinForms) app.

**Read `F1LIVE_FRAMEWORK.md` before changing protocol, merge rules, the
`.f1rec` format, the SSE contract or the recording rules — it is the single
source of truth, and it must be updated in the same change.**

## Layout
- `src/SlqF1Live.Core` — shared logic, **net8.0** (the WinForms app must keep
  building in VS2022). No NuGet packages.
- `src/SlqF1Live.Server` — headless server, **net10.0**, HttpListener-based,
  web UI in `wwwroot/` (vanilla ES modules, no build step). No NuGet packages.
- `src/SlqF1Live` — WinForms app (net8.0-windows). Compile-check on Linux with
  `dotnet build src/SlqF1Live -p:EnableWindowsTargeting=true`.
- `tests/CoreTests`, `tests/ServerTests` — console test runners (Check/Eq).
  `tests/web/*.test.mjs` — `node --test tests/web/*.test.mjs`.

## Conventions
- UI text, log lines and code comments are Danish; identifiers English.
- C# merge rules (`StateStore.DeepMerge`) and JS (`wwwroot/js/merge.js`) must stay identical.
- The recorder's field order `{"t","type","topic","data","ts"}` is relied on by
  `RecordingFilter` and the web player — never reorder.
- Never log F1 tokens or passwords.

## Verify a change
```
dotnet run --project tests/CoreTests -c Release
dotnet run --project tests/ServerTests -c Release
node --test tests/web/*.test.mjs
```
A local mock of F1's SignalR Core endpoint can be used via `F1_LIVETIMING_URL=http://localhost:<port>`.
