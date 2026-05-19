# OptiSigns GraphQL API — Test Scripts

Python scripts for manually testing the OptiSigns GraphQL API. No third-party dependencies — standard library only.

---

## Files

| File                | Purpose                                            |
| ------------------- | -------------------------------------------------- |
| `poll_playlists.py` | Query all playlists in the account                 |
| `poll_devices.py`   | Query all devices (screens/players) in the account |
| `poll_assets.py`    | Query assets — all, or filtered by name            |
| `poll_me.py`        | Query the current authenticated user               |
| `poll_teams.py`     | Query all teams in the account                     |
| `assign_content.py` | Assign an asset or playlist to a device            |
| `_utils.py`         | Shared logging utility (not run directly)          |
| `.env`              | Local secrets and test IDs — never committed       |
| `logs/`             | Per-run log files — never committed                |

---

## Setup

### 1. Configure .env

All test values — API key, device IDs, and asset IDs — are stored in `test-scripts/.env` so they never appear inline in commands or get committed to source control.

> **Note:** `test-scripts/.env` and `test-scripts/logs/` are both listed in `.gitignore` and will never be committed.

**Full .env structure:**

```bash
# ── Authentication ────────────────────────────────────────────────────────────
OPTISIGNS_API_KEY=your_api_key_here
TEAM_ID=1

# ── Teams ─────────────────────────────────────────────────────────────────────
TEAM_A=<team_id>
TEAM_B=<team_id>

# ── Houston Center Devices ─────────────────────────────────────────────────────
DEVICE_LOBBY=<device_id>
DEVICE_SUITE1=<device_id>
DEVICE_SUITE2=<device_id>
DEVICE_SUITE3=<device_id>
DEVICE_SUITE4=<device_id>
DEVICE_SUITE5=<device_id>
DEVICE_HALLWAY=<device_id>

# ── Houston Center Assets ──────────────────────────────────────────────────────
ASSET_LOGO=<asset_id>
ASSET_BRAND_VIDEO=<asset_id>
ASSET_SUITE1=<asset_id>
ASSET_SUITE2=<asset_id>
ASSET_SUITE3=<asset_id>
ASSET_SUITE4=<asset_id>
ASSET_SUITE5=<asset_id>

# ── Houston Center Playlists ───────────────────────────────────────────────────
PLAYLIST_1=<playlist_id>
PLAYLIST_2=<playlist_id>
```

### 2. Load .env into your shell

Source the file once per terminal session to expand all variables:

```bash
source ./test-scripts/.env
```

After sourcing, all `DEVICE_*` and `ASSET_*` variables are available for use in `--device-id` and `--content-id` arguments directly.

---

## Logging

Every script run automatically creates a timestamped log file in `test-scripts/logs/`:

```
test-scripts/logs/YYYY-MM-DD_HH:MM:SS:tt-<scriptname>.txt
```

`tt` = centiseconds (2 digits). All console output — including errors — is written to both the terminal and the log file simultaneously. The log path is printed at the start of each run.

---

## Scripts

Run all scripts from the **repository root**:

```bash
cd /path/to/epi-optisigns-graphql
source ./test-scripts/.env
```

---

### poll_playlists.py

Queries all playlists available in the OptiSigns account.

```bash
python3 ./test-scripts/poll_playlists.py

# Filter by team
python3 ./test-scripts/poll_playlists.py --team-id $TEAM_B

# Override API key
python3 ./test-scripts/poll_playlists.py --api-key YOUR_KEY
```

| Argument    | Required | Description                             |
| ----------- | -------- | --------------------------------------- |
| `--team-id` | No       | Filter results to a specific team       |
| `--api-key` | No       | Overrides `OPTISIGNS_API_KEY` in `.env` |

**Output columns:** `ID`, `Name`, `Duration (s)`, `Tags`

---

### poll_devices.py

Queries all devices (screens/players) registered in the OptiSigns account.

```bash
python3 ./test-scripts/poll_devices.py

# Filter by team
python3 ./test-scripts/poll_devices.py --team-id $TEAM_B

# Override API key
python3 ./test-scripts/poll_devices.py --api-key YOUR_KEY
```

