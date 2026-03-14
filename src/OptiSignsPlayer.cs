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
    /// The player receives its GraphQL client and poll intervals from the parent server.
    /// Each player maintains its own state and is independently bridgeable.
    /// </summary>
    public class OptiSignsPlayer : EssentialsBridgeableDevice
    {
        private readonly OptiSignsPlayerConfig _playerConfig;
        private readonly OptiSignsGraphQLClient _client;
        private readonly int _pollIntervalMs;
        private readonly int _playlistPollIntervalMs;

        // ──────────────────────────────────────────────
        // Internal state
        // ──────────────────────────────────────────────

        private bool _isOnline;
        private bool _powerIsOn;
        private string _currentType;
        private string _currentPlaylistId;
        private string _currentPlaylistName;
        private string _deviceName;
        private int _deviceStatus;
        private string _lastHeartBeat;
        private int _currentPlaylistIndex;   // 1-based; 0 = unknown / none
        private bool _isPolling;

        // Resolved playlist list. Written atomically (reference swap) from the poll thread.
        private List<PlaylistNode> _playlists = new List<PlaylistNode>();
        private int _playlistGroupOffset;

        // Last active playlist ID used to restore content when powered on.
        private string _lastActivePlaylistId;

        // Consecutive API failures used to drive the IsOnline feedback.
        private int _consecutiveFailures;
        private const int MaxFailuresBeforeOffline = 3;

        // Maximum number of playlist name serial joins bridged to SIMPL (S11-S40).
        public const int MaxPlaylistBridgeCount = 30;

        private CTimer _statusPollTimer;
        private CTimer _playlistPollTimer;

        // ──────────────────────────────────────────────
        // Feedbacks
        // ──────────────────────────────────────────────

        public BoolFeedback IsOnlineFeedback { get; private set; }
        public BoolFeedback PowerIsOnFeedback { get; private set; }
        public BoolFeedback PowerIsOffFeedback { get; private set; }
        public BoolFeedback IsPollingFeedback { get; private set; }
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
        public OptiSignsPlayer(
            string key,
            string name,
            OptiSignsPlayerConfig playerConfig,
            OptiSignsGraphQLClient client,
            int pollIntervalMs,
            int playlistPollIntervalMs)
            : base(key, name)
        {
            _playerConfig = playerConfig;
            _client = client;
            _pollIntervalMs = pollIntervalMs;
            _playlistPollIntervalMs = playlistPollIntervalMs;
            // Seed from static config so labels are available before the first API poll.
            if (_playerConfig.Playlists != null && _playerConfig.Playlists.Count > 0)
            {
                _playlists = _playerConfig.Playlists
                    .Select(p => new PlaylistNode { Id = p.Id, Name = p.Name })
                    .ToList();
            }

            IsOnlineFeedback = new BoolFeedback(key + "-IsOnline", () => _isOnline);
            PowerIsOnFeedback = new BoolFeedback(key + "-PowerIsOn", () => _powerIsOn);
            PowerIsOffFeedback = new BoolFeedback(key + "-PowerIsOff", () => !_powerIsOn);
            IsPollingFeedback = new BoolFeedback(key + "-IsPolling", () => _isPolling);
            AbsoluteInputSelectFeedback = new IntFeedback(key + "-AbsoluteInputSelect", () => _currentPlaylistIndex);
            RelativeInputSelectFeedback = new IntFeedback(key + "-RelativeInputSelect", () => GetPageRelativePlaylistIndex());
            DeviceNameFeedback = new StringFeedback(key + "-DeviceName", 
                () => !string.IsNullOrEmpty(_deviceName) 
                    ? _deviceName 
                    : !string.IsNullOrEmpty(_playerConfig.Name) 
                        ? _playerConfig.Name 
                        : Name);
            CurrentPlaylistNameFeedback = new StringFeedback(
                key + "-CurrentPlaylist", () => _currentPlaylistName ?? string.Empty);
            DeviceStatusFeedback = new IntFeedback(
                key + "-DeviceStatus", () => _deviceStatus);
            LastHeartBeatFeedback = new StringFeedback(
                key + "-LastHeartBeat", () => _lastHeartBeat ?? string.Empty);

            PlaylistCountFeedback = new IntFeedback(
                key + "-PlaylistCount",
                () => _playlists.Count);

            // One feedback per bridged slot (0-based index, 0 = playlist 1 on the SIMPL side).
            PlaylistNameFeedbacks = new StringFeedback[MaxPlaylistBridgeCount];
            for (var i = 0; i < MaxPlaylistBridgeCount; i++)
            {
                var capturedI = i;
                PlaylistNameFeedbacks[i] = new StringFeedback(
                    string.Format("{0}-PlaylistName-{1}", key, capturedI + 1),
                    () =>
                    {
                        var actualIndex = _playlistGroupOffset + capturedI;
                        return actualIndex < _playlists.Count
                            ? (_playlists[actualIndex].Name ?? string.Empty)
                            : string.Empty;
                    });
            }
        }

        // ──────────────────────────────────────────────
        // Initialization
        // ──────────────────────────────────────────────

        public override void Initialize()
        {
            base.Initialize();

            this.LogInformation("Initializing OptiSigns player. DeviceId={0}, TeamId={1}",
                _playerConfig.DeviceId, _playerConfig.TeamId);

            this.LogVerbose(
                "[OptiSigns] Player init config: PollIntervalMs={0}, PlaylistPollIntervalMs={1}, DefaultPlaylistId={2}, StaticPlaylists={3}",
                _pollIntervalMs,
                _playlistPollIntervalMs,
                _playerConfig.DefaultPlaylistId ?? "(none)",
                _playlists.Count);

            if (string.IsNullOrEmpty(_playerConfig.TeamId) ||
                string.IsNullOrEmpty(_playerConfig.DeviceId))
            {
                this.LogError("Required configuration missing (teamId / deviceId). Player will not poll.");
                return;
            }

            // Playlist poll fires immediately (dueTime=0) so labels are ready before the UI appears.
            _playlistPollTimer = new CTimer(
                PlaylistPollTimerCallback, null, 0, _playlistPollIntervalMs);

            // Status poll starts 2 seconds later to let the playlist poll finish first.
            _statusPollTimer = new CTimer(
                StatusPollTimerCallback, null, 2000, _pollIntervalMs);
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
            SetPolling(true);
            try
            {
                var node = await _client.GetDeviceStatusAsync(_playerConfig.DeviceId)
                    .ConfigureAwait(false);

                if (node == null)
                {
                    _consecutiveFailures++;
                    this.LogVerbose(
                        "[OptiSigns] Status poll returned null. ConsecutiveFailures={0}, MaxBeforeOffline={1}",
                        _consecutiveFailures, MaxFailuresBeforeOffline);
                    if (_consecutiveFailures >= MaxFailuresBeforeOffline && _isOnline)
                    {
                        _isOnline = false;
                        IsOnlineFeedback.FireUpdate();
                        this.LogWarning("OptiSigns player offline after {0} consecutive failures",
                            _consecutiveFailures);
                    }
                    return;
                }

                _consecutiveFailures = 0;

                this.LogVerbose(
                    "[OptiSigns] Status poll result: CurrentType={0}, Status={1}, CurrentPlaylistId={2}, CurrentAssetId={3}, LastHeartBeat={4}, DeviceName={5}",
                    node.CurrentType ?? "(null)",
                    node.Status ?? "(null)",
                    node.CurrentPlaylistId ?? "(null)",
                    node.CurrentAssetId ?? "(null)",
                    node.LastHeartBeat ?? "(null)",
                    node.DeviceName ?? "(null)");

                if (!_isOnline)
                {
                    _isOnline = true;
                    IsOnlineFeedback.FireUpdate();
                    this.LogInformation("OptiSigns player is online");
                }

                _currentType = node.CurrentType;

                var newPowerIsOn = !string.IsNullOrEmpty(node.CurrentType) &&
                    !node.CurrentType.Equals("NONE", StringComparison.OrdinalIgnoreCase);

                if (newPowerIsOn != _powerIsOn)
                {
                    this.LogVerbose(
                        "[OptiSigns] Power state changed: {0} -> {1} (currentType={2})",
                        _powerIsOn, newPowerIsOn, node.CurrentType);
                    _powerIsOn = newPowerIsOn;
                    PowerIsOnFeedback.FireUpdate();
                    PowerIsOffFeedback.FireUpdate();
                }

                // Per API docs: currentAssetId is used for PLAYLIST/ASSET content (set via updateDevice).
                // currentPlaylistId may be a legacy field. Use currentAssetId when currentType is PLAYLIST.
                var isPlaylistType = string.Equals(node.CurrentType, "PLAYLIST", StringComparison.OrdinalIgnoreCase);
                var effectivePlaylistId = isPlaylistType
                    ? node.CurrentAssetId
                    : node.CurrentPlaylistId;

                this.LogVerbose(
                    "[OptiSigns] Effective playlist calculation: isPlaylistType={0}, using {1}, effectiveId={2}",
                    isPlaylistType,
                    isPlaylistType ? "CurrentAssetId" : "CurrentPlaylistId",
                    effectivePlaylistId ?? "(null)");

                // Track last known playlist for power-on restore
                if (_powerIsOn && !string.IsNullOrEmpty(effectivePlaylistId))
                    _lastActivePlaylistId = effectivePlaylistId;

                if (effectivePlaylistId != _currentPlaylistId)
                {
                    this.LogVerbose(
                        "[OptiSigns] Playlist changed: Id={0} -> {1}, Name={2}, Index={3}",
                        _currentPlaylistId ?? "(null)",
                        effectivePlaylistId ?? "(null)",
                        ResolvePlaylistName(effectivePlaylistId),
                        ResolvePlaylistIndex(effectivePlaylistId));
                    _currentPlaylistId = effectivePlaylistId;
                    _currentPlaylistName = ResolvePlaylistName(_currentPlaylistId);
                    _currentPlaylistIndex = ResolvePlaylistIndex(_currentPlaylistId);
                    CurrentPlaylistNameFeedback.FireUpdate();
                    AbsoluteInputSelectFeedback.FireUpdate();
                    RelativeInputSelectFeedback.FireUpdate();
                }

                var mappedStatus = MapDeviceStatus(node.Status);
                if (mappedStatus != _deviceStatus)
                {
                    this.LogVerbose(
                        "[OptiSigns] Device status changed: {0} -> {1} (raw={2})",
                        _deviceStatus, mappedStatus, node.Status ?? "(null)");
                    _deviceStatus = mappedStatus;
                    DeviceStatusFeedback.FireUpdate();
                }

                if (node.LastHeartBeat != _lastHeartBeat)
                {
                    _lastHeartBeat = node.LastHeartBeat;
                    LastHeartBeatFeedback.FireUpdate();
                }

                // Update device name from API response
                if (!string.IsNullOrEmpty(node.DeviceName) && node.DeviceName != _deviceName)
                {
                    this.LogVerbose("[OptiSigns] Device name updated: {0}", node.DeviceName);
                    _deviceName = node.DeviceName;
                    DeviceNameFeedback.FireUpdate();
                }
            }
            catch (Exception ex)
            {
                this.LogError("Exception during device status poll: {0}", ex.Message);
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
            try
            {
                var apiPlaylists = await _client.GetPlaylistsAsync().ConfigureAwait(false);

                if (apiPlaylists == null)
                {
                    this.LogVerbose(
                        "[OptiSigns] Playlist poll returned null. Keeping {0} existing playlists from config.",
                        _playlists.Count);
                    this.LogDebug(
                        "Playlist API returned null — using config-provided list ({0} items)",
                        _playlists.Count);
                    FirePlaylistNameFeedbacks();
                    return;
                }

                if (apiPlaylists.Count == 0)
                {
                    this.LogWarning("Playlist API returned an empty list");
                    FirePlaylistNameFeedbacks();
                    return;
                }

                // Atomic reference swap — safe on CLR without a lock.
                _playlists = apiPlaylists;
                this.LogDebug("Playlist list refreshed: {0} playlists", _playlists.Count);

                this.LogVerbose(
                    "[OptiSigns] Playlist poll result ({0} playlists):\n{1}",
                    _playlists.Count,
                    JsonConvert.SerializeObject(_playlists, Formatting.Indented));

                var newIndex = ResolvePlaylistIndex(_currentPlaylistId);
                if (newIndex != _currentPlaylistIndex)
                {
                    _currentPlaylistIndex = newIndex;
                    AbsoluteInputSelectFeedback.FireUpdate();
                    RelativeInputSelectFeedback.FireUpdate();
                }

                var newName = ResolvePlaylistName(_currentPlaylistId);
                if (newName != _currentPlaylistName)
                {
                    _currentPlaylistName = newName;
                    CurrentPlaylistNameFeedback.FireUpdate();
                }

                FirePlaylistNameFeedbacks();
            }
            catch (Exception ex)
            {
                this.LogError("Exception during playlist poll: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Triggers an immediate poll of both device status and playlist list.
        /// </summary>
        public void PollNow()
        {
            this.LogDebug("PollNow: triggering device status and playlist polls");
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
            if (_playlists == null || _playlists.Count == 0)
            {
                this.LogWarning("SelectPlaylistByAbsoluteIndex({0}): no playlists loaded", index);
                return;
            }

            if (index < 1 || index > _playlists.Count)
            {
                this.LogWarning("SelectPlaylistByAbsoluteIndex({0}): out of range (1-{1})",
                    index, _playlists.Count);
                return;
            }

            var playlist = _playlists[index - 1];
            this.LogDebug("SelectPlaylistByAbsoluteIndex({0}): '{1}' (id={2})",
                index, playlist.Name, playlist.Id);

            SelectPlaylistById(playlist.Id);
        }

        /// <summary>
        /// Selects a playlist by its 1-based index relative to the current page.
        /// When on page 2 (offset 30), entering index 5 selects playlist 35.
        /// </summary>
        public void SelectPlaylistByRelativeIndex(ushort index)
        {
            if (_playlists == null || _playlists.Count == 0)
            {
                this.LogWarning("SelectPlaylistByRelativeIndex({0}): no playlists loaded", index);
                return;
            }

            if (index < 1 || index > MaxPlaylistBridgeCount)
            {
                this.LogWarning("SelectPlaylistByRelativeIndex({0}): out of range (1-{1})",
                    index, MaxPlaylistBridgeCount);
                return;
            }

            // Calculate actual index accounting for current page offset
            var actualIndex = _playlistGroupOffset + index - 1;

            if (actualIndex >= _playlists.Count)
            {
                this.LogWarning("SelectPlaylistByRelativeIndex({0}): actual index {1} exceeds playlist count {2}",
                    index, actualIndex + 1, _playlists.Count);
                return;
            }

            var playlist = _playlists[actualIndex];
            this.LogDebug("SelectPlaylistByRelativeIndex({0}): page offset={1}, actual index={2}, '{3}' (id={4})",
                index, _playlistGroupOffset, actualIndex + 1, playlist.Name, playlist.Id);

            SelectPlaylistById(playlist.Id);
        }

        /// <summary>
        /// Selects a playlist by its OptiSigns MongoDB _id string.
        /// </summary>
        public void SelectPlaylistById(string playlistId)
        {
            if (string.IsNullOrEmpty(playlistId))
            {
                this.LogWarning("SelectPlaylistById: empty ID — ignoring");
                return;
            }

            this.LogInformation("SelectPlaylistById: {0}", playlistId);

            _lastActivePlaylistId = playlistId;
            ApplyOptimisticPlaylistState(playlistId, powerOn: true);
            CrestronInvoke.BeginInvoke(_ => SelectPlaylistByIdAsync(playlistId));
        }

        private async void SelectPlaylistByIdAsync(string playlistId)
        {
            SetPolling(true);
            try
            {
                // Use pushToScreens mutation with type "NOW" to immediately display playlist
                var payload = new PushToScreensInput
                {
                    DeviceIds = new List<string> { _playerConfig.DeviceId },
                    CurrentPlaylistId = playlistId,
                    Type = "NOW"
                };

                this.LogVerbose(
                    "[OptiSigns] SelectPlaylistByIdAsync (pushToScreens): teamId={0}, playlistId={1}, payload={2}",
                    _playerConfig.TeamId,
                    playlistId,
                    JsonConvert.SerializeObject(payload));

                var success = await _client.PushToScreensAsync(_playerConfig.TeamId, payload)
                    .ConfigureAwait(false);

                this.LogVerbose("[OptiSigns] SelectPlaylistByIdAsync result: success={0}", success);

                if (!success)
                    this.LogWarning("SelectPlaylistById: pushToScreens returned false");

                ScheduleConfirmationPoll();
            }
            catch (Exception ex)
            {
                this.LogError("Exception in SelectPlaylistByIdAsync: {0}", ex.Message);
            }
            finally
            {
                SetPolling(false);
            }
        }

        // ──────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────

        private void ApplyOptimisticPlaylistState(string playlistId, bool powerOn)
        {
            // if (powerOn != _powerIsOn)
            // {
            //     _powerIsOn = powerOn;
            //     PowerIsOnFeedback.FireUpdate();
            //     PowerIsOffFeedback.FireUpdate();
            // }

            _currentPlaylistId = playlistId;
            _currentPlaylistName = ResolvePlaylistName(playlistId);
            _currentPlaylistIndex = ResolvePlaylistIndex(playlistId);
            CurrentPlaylistNameFeedback.FireUpdate();
            AbsoluteInputSelectFeedback.FireUpdate();
            RelativeInputSelectFeedback.FireUpdate();
        }

        private void ScheduleConfirmationPoll()
        {
            new CTimer(_ => CrestronInvoke.BeginInvoke(__ => PollDeviceStatusAsync()), 2000);
        }

        private string ResolvePlaylistName(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            var match = _playlists.FirstOrDefault(p => p.Id == id);
            return match != null ? match.Name ?? id : id;
        }

        private int ResolvePlaylistIndex(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            for (var i = 0; i < _playlists.Count; i++)
            {
                if (_playlists[i].Id == id)
                    return i + 1;
            }
            return 0;
        }

        /// <summary>
        /// Returns the current playlist's index relative to the current page.
        /// Returns 0 if the playlist is not on the current page or unknown.
        /// </summary>
        private int GetPageRelativePlaylistIndex()
        {
            if (_currentPlaylistIndex == 0) return 0;

            // Check if current playlist is on this page
            var pageStart = _playlistGroupOffset + 1; // 1-based
            var pageEnd = _playlistGroupOffset + MaxPlaylistBridgeCount;

            if (_currentPlaylistIndex >= pageStart && _currentPlaylistIndex <= pageEnd)
                return _currentPlaylistIndex - _playlistGroupOffset;

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
            if (_isPolling == value) return;
            _isPolling = value;
            IsPollingFeedback.FireUpdate();
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
            if (_playlistGroupOffset == 0)
            {
                this.LogDebug("FirstPlaylistPage: already at beginning");
                return;
            }

            _playlistGroupOffset = 0;
            this.LogDebug("FirstPlaylistPage: offset now 0");
            FirePlaylistNameFeedbacks();
            RelativeInputSelectFeedback.FireUpdate();
        }

        /// <summary>
        /// Advances to the next page of playlists (offset += MaxPlaylistBridgeCount).
        /// </summary>
        public void NextPlaylistPage()
        {
            if (_playlistGroupOffset + MaxPlaylistBridgeCount >= _playlists.Count)
            {
                this.LogDebug("NextPlaylistPage: already at end");
                return;
            }

            _playlistGroupOffset += MaxPlaylistBridgeCount;
            this.LogDebug("NextPlaylistPage: offset now {0}", _playlistGroupOffset);
            FirePlaylistNameFeedbacks();
            RelativeInputSelectFeedback.FireUpdate();
        }

        /// <summary>
        /// Goes back to the previous page of playlists (offset -= MaxPlaylistBridgeCount).
        /// </summary>
        public void PreviousPlaylistPage()
        {
            if (_playlistGroupOffset <= 0)
            {
                this.LogDebug("PreviousPlaylistPage: already at beginning");
                return;
            }

            _playlistGroupOffset = Math.Max(0, _playlistGroupOffset - MaxPlaylistBridgeCount);
            this.LogDebug("PreviousPlaylistPage: offset now {0}", _playlistGroupOffset);
            FirePlaylistNameFeedbacks();
            RelativeInputSelectFeedback.FireUpdate();
        }

        private void FireAllFeedbacks()
        {
            IsOnlineFeedback.FireUpdate();
            PowerIsOnFeedback.FireUpdate();
            PowerIsOffFeedback.FireUpdate();
            IsPollingFeedback.FireUpdate();
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

            this.LogDebug("Linking to Trilist {0}", trilist.ID.ToString("X"));
            this.LogInformation("Linking to Bridge Type {0}", GetType().Name);

            // ── Digital: ToSIMPL (feedback) ──────────────────────────
            IsOnlineFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsOnline.JoinNumber]);
            IsPollingFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsPolling.JoinNumber]);

            // ── Digital: FromSIMPL (actions) ─────────────────────────
            // Power control removed - playlist selection uses pushToScreens directly
            trilist.SetSigTrueAction(joinMap.PollNow.JoinNumber, PollNow);
            trilist.SetSigTrueAction(joinMap.PageFirst.JoinNumber, FirstPlaylistPage);
            trilist.SetSigTrueAction(joinMap.PageNext.JoinNumber, NextPlaylistPage);
            trilist.SetSigTrueAction(joinMap.PreviousPage.JoinNumber, PreviousPlaylistPage);

            // ── Analog: ToFromSIMPL ───────────────────────────────────
            AbsoluteInputSelectFeedback.LinkInputSig(trilist.UShortInput[joinMap.SelectPlaylistByAbsoluteIndex.JoinNumber]);
            trilist.SetUShortSigAction(joinMap.SelectPlaylistByAbsoluteIndex.JoinNumber, SelectPlaylistByAbsoluteIndex);

            RelativeInputSelectFeedback.LinkInputSig(trilist.UShortInput[joinMap.SelectPlaylistByRelativeIndex.JoinNumber]);
            trilist.SetUShortSigAction(joinMap.SelectPlaylistByRelativeIndex.JoinNumber, SelectPlaylistByRelativeIndex);

            PlaylistCountFeedback.LinkInputSig(trilist.UShortInput[joinMap.PlaylistCount.JoinNumber]);

            // ── Serial: ToSIMPL (feedback) ────────────────────────────
            trilist.SetString(joinMap.DeviceName.JoinNumber, Name);
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

            // ── Serial: FromSIMPL (action) ────────────────────────────
            trilist.SetStringSigAction(joinMap.SelectPlaylistById.JoinNumber,
                SelectPlaylistById);

            // ── Re-fire all feedback when the bridge (re)connects ─────
            trilist.OnlineStatusChange += (sender, args) =>
            {
                if (!args.DeviceOnLine) return;

                trilist.SetString(joinMap.DeviceName.JoinNumber, Name);
                FireAllFeedbacks();
            };
        }
    }
}
