# Jackett — Agent Notes

API support for torrent trackers. Jackett translates tracker sites into a
standard Torznab/PotatoFeed API consumed by *arr apps (Sonarr, Radarr, etc.).

Upstream: https://github.com/Jackett/Jackett

## Tech stack

- C# / ASP.NET Core, targets `net9.0` (server) and `net471` (legacy Windows service)
- Solution: `src/Jackett.sln`
- Requires **.NET SDK 9.x**. WSL only has SDK 8 — build via the Windows SDK
  through WSL interop (`dotnet.exe`) or from Rider/Visual Studio.

## Project layout (`src/`)

| Project | Purpose |
|---|---|
| `Jackett.Server` | ASP.NET Core web app + console entry point (`jackett.dll`). Build output: `bin/Release/net9.0/` |
| `Jackett.Common` | Indexer implementations (`Indexers/Definitions/*.cs` native, YAML cardigann defs in `Definitions/`) |
| `Jackett.Service` | Windows service host (net471) |
| `Jackett.Tray` | Windows tray icon |
| `Jackett.Updater` | Self-updater |
| `Jackett.Test` | Unit tests (NUnit) |
| `Jackett.IntegrationTests` | Selenium/WebDriver dashboard tests |
| `DateTimeRoutines` | Date parsing helper library |

## Build & test

```bash
# From WSL (uses Windows SDK 9.x):
cd /mnt/f/RiderProjects/Jackett
dotnet.exe build src/Jackett.Server/Jackett.Server.csproj -c Release -f net9.0

# Unit tests:
dotnet.exe test src/Jackett.Test/Jackett.Test.csproj -f net9.0

# Format (required before PRs upstream):
dotnet.exe format
```

- Build output is framework-dependent (`jackett.dll`), not self-contained —
  it needs the .NET 9 ASP.NET runtime to run.
- `Jackett.Server` must be the startup project, not `Jackett.Service`.

## Local dev deployment (Docker)

The built binaries are run in Docker side-by-side with the official image.
Compose lives outside the repo at `F:\Videos\Plex\Jackett\docker-compose.yml`:

- `jackett` — official `lscr.io/linuxserver/jackett`, host port 9117, config `data/`
- `jackett-dev` — `mcr.microsoft.com/dotnet/aspnet:9.0`, mounts
  `src/Jackett.Server/bin/Release/net9.0` read-only at `/app`,
  config at `data-dev/` (migrated from `C:\ProgramData\Jackett`),
  host port 9118, runs `dotnet jackett.dll --NoUpdates`

After rebuilding, reload the running binaries:

```bash
cd /mnt/f/Videos/Plex/Jackett && docker compose restart jackett-dev
```

Gotchas:

- If a native `jackett.exe` is running from `bin/Release/net9.0`, it locks the
  DLLs and the build fails (MSB3026/MSB3027). Kill it first — don't run the
  exe from the output dir; use the container.
- `ServerConfig.json` inside the container must have `"LocalBindAddress": "*"`
  or Docker port mapping won't reach it.
- Public exposure: an external reverse proxy forwards to the **WSL Tailscale
  node** on the container port; Docker-published ports are NOT reachable via
  the Windows Tailscale IP or Windows localhost (WSL2 NAT: published ports
  only exist inside the WSL VM).
- Dev builds report version `v0.0.0` — normal, upstream stamps versions at
  release time.

## Code style (from CONTRIBUTING.md / .editorconfig)

- 4-space indent, LF endings, final newline, no trailing whitespace
- Braces on their own line; single-line statements omit braces
- `IInterface`, `_privateField`, `s_static`, `camelCase` locals, `*Async` for async methods
- Prefer `var`, pattern matching, `?.`/`??`, expression bodies; avoid `this.`
- `using`s outside the namespace: `System`, `System.*`, then alphabetical
- Run `dotnet format` before committing

## Indexers

- New trackers: prefer YAML cardigann definitions (`src/Jackett.Common/Definitions/*.yml`)
  over C# native indexers when the site allows it.
- Validate YAML with yamllint (`yamllint.yml` in repo root).
- Enhanced logs for debugging: run with `-t -l` or enable in the UI; logs land
  in the config dir (`data-dev/` for the dev container).

## Indexers custom en este fork

- `LaMovie.cs` — wp-api JSON de lamovie.org; emite magnets + hoster links crudos (tag hoster en título).
- `Cinecalidad.cs` — HTML scraping; Link=ficha, magnet resuelto perezosamente en `Download()`.
- `PelisPanda.cs` — mismo estilo que cinecalidad.
- `MundoToon.cs` — scraping de mundotoon.com; sigue el botón "ver enlaces" (`a.imgb_pub`) al paste en todopaste.com y emite los `/go/<token>` como links crudos (hoster desde el `img` del anchor: Mega.webp/Mediafire.webp/Gdrive.webp). El countdown del `/go/` lo resuelve webdl-bridge, no el indexer. Title-matching laxo (≥60% palabras contra el query, el título TMDB es-MX o las primeras 2 palabras) porque TMDB es-MX difiere de los títulos del sitio. Sin fallback al 1er resultado: el `?s=` del sitio devuelve posts irrelevantes incluso como único hit (2026-11-05: `Bratz Rock Angelz` devolvía `Mi villano favorito 3`).
- Indexers nuevos se configuran creando `data-dev/Indexers/<id>.json` (copiar formato de lamovie.json) y reiniciando `jackett-dev`; el POST `/api/v2.0/indexers/<id>/config` pide sesión admin (302), no apikey.