| Argument    | Required | Description                             |
| ----------- | -------- | --------------------------------------- |
| `--team-id` | No       | Filter results to a specific team       |
| `--api-key` | No       | Overrides `OPTISIGNS_API_KEY` in `.env` |

**Output columns:** `ID`, `Name`, `Type`, `App Version`, `Playlist ID`

---

### poll_assets.py

Queries assets in the OptiSigns account. Returns all assets when `--name` is omitted, or filters by `originalFileName` when provided.

```bash
# All assets
python3 ./test-scripts/poll_assets.py

# Filter by name
python3 ./test-scripts/poll_assets.py --name "Houston Weather Test"

# Filter by team
python3 ./test-scripts/poll_assets.py --team-id $TEAM_B

# Combine filters
python3 ./test-scripts/poll_assets.py --name "Lobby" --team-id $TEAM_B

# Override API key
python3 ./test-scripts/poll_assets.py --api-key YOUR_KEY
```

| Argument    | Required | Description                                             |
| ----------- | -------- | ------------------------------------------------------- |
| `--name`    | No       | Filter by `originalFileName`; omit to return all assets |
| `--team-id` | No       | Filter results to a specific team                       |
| `--api-key` | No       | Overrides `OPTISIGNS_API_KEY` in `.env`                 |

**Output columns:** `ID`, `Name`, `App Type`, `File Type`, `Filename`

---

### poll_me.py

Returns info about the currently authenticated user.

```bash
python3 ./test-scripts/poll_me.py

# Override API key
python3 ./test-scripts/poll_me.py --api-key YOUR_KEY
```

| Argument    | Required | Description                             |
| ----------- | -------- | --------------------------------------- |
| `--api-key` | No       | Overrides `OPTISIGNS_API_KEY` in `.env` |

**Output fields:** `ID`, `Account ID`, `Name`, `Email`, `Username`

---

### poll_teams.py

Lists all teams in the account. Use `--team-id` to inspect a specific team.

```bash
# All teams
python3 ./test-scripts/poll_teams.py

# Specific team
python3 ./test-scripts/poll_teams.py --team-id $TEAM_B

# Override API key
python3 ./test-scripts/poll_teams.py --api-key YOUR_KEY
```

| Argument    | Required | Description                             |
| ----------- | -------- | --------------------------------------- |
| `--team-id` | No       | Filter to a specific team ID            |
| `--api-key` | No       | Overrides `OPTISIGNS_API_KEY` in `.env` |

**Output columns:** `ID`, `Name`, `Account ID`, `Created At`

---

### assign_content.py

Assigns an asset or playlist to a device via the `updateDevice` mutation.

```bash
# Assign the lobby brand video to the lobby screen
python3 ./test-scripts/assign_content.py --type ASSET --device-id $DEVICE_LOBBY --content-id $ASSET_BRAND_VIDEO

# Assign a suite name-plate asset to its screen
python3 ./test-scripts/assign_content.py --type ASSET --device-id $DEVICE_SUITE1 --content-id $ASSET_SUITE1

# Assign a playlist to a suite screen
python3 ./test-scripts/assign_content.py --type PLAYLIST --device-id $DEVICE_SUITE5 --content-id $PLAYLIST_1

# Specify team explicitly
python3 ./test-scripts/assign_content.py --type ASSET --device-id $DEVICE_LOBBY --content-id $ASSET_BRAND_VIDEO --team-id $TEAM_B
```

| Argument        | Required | Default                           | Description                                                       |
| --------------- | -------- | --------------------------------- | ----------------------------------------------------------------- |
| `--type`        | No       | `ASSET`                           | `ASSET` or `PLAYLIST`                                             |
| `--device-id`   | No       | See below                         | Device `_id` to update; use `$DEVICE_*` variables                 |
| `--content-id`  | No       | See below                         | Asset or playlist `_id`; use `$ASSET_*` / `$PLAYLIST_*` variables |
| `--team-id`     | No       | `TEAM_ID` in `.env` (default `1`) | Team that owns the device                                         |
| `--device-name` | No       | `GraphAPI Test`                   | Display name written to the device                                |
| `--orientation` | No       | `LANDSCAPE`                       | `LANDSCAPE` or `PORTRAIT`                                         |
| `--api-key`     | No       | —                                 | Overrides `OPTISIGNS_API_KEY` in `.env`                           |


