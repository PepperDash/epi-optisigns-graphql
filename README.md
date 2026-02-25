![PepperDash Logo](/images/logo_pdt_no_tagline_600.png)
# OptiSigns GraphQL Plugin

[OptiSigns API Reference](https://support.optisigns.com/hc/en-us/articles/4414558392339-API-Reference)

[OptiSigns Node SDK (GraphQL)](https://github.com/optisigns/optisigns-node)

## Overview

This plugin integrates PepperDash Essentials with the [OptiSigns](https://www.optisigns.com/) digital signage platform via its GraphQL API. It targets **PepperDash Essentials 2.x** (`net472`, Crestron 4-Series) and provides control panel-style operations mapped to the OptiSigns cloud API.

### API Details

| Item           | Value                                           |
| -------------- | ----------------------------------------------- |
| Type           | GraphQL                                         |
| Endpoint       | `https://graphql-gateway.optisigns.com/graphql` |
| Authentication | `Authorization: Bearer {apiKey}`                |
| Format         | HTTP POST, `application/json`                   |

### Feature Mapping

| Control Surface Operation | OptiSigns API Action                                                        |
| ------------------------- | --------------------------------------------------------------------------- |
| Power ON                  | `pushToScreens` — restores last active or default playlist                  |
| Power OFF                 | `updateDevice` — sets `currentType` to `"NONE"` (blanks screen)             |
| Input / Playlist select   | `pushToScreens` — pushes selected playlist by 1-based index or direct `_id` |
| Poll device status        | `devices` query — refreshes `currentType`, `status`, `lastHeartBeat`        |
| Poll playlist list        | `playlists` query — refreshes labels for input selection                    |

> **Note:** `pushToScreens` and the `playlists` list query are Phase 2 features in the official OptiSigns TypeScript SDK. This plugin calls those GraphQL mutations and queries directly. If `playlists` is not yet live on the backend, the plugin silently falls back to the static `playlists` list in the device config.

---

## Essentials Device Configuration

The `deviceId` is the MongoDB `_id` of the OptiSigns screen. Obtain it by calling `listAllDevices` in the OptiSigns SDK or from the OptiSigns web app device settings. The `teamId` and `apiKey` are found in your OptiSigns account settings at `https://app.optisigns.com/account-setting`.

```json
{
  "key": "optiSigns-lobby",
  "name": "Lobby Digital Sign",
  "type": "optiSigns",
  "group": "signage",
  "uid": 1,
  "properties": {
    "apiKey": "YOUR_OPTISIGNS_API_KEY_HERE",
    "teamId": "YOUR_TEAM_ID_HERE",
    "deviceId": "YOUR_OPTISIGNS_SCREEN_DEVICE_ID_HERE",
    "pollIntervalMs": 30000,
    "playlistPollIntervalMs": 300000,
    "defaultPlaylistId": "YOUR_DEFAULT_PLAYLIST_ID_HERE",
    "playlists": [
      { "id": "PLAYLIST_ID_1", "name": "Welcome Loop" },
      { "id": "PLAYLIST_ID_2", "name": "Event Content" },
      { "id": "PLAYLIST_ID_3", "name": "Emergency Messaging" }
    ]
  }
}
```

### Properties Reference

| Property                 | Type   | Required | Default  | Description                                                                       |
| ------------------------ | ------ | -------- | -------- | --------------------------------------------------------------------------------- |
| `apiKey`                 | string | Yes      | —        | OptiSigns API key (Bearer token)                                                  |
| `teamId`                 | string | Yes      | —        | OptiSigns team ID, required in all mutations                                      |
| `deviceId`               | string | Yes      | —        | MongoDB `_id` of the target OptiSigns screen                                      |
| `pollIntervalMs`         | int    | No       | `30000`  | Device status poll interval in ms                                                 |
| `playlistPollIntervalMs` | int    | No       | `300000` | Playlist list refresh interval in ms                                              |
| `defaultPlaylistId`      | string | No       | —        | Playlist pushed on `PowerOn` when no prior playlist is known                      |
| `playlists`              | array  | No       | `[]`     | Static fallback playlist list; used when the API `playlists` query is unavailable |

---

## Essentials Bridging

```json
{
  "key": "eiscBridge-signage",
  "name": "Signage EISC Bridge",
  "type": "eiscApiAdvanced",
  "group": "api",
  "uid": 2,
  "properties": {
    "control": {
      "method": "ipidTcp",
      "ipid": "A0",
      "tcpSshProperties": {
        "address": "127.0.0.2",
        "port": 0
      }
    },
    "devices": [
      {
        "deviceKey": "optiSigns-lobby",
        "joinStart": 1
      }
    ]
  }
}
```

---

## Essentials Bridge Join Map

Join numbers below are **relative to `joinStart`**. With `joinStart: 1`, join numbers are as shown.

### Digitals

| Input (From SIMPL)   | Join | Output (To SIMPL) |
| -------------------- | ---- | ----------------- |
|                      | 1    | Is Online fb      |
| Power On (pulse)     | 2    | Power Is On fb    |
| Power Off (pulse)    | 3    | Power Is Off fb   |
| Power Toggle (pulse) | 4    |                   |
| Poll Now (pulse)     | 5    |                   |
|                      | 6    | Is Polling fb     |

### Analogs

| Input (From SIMPL)                              | Join | Output (To SIMPL)                                                      |
| ----------------------------------------------- | ---- | ---------------------------------------------------------------------- |
|                                                 | 1    | Device Status fb (0=Unknown, 1=Ok/ONLINE, 2=Warning/SLEEP, 3=Error/OFFLINE) |
|                                                 | 5    | Playlist Count fb (capped at 30)                                       |
| Select Playlist by Index (1-based)              | 6    | Select Playlist by Index fb (current playlist index)                   |

### Serials

| Input (From SIMPL)    | Join  | Output (To SIMPL)                                          |
| --------------------- | ----- | ---------------------------------------------------------- |
|                       | 1     | Device Name fb                                             |
|                       | 2     | Last HeartBeat fb (ISO 8601 timestamp)                     |
| Select Playlist by ID | 6     | Current Playlist Name fb                                   |
|                       | 10    | Playlist List fb (pipe-delimited: `"Name1\|Name2\|Name3"`) |
|                       | 11    | Playlist Name[1] fb                                        |
|                       | 12    | Playlist Name[2] fb                                        |
|                       | …     | …                                                          |
|                       | 40    | Playlist Name[30] fb                                       |

#### Analog Join Notes

- **A1 DeviceStatus** — maps the raw OptiSigns API status string to an analog value: `0` = StatusUnknown, `1` = IsOk (`ONLINE`), `2` = InWarning (`SLEEP`), `3` = InError (`OFFLINE`).
- **A6 SelectPlaylistByIndex** — 1-based index into the available playlist list. Send from SIMPL to select; feedback reflects the current active playlist. `0` = no playlist active or unknown.

#### Serial Join Notes

- **S6 SelectPlaylistById / CurrentPlaylistName** — shared join: send a raw OptiSigns playlist `_id` string from SIMPL to select it immediately without needing to resolve its index. Feedback returns the name of the currently active playlist. Useful for config-driven or event-triggered room logic.
- **S10 PlaylistList** — pipe-delimited names in index order. Index 1 corresponds to the first name. Suitable for populating a SIMPL+ string array or a button list.
- **S11–S40 PlaylistName[N]** — individual playlist name strings (S11 = playlist 1, …, S40 = playlist 30). Slots beyond the current `PlaylistCount` (A5) are sent as empty strings. Use A5 to know how many slots are populated.

---

## Polling Behavior

Two independent timers run after `Initialize()`:

| Timer         | Default Interval                | What It Does                                                         |
| ------------- | ------------------------------- | -------------------------------------------------------------------- |
| Status poll   | 30s (`pollIntervalMs`)          | Queries device `currentType`, `status`, `lastHeartBeat`              |
| Playlist poll | 5min (`playlistPollIntervalMs`) | Refreshes the available playlist list and updates S10, S11–S40, A5, and A6 feedback |

The status poll starts 2 seconds after initialization to let the playlist poll complete first. After a Power On, Power Off, or playlist select command, a one-shot confirmation poll fires 2 seconds later to reconcile optimistic UI state with the actual API response.

The device is marked **offline** after 3 consecutive status poll failures. It comes back online on the next successful poll.

---

## DEVJSON Commands

Update `programIndex` and `deviceKey` to match your environment.

```
devjson:1 {"deviceKey":"optiSigns-lobby", "methodName":"PowerOn", "params":[]}
devjson:1 {"deviceKey":"optiSigns-lobby", "methodName":"PowerOff", "params":[]}
devjson:1 {"deviceKey":"optiSigns-lobby", "methodName":"PowerToggle", "params":[]}

devjson:1 {"deviceKey":"optiSigns-lobby", "methodName":"SelectPlaylistByIndex", "params":[1]}
devjson:1 {"deviceKey":"optiSigns-lobby", "methodName":"SelectPlaylistByIndex", "params":[2]}
devjson:1 {"deviceKey":"optiSigns-lobby", "methodName":"SelectPlaylistByIndex", "params":[3]}

devjson:1 {"deviceKey":"optiSigns-lobby", "methodName":"SelectPlaylistById", "params":["YOUR_PLAYLIST_ID_HERE"]}
```

---

## Build

Requires .NET SDK 6.0 or later (for SDK-style project support on the build host; the output targets `net472`).

```bash
dotnet build src/epi-optisigns-graphql.4Series.csproj
```

The post-build target zips the output to:

```
output/epi-optisigns-graphql.4Series.{version}.cplz
```

Deploy the `.cplz` to the Crestron 4-Series processor using the Crestron Toolbox *Send to Processor* tool or `progload` via SSH.
