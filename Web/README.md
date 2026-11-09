# MSFS Telemetry Web (Phase One)

A small self-hosted ASP.NET Core (.NET 10 LTS) app: a Minimal API, a static frontend in
`wwwroot`, and a local SQLite database (`App_Data/airports.db`) in WAL mode. It runs with
`dotnet run` (Kestrel) or behind IIS (in-process).

It listens on `http://localhost:5000` by default.

## API

| Method & path | Purpose | Responses |
|---|---|---|
| `GET /api/status` | Backend and database health | 200 ok, 503 database unavailable |
| `GET /api/auth/config` | Whether Google sign-in is on, and the public client ID | 200 |
| `POST /api/auth/google` | Body `{ "credential": "<Google ID token>" }`. Verifies the token, creates the profile on first sign-in, sets the session cookie. Returns `{ isNewUser, user }` | 200, 400, 401 bad token, 403 unverified email or deactivated, 503 not configured or Google unreachable |
| `POST /api/auth/logout` | Clears the session cookie | 204 |
| `GET /api/auth/me` | The signed-in user's profile | 200, 401 |
| `PUT /api/profile` | Body `{ displayName, bio, homeAirportIcao, timeZone }`. Updates the editable fields | 200, 400 validation errors, 401 |
| `GET /api/profile/api-key` | The user's API key `{ key, prefix, canShow, createdUtc, lastUsedUtc }`, created on first request. Session cookie only | 200, 401 |
| `POST /api/profile/api-key` | Replaces the key; the old one stops working immediately. Same response. Session cookie only | 200, 401 |
| `POST /api/flights` | **API key** (`X-Api-Key: fsf_…` or `Authorization: Bearer fsf_…`). Saves a flight summary (see below). Re-posting the same `flightId` updates it | 201 created, 200 updated, 400 validation errors, 401 missing/invalid key, 413 body over 16 KB |
| `GET /api/flights?limit=20&offset=0` | The signed-in user's flights, newest first, plus totals `{ flights, distanceNm, durationSeconds }`. `limit` 1–100. Session cookie only | 200, 401 |

## Sign in with Google

