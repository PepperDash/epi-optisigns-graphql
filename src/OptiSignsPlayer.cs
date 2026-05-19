using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.DeviceSupport;
using Newtonsoft.Json;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    /// <summary>
    /// PepperDash Essentials bridgeable device for an individual OptiSigns player (screen).
    ///
    /// Maps AV-style power and input-select operations to the OptiSigns GraphQL API:
    ///   Power OFF  ->  updateDevice  (currentType: "NONE")       Blanks the screen
    ///   Power ON   ->  pushToScreens (restores last/default playlist, type: "NOW")
    ///   Input N    ->  pushToScreens (currentPlaylistId: id, type: "NOW")
    ///
    /// Alternative methods using updateDevice mutation (AssignPlaylistAsset*):
    ///   AssignPlaylistAsset* ->  updateDevice (currentType: "PLAYLIST", currentAssetId: id)
    ///   Use these when pushToScreens doesn't properly switch from asset mode.
    ///
    /// The player receives its GraphQL client and poll intervals from the parent server.
    /// Each player maintains its own state and is independently bridgeable.
    /// </summary>
    public class OptiSignsPlayer : EssentialsBridgeableDevice
    {
        private readonly OptiSignsPlayerConfig playerConfig;
        private readonly OptiSignsGraphQLClient client;
        private readonly int pollIntervalMs;
        private readonly int playlistPollIntervalMs;
        private readonly int playlistLimit;

        // ──────────────────────────────────────────────
        // Internal state
        // ──────────────────────────────────────────────

        private bool isOnline;
        private bool powerIsOn;
        private string currentType;
        private string currentPlaylistId;
        private string currentPlaylistName;
        private string deviceName;
        private int deviceStatus;
        private string lastHeartBeat;
        private int currentPlaylistIndex;   // 1-based; 0 = unknown / none
        private bool isPolling;
        private bool playlistSelectionBusy;

        // Resolved playlist list. Written atomically (reference swap) from the poll thread.
        private List<PlaylistNode> playlists = new List<PlaylistNode>();
        private int playlistGroupOffset;

        // Last active playlist ID used to restore content when powered on.
        private string lastActivePlaylistId;

        // Consecutive API failures used to drive the IsOnline feedback.
        private int consecutiveFailures;
        private const int maxFailuresBeforeOffline = 3;

        // Maximum number of playlist name serial joins bridged to SIMPL (S11-S40).
        public const int MaxPlaylistBridgeCount = 30;

        // When true (D6 high), playlist items are sent as JSON; when false, as name only
        private bool playlistItemAsJsonFormat = false;

        private CTimer statusPollTimer;
        private CTimer playlistPollTimer;
        private CTimer _confirmationPollTimer;
        private bool isPlaylistPolling;

        // ──────────────────────────────────────────────
        // Feedbacks
        // ──────────────────────────────────────────────

        public BoolFeedback IsOnlineFeedback { get; private set; }
        public BoolFeedback PowerIsOnFeedback { get; private set; }
        public BoolFeedback PowerIsOffFeedback { get; private set; }
        public BoolFeedback IsPollingFeedback { get; private set; }
        public BoolFeedback PlaylistSelectionBusyFeedback { get; private set; }
        public IntFeedback AbsoluteInputSelectFeedback { get; private set; }
        public IntFeedback RelativeInputSelectFeedback { get; private set; }
        public IntFeedback PlaylistCountFeedback { get; private set; }
        public StringFeedback DeviceNameFeedback { get; private set; }
        public StringFeedback CurrentPlaylistNameFeedback { get; private set; }
        public IntFeedback DeviceStatusFeedback { get; private set; }
        public StringFeedback LastHeartBeatFeedback { get; private set; }
        public StringFeedback[] PlaylistNameFeedbacks { get; private set; }

        // ──────────────────────────────────────────────
        // Constructor
        // ──────────────────────────────────────────────

        /// <summary>
        /// Creates a new OptiSigns player instance.
        /// </summary>
        /// <param name="key">Unique device key (e.g., "optisign-server-player-1")</param>
        /// <param name="name">Display name for the player</param>
        /// <param name="playerConfig">Player-specific configuration</param>
        /// <param name="client">Shared GraphQL client from the parent server</param>
        /// <param name="pollIntervalMs">Status poll interval in milliseconds</param>
        /// <param name="playlistPollIntervalMs">Playlist poll interval in milliseconds</param>
        /// <param name="playlistLimit">Maximum playlists to fetch per request (0 = server default)</param>
        public OptiSignsPlayer(
            string key,
            string name,
            OptiSignsPlayerConfig playerConfig,
            OptiSignsGraphQLClient client,
            int pollIntervalMs,
            int playlistPollIntervalMs,
            int playlistLimit = 100)
            : base(key, name)
        {
			this.playerConfig = playerConfig;
			this.client = client;
			this.pollIntervalMs = pollIntervalMs;
			this.playlistPollIntervalMs = playlistPollIntervalMs;
			this.playlistLimit = playlistLimit;
            // Seed from static config so labels are available before the first API poll.
            if (this.playerConfig.Playlists != null && this.playerConfig.Playlists.Count > 0)
            {
				playlists = this.playerConfig.Playlists
                    .Select(p => new PlaylistNode { Id = p.Id, Name = p.Name, Type = p.Type })
                    .ToList();
            }

            IsOnlineFeedback = new BoolFeedback(key + "-IsOnline", () => isOnline);
            PowerIsOnFeedback = new BoolFeedback(key + "-PowerIsOn", () => powerIsOn);
            PowerIsOffFeedback = new BoolFeedback(key + "-PowerIsOff", () => !powerIsOn);
            IsPollingFeedback = new BoolFeedback(key + "-IsPolling", () => isPolling);
            PlaylistSelectionBusyFeedback = new BoolFeedback(key + "-PlaylistSelectionBusy", () => playlistSelectionBusy);
            AbsoluteInputSelectFeedback = new IntFeedback(key + "-AbsoluteInputSelect", () => currentPlaylistIndex);
            RelativeInputSelectFeedback = new IntFeedback(key + "-RelativeInputSelect", () => GetPageRelativePlaylistIndex());
			DeviceNameFeedback = new StringFeedback(key + "-DeviceName", 
                () => !string.IsNullOrEmpty(deviceName) 
                    ? deviceName
					: !string.IsNullOrEmpty(this.playerConfig.Name) 
                        ? this.playerConfig.Name 
                        : Name);
            CurrentPlaylistNameFeedback = new StringFeedback(
                key + "-CurrentPlaylist", () => currentPlaylistName ?? string.Empty);
            DeviceStatusFeedback = new IntFeedback(
                key + "-DeviceStatus", () => deviceStatus);
            LastHeartBeatFeedback = new StringFeedback(
                key + "-LastHeartBeat", () => lastHeartBeat ?? string.Empty);

            PlaylistCountFeedback = new IntFeedback(
                key + "-PlaylistCount",
                () => playlists.Count);

            // One feedback per bridged slot (0-based index, 0 = playlist 1 on the SIMPL side).
            PlaylistNameFeedbacks = new StringFeedback[MaxPlaylistBridgeCount];
            for (var i = 0; i < MaxPlaylistBridgeCount; i++)
            {
                var capturedI = i;
                PlaylistNameFeedbacks[i] = new StringFeedback(
                    string.Format("{0}-PlaylistName-{1}", key, capturedI + 1),
                    () =>
                    {
                        var actualIndex = playlistGroupOffset + capturedI;
                        if (actualIndex >= playlists.Count)
                            return string.Empty;

                        var playlist = playlists[actualIndex];

                        if (playlistItemAsJsonFormat)
                            return JsonConvert.SerializeObject(new { id = playlist.Id, name = playlist.Name });
                        else
                            return playlist.Name ?? string.Empty;
                    });
            }
        }

        // ──────────────────────────────────────────────
        // Initialization
        // ──────────────────────────────────────────────

        public override void Initialize()
        {
            base.Initialize();

            this.LogInformation("Initializing. DeviceId={0}, TeamId={1}",
                playerConfig.DeviceId, playerConfig.TeamId);

            if (string.IsNullOrEmpty(playerConfig.TeamId) ||
                string.IsNullOrEmpty(playerConfig.DeviceId))
            {
                this.LogError("Required configuration missing (teamId / deviceId). Player will not poll.");
                return;
            }

            // Playlist poll fires immediately (dueTime=0) so labels are ready before the UI appears.
            playlistPollTimer = new CTimer(
                PlaylistPollTimerCallback, null, 0, playlistPollIntervalMs);

            // Status poll starts 2 seconds later to let the playlist poll finish first.
            statusPollTimer = new CTimer(
                StatusPollTimerCallback, null, 2000, pollIntervalMs);
        }

        // ──────────────────────────────────────────────
        // Timer callbacks
        // ──────────────────────────────────────────────

        private void StatusPollTimerCallback(object notUsed)
        {
            CrestronInvoke.BeginInvoke(_ => PollDeviceStatusAsync());
        }

        private void PlaylistPollTimerCallback(object notUsed)
        {
            CrestronInvoke.BeginInvoke(_ => PollPlaylistsAsync());
        }

        // ──────────────────────────────────────────────
        // Polling — device status
        // ──────────────────────────────────────────────

        private async void PollDeviceStatusAsync()
        {
            if (isPolling) return;
            SetPolling(true);
            try
            {
                var node = await client.GetDeviceStatusAsync(playerConfig.DeviceId, Key)
                    .ConfigureAwait(false);

                if (node == null)
                {
                    consecutiveFailures++;
                    this.LogVerbose("Poll null (failure {0}/{1})",
                        consecutiveFailures, maxFailuresBeforeOffline);
                    if (consecutiveFailures >= maxFailuresBeforeOffline && isOnline)
                    {
                        isOnline = false;
                        IsOnlineFeedback.FireUpdate();
                        this.LogWarning("Offline after {0} failures",
                            consecutiveFailures);
                    }
                    return;
                }

                consecutiveFailures = 0;

                // Only log poll details at Verbose when values actually change
                if (node.CurrentType != currentType ||
                    node.CurrentPlaylistId != currentPlaylistId)
                {
                    this.LogVerbose("Poll: type={0} playlist={1} asset={2}",
                        node.CurrentType ?? "-",
                        node.CurrentPlaylistId ?? "-",
                        node.CurrentAssetId ?? "-");
                }

                if (!isOnline)
                {
                    isOnline = true;
                    IsOnlineFeedback.FireUpdate();
                    this.LogInformation("Online");
                }

                currentType = node.CurrentType;

                var newPowerIsOn = !string.IsNullOrEmpty(node.CurrentType) &&
                    !node.CurrentType.Equals("NONE", StringComparison.OrdinalIgnoreCase);

                if (newPowerIsOn != powerIsOn)
                {
                    this.LogDebug("Power: {0}", newPowerIsOn ? "ON" : "OFF");
                    powerIsOn = newPowerIsOn;
                    PowerIsOnFeedback.FireUpdate();
                    PowerIsOffFeedback.FireUpdate();
                }

                // Determine effective playlist ID based on API response.
                // pushToScreens sets currentPlaylistId, updateDevice sets currentAssetId.
                // Prefer currentPlaylistId when available (non-null/non-empty), fall back to currentAssetId.
                var effectivePlaylistId = !string.IsNullOrEmpty(node.CurrentPlaylistId)
                    ? node.CurrentPlaylistId
                    : node.CurrentAssetId;



                // Track last known playlist for power-on restore
                if (powerIsOn && !string.IsNullOrEmpty(effectivePlaylistId))
                    lastActivePlaylistId = effectivePlaylistId;

                if (effectivePlaylistId != currentPlaylistId)
                {
                    int newIdx;
                    var newName = ResolvePlaylistInfo(effectivePlaylistId, out newIdx);
                    this.LogDebug("Playlist changed: '{0}' [idx={1}]", newName, newIdx);
                    currentPlaylistId = effectivePlaylistId;
                    currentPlaylistName = newName;
                    currentPlaylistIndex = newIdx;
                    CurrentPlaylistNameFeedback.FireUpdate();
                    AbsoluteInputSelectFeedback.FireUpdate();
                    RelativeInputSelectFeedback.FireUpdate();
                }

                var mappedStatus = MapDeviceStatus(node.Status);
                if (mappedStatus != deviceStatus)
                {
                    this.LogDebug("Status: {0}", node.Status ?? "unknown");
                    deviceStatus = mappedStatus;
                    DeviceStatusFeedback.FireUpdate();
                }

                if (node.LastHeartBeat != lastHeartBeat)
                {
                    lastHeartBeat = node.LastHeartBeat;
                    LastHeartBeatFeedback.FireUpdate();
                }

                // Update device name from API response
                if (!string.IsNullOrEmpty(node.DeviceName) && node.DeviceName != deviceName)
                {
                    this.LogDebug("Name: '{0}'", node.DeviceName);
                    deviceName = node.DeviceName;
                    DeviceNameFeedback.FireUpdate();
                }
            }
            catch (Exception ex)
            {
                this.LogError("Poll failed: {0}", ex.Message);
            }
            finally
            {
                SetPolling(false);
            }
        }

        // ──────────────────────────────────────────────
        // Polling — playlist list
        // ──────────────────────────────────────────────

        private async void PollPlaylistsAsync()
        {
            if (isPlaylistPolling) return;
            isPlaylistPolling = true;
            try
            {
                var apiPlaylists = await client.GetPlaylistsAsync(playerConfig.TeamId, playlistLimit, Key)
                    .ConfigureAwait(false);

                if (apiPlaylists == null)
                {
                    this.LogVerbose("Playlist API null, using {0} cached", playlists.Count);
                    FirePlaylistNameFeedbacks();
                    return;
                }

                if (apiPlaylists.Count == 0)
                {
                    this.LogWarning("Playlist API returned empty");
                    FirePlaylistNameFeedbacks();
                    return;
                }

                // Atomic reference swap — safe on CLR without a lock.
                var changed = playlists.Count != apiPlaylists.Count ||
                    !playlists.Select(p => p.Id).SequenceEqual(apiPlaylists.Select(p => p.Id));
                playlists = apiPlaylists;

                if (changed)
                    this.LogDebug("Playlists updated: {0} items", playlists.Count);

                var newIndex = ResolvePlaylistIndex(currentPlaylistId);
                if (newIndex != currentPlaylistIndex)
                {
                    currentPlaylistIndex = newIndex;
                    AbsoluteInputSelectFeedback.FireUpdate();
                    RelativeInputSelectFeedback.FireUpdate();
                }

                var newName = ResolvePlaylistName(currentPlaylistId);
                if (newName != currentPlaylistName)
                {
                    currentPlaylistName = newName;
                    CurrentPlaylistNameFeedback.FireUpdate();
                }

                FirePlaylistNameFeedbacks();
            }
            catch (Exception ex)
            {
                this.LogError("Playlist poll failed: {0}", ex.Message);
            }
            finally
            {
                isPlaylistPolling = false;
            }
        }

        /// <summary>
        /// Triggers an immediate poll of both device status and playlist list.
        /// </summary>
        public void PollNow()
        {
            this.LogDebug("Poll now");
            CrestronInvoke.BeginInvoke(_ =>
            {
                PollDeviceStatusAsync();
                PollPlaylistsAsync();
            });
        }

        // ──────────────────────────────────────────────
        // Playlist / input selection
        // ──────────────────────────────────────────────

        /// <summary>
        /// Selects a playlist by its absolute 1-based index in the entire playlist list.
        /// Index 1 = first playlist, index 2 = second playlist, etc.
        /// Ignores pagination - always selects from the full list.
        /// </summary>
        public void SelectPlaylistByAbsoluteIndex(ushort index)
        {
            if (playlists == null || playlists.Count == 0)
            {
                this.LogWarning("Select[{0}]: no playlists", index);
                return;
            }

            if (index < 1 || index > playlists.Count)
            {
                this.LogWarning("Select[{0}]: out of range (1-{1})",
                    index, playlists.Count);
                return;
            }

            var playlist = playlists[index - 1];
            this.LogDebug("Select[{0}]: '{1}'", index, playlist.Name);

            SelectPlaylistById(playlist.Id);
        }

        /// <summary>
        /// Selects a playlist by its 1-based index relative to the current page.
        /// When on page 2 (offset 30), entering index 5 selects playlist 35.
        /// </summary>
        public void SelectPlaylistByRelativeIndex(ushort index)
        {
            if (playlists == null || playlists.Count == 0)
            {
                this.LogWarning("Select(rel)[{0}]: no playlists", index);
                return;
            }

            if (index < 1 || index > MaxPlaylistBridgeCount)
            {
                this.LogWarning("Select(rel)[{0}]: out of range (1-{1})",
                    index, MaxPlaylistBridgeCount);
                return;
            }

            // Calculate actual index accounting for current page offset
            var actualIndex = playlistGroupOffset + index - 1;

            if (actualIndex >= playlists.Count)
            {
                this.LogWarning("Select(rel)[{0}]: idx {1} > count {2}",
                    index, actualIndex + 1, playlists.Count);
                return;
            }

            var playlist = playlists[actualIndex];
            this.LogDebug("Select(rel)[{0}] -> [{1}]: '{2}'",
                index, actualIndex + 1, playlist.Name);

            SelectPlaylistById(playlist.Id);
        }

        /// <summary>
        /// Selects a playlist by its OptiSigns MongoDB _id string.
        /// </summary>
        public void SelectPlaylistById(string playlistId)
        {
            if (string.IsNullOrEmpty(playlistId))
            {
                this.LogWarning("SelectById: empty ID");
                return;
            }

            this.LogInformation("Select: {0}", playlistId);

            lastActivePlaylistId = playlistId;
            ApplyOptimisticPlaylistState(playlistId);
            CrestronInvoke.BeginInvoke(_ => SelectPlaylistByIdAsync(playlistId));
        }

        private async void SelectPlaylistByIdAsync(string playlistId)
        {
            SetPolling(true);
            SetPlaylistSelectionBusy(true);
            try
            {
                var contentType = playlists.Find(p => p.Id == playlistId)?.Type;
                bool success;

                if (string.Equals(contentType, "ASSET", StringComparison.OrdinalIgnoreCase))
                {
                    success = await client.AssignContentAsync(
                        playerConfig.DeviceId, playerConfig.TeamId, playlistId, "ASSET", Key)
                        .ConfigureAwait(false);

                    if (!success) this.LogWarning("assignContent(ASSET) failed");
                    else this.LogDebug("assignContent(ASSET) OK");
                }
                else
                {
                    // Default: PLAYLIST via pushToScreens
                    var payload = new PushToScreensInput
                    {
                        DeviceIds = new List<string> { playerConfig.DeviceId },
                        CurrentPlaylistId = playlistId,
                        Type = "NOW"
                    };

                    var playlistName = ResolvePlaylistName(playlistId);
                    success = await client.PushToScreensAsync(playerConfig.TeamId, payload, false, Key, playlistName)
                        .ConfigureAwait(false);

                    if (!success) this.LogWarning("pushToScreens failed");
                    else this.LogDebug("pushToScreens OK");
                }

                ScheduleConfirmationPoll();
            }
            catch (Exception ex)
            {
                this.LogError("Select failed: {0}", ex.Message);
            }
            finally
            {
                SetPolling(false);
                SetPlaylistSelectionBusy(false);
            }
        }

        // ──────────────────────────────────────────────
        // Assign Playlist Asset Methods
        // ──────────────────────────────────────────────

        /// <summary>
        /// Assigns a playlist as an asset using updateDevice mutation.
        /// This sets currentType=PLAYLIST and currentAssetId, which properly
        /// switches the device to playlist mode (instead of asset mode).
        /// </summary>
        public void AssignPlaylistAssetByIndex(int index)
        {
            if (index < 1 || index > playlists.Count)
            {
                this.LogWarning("Assign[{0}]: out of range (1-{1})",
                    index, playlists.Count);
                return;
            }

            var playlist = playlists[index - 1];
            this.LogDebug("Assign[{0}]: '{1}'", index, playlist.Name);
            AssignPlaylistAssetById(playlist.Id);
        }

        /// <summary>
        /// Assigns a playlist as an asset by its relative index on the current page.
        /// </summary>
        public void AssignPlaylistAssetByRelativeIndex(int relativeIndex)
        {
            if (relativeIndex < 1 || relativeIndex > MaxPlaylistBridgeCount)
            {
                this.LogWarning("Assign(rel)[{0}]: out of range (1-{1})",
                    relativeIndex, MaxPlaylistBridgeCount);
                return;
            }

            var absoluteIndex = playlistGroupOffset + relativeIndex;
            AssignPlaylistAssetByIndex(absoluteIndex);
        }

        /// <summary>
        /// Assigns a playlist as an asset by its ID using updateDevice mutation.
        /// </summary>
        public void AssignPlaylistAssetById(string playlistId)
        {
            if (string.IsNullOrEmpty(playlistId))
            {
                this.LogWarning("Assign: empty ID");
                return;
            }

            this.LogInformation("Assign: {0}", playlistId);

            lastActivePlaylistId = playlistId;
            ApplyOptimisticPlaylistState(playlistId);
            CrestronInvoke.BeginInvoke(_ => AssignPlaylistAssetByIdAsync(playlistId));
        }

        private async void AssignPlaylistAssetByIdAsync(string playlistId)
        {
            SetPolling(true);
            SetPlaylistSelectionBusy(true);
            try
            {
                // Use updateDevice mutation with currentType=PLAYLIST and currentAssetId
                // This properly switches the device to playlist mode (not asset mode)
                var success = await client.AssignPlaylistAsync(
                    playerConfig.DeviceId,
                    playerConfig.TeamId,
                    playlistId,
                    Key).ConfigureAwait(false);

                if (!success)
                    this.LogWarning("updateDevice failed");
                else
                    this.LogDebug("updateDevice OK");

                ScheduleConfirmationPoll();
            }
            catch (Exception ex)
            {
                this.LogError("Assign failed: {0}", ex.Message);
            }
            finally
            {
                SetPolling(false);
                SetPlaylistSelectionBusy(false);
            }
        }

        // ──────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────

        private void ApplyOptimisticPlaylistState(string playlistId)
        {
            currentPlaylistId = playlistId;
            int resolvedIndex;
            currentPlaylistName = ResolvePlaylistInfo(playlistId, out resolvedIndex);
            currentPlaylistIndex = resolvedIndex;
            CurrentPlaylistNameFeedback.FireUpdate();
            AbsoluteInputSelectFeedback.FireUpdate();
            RelativeInputSelectFeedback.FireUpdate();
        }

        private void ScheduleConfirmationPoll()
        {
            _confirmationPollTimer?.Dispose();
            _confirmationPollTimer = new CTimer(_ => CrestronInvoke.BeginInvoke(__ => PollDeviceStatusAsync()), 2000);
        }

        private string ResolvePlaylistName(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            for (var i = 0; i < playlists.Count; i++)
            {
                if (playlists[i].Id == id)
                    return playlists[i].Name ?? id;
            }
            return id;
        }

        private int ResolvePlaylistIndex(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            for (var i = 0; i < playlists.Count; i++)
            {
                if (playlists[i].Id == id)
                    return i + 1;
            }
            return 0;
        }

        /// <summary>
        /// Single-pass lookup returning both name and 1-based index for a playlist ID.
        /// Avoids scanning the list twice when both values are needed.
        /// </summary>
        private string ResolvePlaylistInfo(string id, out int index)
        {
            index = 0;
            if (string.IsNullOrEmpty(id)) return string.Empty;
            for (var i = 0; i < playlists.Count; i++)
            {
                if (playlists[i].Id == id)
                {
                    index = i + 1;
                    return playlists[i].Name ?? id;
                }
            }
            return id;
        }

        /// <summary>
        /// Returns the current playlist's index relative to the current page.
        /// Returns 0 if the playlist is not on the current page or unknown.
        /// </summary>
        private int GetPageRelativePlaylistIndex()
        {
            if (currentPlaylistIndex == 0) return 0;

            // Check if current playlist is on this page
            var pageStart = playlistGroupOffset + 1; // 1-based
            var pageEnd = playlistGroupOffset + MaxPlaylistBridgeCount;

            if (currentPlaylistIndex >= pageStart && currentPlaylistIndex <= pageEnd)
                return currentPlaylistIndex - playlistGroupOffset;

            return 0; // Current playlist not on this page
        }

        private static int MapDeviceStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return 0;

            switch (status.ToUpperInvariant())
            {
                case "ONLINE": return 1;
                case "SLEEP": return 2;
                case "OFFLINE": return 3;
                default: return 0;
            }
        }

        private void SetPolling(bool value)
        {
            if (isPolling == value) return;
            isPolling = value;
            IsPollingFeedback.FireUpdate();
        }

        private void SetPlaylistSelectionBusy(bool value)
        {
            if (playlistSelectionBusy == value) return;
            playlistSelectionBusy = value;
            PlaylistSelectionBusyFeedback.FireUpdate();
        }

        /// <summary>
        /// Sets the format for playlist items sent to the bridge.
        /// When true, items are sent as JSON: {"id":"...","name":"..."}.
        /// When false, items are sent as the playlist name only.
        /// </summary>
        public void SetPlaylistItemJsonFormat(bool useJson)
        {
            if (playlistItemAsJsonFormat == useJson) return;

            playlistItemAsJsonFormat = useJson;
            this.LogDebug("Playlist format: {0}", useJson ? "JSON" : "name");
            FirePlaylistNameFeedbacks();
        }

        private void FirePlaylistNameFeedbacks()
        {
            PlaylistCountFeedback.FireUpdate();
            foreach (var fb in PlaylistNameFeedbacks)
                fb.FireUpdate();
        }

        /// <summary>
        /// Navigates to the first page of playlists (offset = 0).
        /// </summary>
        public void FirstPlaylistPage()
        {
            if (playlistGroupOffset == 0)
            {
                this.LogVerbose("Page: at start");
                return;
            }

            playlistGroupOffset = 0;
            this.LogDebug("Page: first");
            FirePlaylistNameFeedbacks();
            RelativeInputSelectFeedback.FireUpdate();
        }

        /// <summary>
        /// Advances to the next page of playlists (offset += MaxPlaylistBridgeCount).
        /// </summary>
        public void NextPlaylistPage()
        {
            if (playlistGroupOffset + MaxPlaylistBridgeCount >= playlists.Count)
            {
                this.LogVerbose("Page: at end");
                return;
            }

            playlistGroupOffset += MaxPlaylistBridgeCount;
            this.LogDebug("Page: next (offset={0})", playlistGroupOffset);
            FirePlaylistNameFeedbacks();
            RelativeInputSelectFeedback.FireUpdate();
        }

        /// <summary>
        /// Goes back to the previous page of playlists (offset -= MaxPlaylistBridgeCount).
        /// </summary>
        public void PreviousPlaylistPage()
        {
            if (playlistGroupOffset <= 0)
            {
                this.LogVerbose("Page: at start");
                return;
            }

            playlistGroupOffset = Math.Max(0, playlistGroupOffset - MaxPlaylistBridgeCount);
            this.LogDebug("Page: prev (offset={0})", playlistGroupOffset);
            FirePlaylistNameFeedbacks();
            RelativeInputSelectFeedback.FireUpdate();
        }

        private void FireAllFeedbacks()
        {
            IsOnlineFeedback.FireUpdate();
            PowerIsOnFeedback.FireUpdate();
            PowerIsOffFeedback.FireUpdate();
            IsPollingFeedback.FireUpdate();
            PlaylistSelectionBusyFeedback.FireUpdate();
            AbsoluteInputSelectFeedback.FireUpdate();
            RelativeInputSelectFeedback.FireUpdate();
            DeviceNameFeedback.FireUpdate();
            CurrentPlaylistNameFeedback.FireUpdate();
            DeviceStatusFeedback.FireUpdate();
            LastHeartBeatFeedback.FireUpdate();
            FirePlaylistNameFeedbacks();
        }

        // ──────────────────────────────────────────────
        // IBridgeAdvanced — LinkToApi
        // ──────────────────────────────────────────────

        public override void LinkToApi(BasicTriList trilist, uint joinStart,
            string joinMapKey, EiscApiAdvanced bridge)
        {
            var joinMap = new OptiSignsBridgeJoinMap(joinStart);

            bridge?.AddJoinMap(Key, joinMap);

            var customJoins = JoinMapHelper.TryGetJoinMapAdvancedForDevice(joinMapKey);
            if (customJoins != null)
                joinMap.SetCustomJoinData(customJoins);

            this.LogDebug("Bridge: IPID=0x{0}", trilist.ID.ToString("X"));

            // ── Digital: ToSIMPL (feedback) ──────────────────────────
            IsOnlineFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsOnline.JoinNumber]);
            IsPollingFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsPolling.JoinNumber]);
            PlaylistSelectionBusyFeedback.LinkInputSig(trilist.BooleanInput[joinMap.PlaylistSelectionBusy.JoinNumber]);

            // ── Digital: FromSIMPL (actions) ─────────────────────────
            // Power control removed - playlist selection uses pushToScreens directly
            trilist.SetSigTrueAction(joinMap.PollNow.JoinNumber, PollNow);
            trilist.SetSigTrueAction(joinMap.PageFirst.JoinNumber, FirstPlaylistPage);
            trilist.SetSigTrueAction(joinMap.PageNext.JoinNumber, NextPlaylistPage);
            trilist.SetSigTrueAction(joinMap.PreviousPage.JoinNumber, PreviousPlaylistPage);
            trilist.SetBoolSigAction(joinMap.PlaylistItemAsJsonObject.JoinNumber, SetPlaylistItemJsonFormat);

            // ── Analog: ToFromSIMPL (pushToScreens) ────────────────────
            AbsoluteInputSelectFeedback.LinkInputSig(trilist.UShortInput[joinMap.SelectPlaylistByAbsoluteIndex.JoinNumber]);
            trilist.SetUShortSigAction(joinMap.SelectPlaylistByAbsoluteIndex.JoinNumber, SelectPlaylistByAbsoluteIndex);

            RelativeInputSelectFeedback.LinkInputSig(trilist.UShortInput[joinMap.SelectPlaylistByRelativeIndex.JoinNumber]);
            trilist.SetUShortSigAction(joinMap.SelectPlaylistByRelativeIndex.JoinNumber, SelectPlaylistByRelativeIndex);

            // ── Analog: FromSIMPL (updateDevice - asset mode) ─────────
            trilist.SetUShortSigAction(joinMap.AssignPlaylistAssetByAbsoluteIndex.JoinNumber,
                index => AssignPlaylistAssetByIndex((int)index));
            trilist.SetUShortSigAction(joinMap.AssignPlaylistAssetByRelativeIndex.JoinNumber,
                index => AssignPlaylistAssetByRelativeIndex((int)index));

            PlaylistCountFeedback.LinkInputSig(trilist.UShortInput[joinMap.PlaylistCount.JoinNumber]);

            // ── Serial: ToSIMPL (feedback) ────────────────────────────
            DeviceNameFeedback.LinkInputSig(trilist.StringInput[joinMap.DeviceName.JoinNumber]);
            CurrentPlaylistNameFeedback.LinkInputSig(
                trilist.StringInput[joinMap.CurrentPlaylistName.JoinNumber]);

            DeviceStatusFeedback.LinkInputSig(
                trilist.UShortInput[joinMap.DeviceStatus.JoinNumber]);
            LastHeartBeatFeedback.LinkInputSig(
                trilist.StringInput[joinMap.LastHeartBeat.JoinNumber]);

            // ── Serial: PlaylistNames span (S11-S40) ──────────────────
            for (uint i = 0; i < MaxPlaylistBridgeCount; i++)
            {
                PlaylistNameFeedbacks[i].LinkInputSig(
                    trilist.StringInput[joinMap.PlaylistNames.JoinNumber + i]);
            }

            // ── Serial: FromSIMPL (pushToScreens) ─────────────────────
            trilist.SetStringSigAction(joinMap.SelectPlaylistById.JoinNumber,
                SelectPlaylistById);

            // ── Serial: FromSIMPL (updateDevice - asset mode) ─────────
            trilist.SetStringSigAction(joinMap.AssignPlaylistAssetById.JoinNumber,
                AssignPlaylistAssetById);

            // ── Re-fire all feedback when the bridge (re)connects ─────
            trilist.OnlineStatusChange += (sender, args) =>
            {
                if (!args.DeviceOnLine) return;

                FireAllFeedbacks();
            };
        }
    }
}