The browser shows Google's own button (Google Identity Services). When the user picks an account,
Google gives the page a signed **ID token**, and the page POSTs it to `/api/auth/google`. The
server checks the token's signature, audience (our client ID), issuer and expiry against Google's
public keys. It then finds or creates the `users` row by Google ID (the token's `sub` claim) and
issues its own session cookie: `msfs.session`, HttpOnly, SameSite=Strict, sliding 30 days. The
Google token isn't stored anywhere. This flow needs no client secret and no redirect URI.

### One-time Google Cloud setup

1. [Google Cloud Console](https://console.cloud.google.com/) → create or choose a project →
   **APIs & Services → OAuth consent screen**. Pick *External*, fill in the app name and support
   email, and add yourself as a test user while the app is in *Testing*.
2. **APIs & Services → Credentials → Create credentials → OAuth client ID → Web application.**
   Under **Authorized JavaScript origins**, add every origin the site is opened from, e.g.
   `http://localhost:5000`, `http://localhost` (Google wants both for local dev) and
   `http://yourserver:8080`. Leave *Authorized redirect URIs* empty.
3. Copy the client ID (`….apps.googleusercontent.com`) into `Authentication:Google:ClientId` in
   `appsettings.json`, or into `appsettings.Production.json` on the server. It isn't a secret.

Optional: `Authentication:Google:HostedDomain` limits sign-in to one Google Workspace domain.
`RequireVerifiedEmail` (default `true`) refuses accounts whose email Google hasn't verified.

### The `users` table

Created by schema migration 1 (`Data/Schema.cs`). Migrations are tracked with `PRAGMA user_version`;
to change the schema, append a new migration and never edit a shipped one.

| Column | Type | Source | Notes |
|---|---|---|---|
| `User_ID` | INTEGER PK AUTOINCREMENT | — | Internal ID; stored in the session cookie |
| `Google_ID` | TEXT UNIQUE NOT NULL | Google | `sub` claim; how a returning user is recognized |
| `Email`, `Email_Verified` | TEXT, 0/1 | Google | Refreshed at every sign-in |
| `Given_Name`, `Family_Name`, `Avatar_URL` | TEXT | Google | Refreshed at every sign-in |
| `Display_Name` | TEXT NOT NULL | User | Seeded from Google's name on creation, then only the user changes it |
| `Bio`, `Home_Airport_ICAO`, `Time_Zone` | TEXT | User | Time zone is an IANA name, e.g. `America/Chicago` |
| `Is_Active` | 0/1, default 1 | Admin | Set to 0 to block a user. Takes effect on their next request |
| `Login_Count`, `Last_Login_UTC` | INTEGER, TEXT | System | |
| `Created_UTC`, `Updated_UTC` | TEXT | System | ISO 8601 UTC |

To deactivate someone: `UPDATE users SET Is_Active = 0 WHERE Email = '…';`

### API keys (the `api_keys` table)

Each user has one API key for the companion app, shown on the profile page with **Show**, **Copy**
and **Replace key**. Keys look like `fsf_` followed by 43 random base64url characters (256 bits).
Created by schema migration 2.

| Column | Notes |
|---|---|
| `User_ID` | PK, references `users`; one key per user |
| `Key_Hash` | SHA-256 of the key (hex), UNIQUE. Requests are checked against this |
| `Key_Protected` | The key encrypted with ASP.NET Core Data Protection, so the profile page can show it again |
| `Key_Prefix` | First 12 characters, safe to show and log |
| `Created_UTC`, `Last_Used_UTC` | Last use is updated at most once a minute |

- A key is created the first time the profile page asks for it; **Replace key** swaps it immediately.
- Keys can only be viewed or replaced with a signed-in browser session, never with a key itself.
- A deactivated user's key stops working along with their session.
- If the Data Protection keys are lost (see Troubleshooting), stored API keys still work but can't be
  shown any more; the page says so and the user replaces the key to get a copyable one.
- Copy uses the Clipboard API on HTTPS or localhost, and falls back to select-and-copy over plain HTTP.
- The `ApiKey` authentication scheme (`Auth/ApiKeyAuthentication.cs`) accepts the key. Only endpoints
  that require the `ApiKey` policy take it (today just `POST /api/flights`); every other endpoint is
  cookie-only, so a key can't read the profile or view and replace itself.

### Uploading a flight (`POST /api/flights`)

```http
POST /api/flights
X-Api-Key: fsf_…
Content-Type: application/json

{
  "flightId":   "5f0c8e2a-1111-4c3e-9d1a-2b7f6e0a9c01",
  "startedUtc": "2026-11-04T01:30:48Z",
  "endedUtc":   "2026-11-04T01:35:43Z",
  "distanceNm": 3.2,
  "sampleCount": 60,
  "departure": { "ident": "KALB", "name": "Albany International Airport", "latitude": 42.7497, "longitude": -73.7875 },
  "arrival":   { "latitude": 42.78215, "longitude": -73.74473 },
  "aircraft":  { "title": "Cessna Skyhawk G1000 Asobo", "type": "Cessna", "model": "C172",
                 "registration": "N172SP", "airline": null, "flightNumber": null }
}
```

- `flightId` (the companion app's flight GUID), `startedUtc` and `endedUtc` are required; the rest is optional.
  `departure` / `arrival` can carry an airport (`ident`, `name`), a position, both, or be left out.
- Checks: end not before start, at most 7 days long, end at most 1 hour in the future (clock skew);
  distance 0–25,000 NM; `ident` 1–10 letters, digits or `-` (stored upper-case); `name` up to 100
  characters; latitude/longitude in range and given together; `aircraft.title` up to 256 characters and
  its other fields up to 64, no control characters (blank fields are stored as null).
- The same `flightId` from the same user updates the existing row, so the app can retry safely.
- The response is the stored flight: `{ id, flightId, startedUtc, endedUtc, durationSeconds, distanceNm,
  sampleCount, departure, arrival, aircraft, uploadedUtc, updatedUtc }`.

### The `flights` table

Created by schema migration 3; migration 4 added the aircraft columns. One row per uploaded flight,
`UNIQUE (User_ID, Client_Flight_ID)`.

| Column | Notes |
|---|---|
| `Flight_ID` | INTEGER PK AUTOINCREMENT |
| `User_ID` | The API key's user |
| `Client_Flight_ID` | The companion app's flight GUID |
| `Started_UTC`, `Ended_UTC` | ISO 8601 UTC text, same format as the other tables |
| `Distance_NM`, `Sample_Count` | Airborne distance and the number of samples it came from |
| `Departure_Ident`, `_Name`, `_Latitude`, `_Longitude` | All nullable |
| `Arrival_Ident`, `_Name`, `_Latitude`, `_Longitude` | All nullable; position only when the flight didn't end at an airport |
| `Aircraft_Title`, `Aircraft_Type`, `Aircraft_Model`, `Registration`, `Airline`, `Flight_Number` | From the aircraft's ATC settings in the sim; all nullable (null for flights uploaded before migration 4) |
| `Created_UTC`, `Updated_UTC` | First upload and latest re-post |

### Pages

- **Profile** (`index.html`): profile editor, **Recent flights** (last 5) and the API key.
- **Flights** (`flights.html`): totals (flights, distance, time) and the full history, 50 at a time with **Load more**.
  Hovering an airport code shows its name; a position means the flight didn't end at an airport.

## Project layout

```
Web/
├── MsfsTelemetry.Web.csproj      Web SDK, net10.0, InProcess hosting, Microsoft.Data.Sqlite
├── Program.cs                    DI, logging, CORS, static files, endpoint mapping
├── appsettings.json              Connection string, WAL/synchronous settings, CORS origins, logging
├── appsettings.Development.json  Per-request HTTP logging for local dev
├── web.config                    ASP.NET Core Module V2 (used only by IIS)
├── Properties/launchSettings.json  `dotnet run` profile (port 5000)
├── Auth/
│   ├── GoogleAuthOptions.cs      "Authentication:Google" config section
│   ├── CurrentUser.cs            ClaimsPrincipal.GetUserId()
│   └── ApiKeyAuthentication.cs   "ApiKey" scheme: X-Api-Key / Bearer for the companion app
├── Data/
│   ├── DatabaseOptions.cs        "Database" config section and PRAGMA allowlists
│   ├── AirportsDb.cs             Connection factory, startup init (WAL + migrations), health probe
│   ├── Schema.cs                 Versioned schema migrations (PRAGMA user_version)
│   ├── UsersRepository.cs        users table: sign-in upsert, lookup, profile update
│   ├── ApiKeysRepository.cs      api_keys table: create, replace, look up a user by key
│   └── FlightsRepository.cs      flights table: upsert an uploaded summary, list, totals
├── Endpoints/
│   ├── StatusEndpoints.cs        GET /api/status
│   ├── AuthEndpoints.cs          /api/auth/config, /google, /logout, /me
│   ├── ProfileEndpoints.cs       PUT /api/profile, GET/POST /api/profile/api-key
│   └── FlightEndpoints.cs        POST /api/flights (API key), GET /api/flights (session)
├── wwwroot/
│   ├── index.html                Home: sign-in, profile editor, recent flights, API key (Tailwind via CDN)
│   ├── flights.html              Flight history: totals and all flights
│   ├── dashboard.html            System status dashboard (backend + database health)
│   ├── js/flights.js             Flight tables for index.html (last 5) and flights.html (paged)
│   ├── js/dashboard.js           Calls /api/status and renders the dashboard
│   └── js/auth.js                Shared by all pages: Google button, session check, profile form, API key; raises auth:signed-in/out
├── deploy/
│   ├── Install-IisSite.ps1       One-time IIS site and app pool setup (no idle timeout, no recycling)
│   └── Deploy-ToIis.ps1          Publish, then copy into the site (keeps App_Data)
└── App_Data/                     Created at runtime: airports.db, -wal, -shm (git-ignored)
```

To add a feature, add `Endpoints/XxxEndpoints.cs` with a `MapXxxEndpoints(this RouteGroupBuilder)`
extension and call it next to `api.MapStatusEndpoints()` in `Program.cs`. Data access goes in
`Data/` and takes `AirportsDb` as a dependency.

## Notes on SQLite and WAL

- **`Journal Mode=Wal` is not a valid Microsoft.Data.Sqlite connection-string keyword.** It's a
  System.Data.SQLite keyword, and Microsoft.Data.Sqlite throws `ArgumentException` on it. This
  app runs `PRAGMA journal_mode = WAL` once at startup instead (set by `Database:JournalMode`).
  The setting is stored in the database file, so it persists.
- `PRAGMA synchronous = NORMAL` is set every time a connection opens (`Database:Synchronous`).
- A relative `Data Source` is resolved against the app folder, not the working directory, so the
  database lands in the same place under `dotnet run` and under IIS.
- WAL needs the database on a **local disk**. It doesn't work on a network share.
- WAL creates `airports.db-wal` and `airports.db-shm` beside the database. The process must be
  able to create files in `App_Data`, not just write to `airports.db`.
- If startup initialization fails, the app still starts and `/api/status` returns **503** with the error.

## 1. Build and run locally

Prerequisite: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`dotnet --list-sdks`).

```powershell
cd Web
dotnet restore
dotnet build
dotnet run            # http://localhost:5000, opens the browser
```

`http://localhost:5000/dashboard.html` should show a green **"Backend and database are responding"** banner, with
`Journal mode: wal` and `Host: Kestrel`. To check from the command line:

```powershell
Invoke-RestMethod http://localhost:5000/api/status
```

Other ports: `dotnet run --urls http://localhost:5080`. Edit-and-refresh loop: `dotnet watch`.

## 2. Deploy to IIS

### One-time machine setup (elevated PowerShell)

1. **Enable IIS**, plus Application Initialization so the app loads before the first request:
   ```powershell
   # Windows 10/11
   Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole, IIS-WebServer, IIS-ApplicationInit -All
   # Windows Server
   Install-WindowsFeature Web-Server, Web-AppInit -IncludeManagementTools
   ```
2. **Install the .NET 10 Hosting Bundle** (not just the runtime) from
   https://dotnet.microsoft.com/download/dotnet/10.0. It installs the ASP.NET Core Module (ANCM).
   Then run `iisreset`. If IIS was enabled *after* the bundle was installed, run the bundle
   installer again and choose **Repair**.
3. **Create the site and app pool:**
   ```powershell
   cd Web\deploy
   .\Install-IisSite.ps1 -SiteName MsfsTelemetryWeb -PhysicalPath C:\inetpub\MsfsTelemetryWeb -Port 8080
   ```
   The script sets the pool to No Managed Code and 64-bit, with **AlwaysRunning**,
   **idle timeout 0**, **no periodic recycle**, and **preload** on. It also gives the pool
   identity Modify rights on `App_Data` and `logs`. Together these keep the app from being
   unloaded when idle and from restarting on a schedule.

### Each deployment

```powershell
cd Web\deploy
.\Deploy-ToIis.ps1 -PhysicalPath C:\inetpub\MsfsTelemetryWeb
```

The script publishes a Release build, puts the site into `app_offline.htm` maintenance mode
(this releases the DLL and database locks), mirrors the files across while **keeping
`App_Data`**, then brings the site back up.

Manual alternative: `dotnet publish -c Release -o publish`, then copy `publish\*` into the site
folder. Don't overwrite `App_Data`.

### Verify

Open `http://localhost:8080/dashboard.html`. The banner should be green and `Host` should read
**IIS (in-process)**.

### Troubleshooting

| Symptom | Cause / fix |
|---|---|
| HTTP 500.19 | ANCM not installed: install or repair the Hosting Bundle, then `iisreset`. |
| HTTP 500.31 / 500.32 | .NET 10 runtime missing, or a 32-bit pool. Install the Hosting Bundle and set *Enable 32-bit* to False. |
| HTTP 500.30 | The app failed at startup. Set `stdoutLogEnabled="true"` in the deployed `web.config`, reload, read `logs\stdout_*.log`, then set it back. Also check the **Windows Event Log → Application**. |
| Page loads but database shows **503 / unable to open database** | The pool identity can't write to `App_Data`. Run `Install-IisSite.ps1` again, or `icacls App_Data /grant "IIS AppPool\MsfsTelemetryWeb:(OI)(CI)M"`. |
| First request after a while is slow | Preload is off (Application Initialization isn't installed). Install it and run `Install-IisSite.ps1` again. |
| Google button shows "origin is not allowed" / error 400 | Add the exact origin (scheme, host, port) the page is opened from to *Authorized JavaScript origins*. Changes can take a few minutes to apply. |
| "Google sign-in isn't configured yet" | `Authentication:Google:ClientId` is empty on that server. |
| Everyone is signed out after each app restart | Data Protection keys aren't being kept. Make sure the app pool has `loadUserProfile = true` (`Install-IisSite.ps1` sets it). |
| Hosted as a sub-application (`/telemetry`) | Open it with the trailing slash, `http://server/telemetry/`. The frontend uses relative URLs. |
