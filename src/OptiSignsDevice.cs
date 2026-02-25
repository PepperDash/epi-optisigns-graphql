using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.DeviceSupport;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    /// <summary>
    /// PepperDash Essentials bridgeable device for OptiSigns digital signage.
    ///
    /// Maps AV-style power and input-select operations to the OptiSigns GraphQL API:
    ///   Power OFF  ->  updateDevice  (currentType: "NONE")       Blanks the screen
    ///   Power ON   ->  pushToScreens (restores last/default playlist, type: "NOW")
    ///   Input N    ->  pushToScreens (currentPlaylistId: id, type: "NOW")
    ///
    /// Two independent CTimer instances run concurrently:
    ///   Status poll   (default 30s):  queries device currentType / status / heartbeat
    ///   Playlist poll (default 5min): queries the full account playlist list for labels
    ///
    /// Control operations apply optimistic UI updates immediately, then schedule a
    /// confirmation poll 2 seconds later to reconcile with the actual API state.
    /// </summary>
    public class OptiSignsDevice : EssentialsBridgeableDevice
    {
        private readonly OptiSignsPropertiesConfig _props;
        private readonly OptiSignsGraphQLClient _client;

        // ──────────────────────────────────────────────
        // Internal state
        // ──────────────────────────────────────────────

        private bool _isOnline;
        private bool _powerIsOn;
        private string _currentType;
        private string _currentPlaylistId;
        private string _currentPlaylistName;
        private int _deviceStatus;
        private string _lastHeartBeat;
        private int _currentPlaylistIndex;   // 1-based; 0 = unknown / none
        private bool _isPolling;

        // Resolved playlist list. Written atomically (reference swap) from the poll thread.
        private List<PlaylistNode> _playlists = new List<PlaylistNode>();

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
        public IntFeedback InputSelectFeedback { get; private set; }
        public IntFeedback PlaylistCountFeedback { get; private set; }
        public StringFeedback DeviceNameFeedback { get; private set; }
        public StringFeedback CurrentPlaylistNameFeedback { get; private set; }
        public StringFeedback PlaylistListFeedback { get; private set; }
        public IntFeedback DeviceStatusFeedback { get; private set; }
        public StringFeedback LastHeartBeatFeedback { get; private set; }
        public StringFeedback[] PlaylistNameFeedbacks { get; private set; }

        // ──────────────────────────────────────────────
        // Constructor
        // ──────────────────────────────────────────────

        public OptiSignsDevice(string key, string name, OptiSignsPropertiesConfig props)
            : base(key, name)
        {
            _props = props;
            _client = new OptiSignsGraphQLClient(Key, props.ApiKey);

            // Seed from static config so labels are available before the first API poll.
            if (_props.Playlists != null && _props.Playlists.Count > 0)
            {
                _playlists = _props.Playlists
                    .Select(p => new PlaylistNode { Id = p.Id, Name = p.Name })
                    .ToList();
            }

            IsOnlineFeedback = new BoolFeedback(key + "-IsOnline", () => _isOnline);
            PowerIsOnFeedback = new BoolFeedback(key + "-PowerIsOn", () => _powerIsOn);
            PowerIsOffFeedback = new BoolFeedback(key + "-PowerIsOff", () => !_powerIsOn);
            IsPollingFeedback = new BoolFeedback(key + "-IsPolling", () => _isPolling);
            InputSelectFeedback = new IntFeedback(key + "-InputSelect", () => _currentPlaylistIndex);
            DeviceNameFeedback = new StringFeedback(key + "-DeviceName", () => Name);
            CurrentPlaylistNameFeedback = new StringFeedback(
                key + "-CurrentPlaylist", () => _currentPlaylistName ?? string.Empty);
            PlaylistListFeedback = new StringFeedback(
                key + "-PlaylistList", BuildPlaylistListString);
            DeviceStatusFeedback = new IntFeedback(
                key + "-DeviceStatus", () => _deviceStatus);
            LastHeartBeatFeedback = new StringFeedback(
                key + "-LastHeartBeat", () => _lastHeartBeat ?? string.Empty);

            PlaylistCountFeedback = new IntFeedback(
                key + "-PlaylistCount",
                () => Math.Min(_playlists.Count, MaxPlaylistBridgeCount));

            // One feedback per bridged slot (0-based index, 0 = playlist 1 on the SIMPL side).
            // Each lambda captures its index at construction time via a local copy (capturedI),
            // avoiding the classic C# loop-closure pitfall.
            PlaylistNameFeedbacks = new StringFeedback[MaxPlaylistBridgeCount];
            for (var i = 0; i < MaxPlaylistBridgeCount; i++)
            {
                var capturedI = i;
                PlaylistNameFeedbacks[i] = new StringFeedback(
                    string.Format("{0}-PlaylistName-{1}", key, capturedI + 1),
                    () => capturedI < _playlists.Count
                        ? (_playlists[capturedI].Name ?? string.Empty)
                        : string.Empty);
            }
        }

        // ──────────────────────────────────────────────
        // Initialization
        // Called by Essentials after the device is constructed and the config is loaded.
        // ──────────────────────────────────────────────

        public override void Initialize()
        {
            base.Initialize();

            this.LogInformation("Initializing OptiSigns device. DeviceId={0}, TeamId={1}",
                _props.DeviceId, _props.TeamId);

            if (string.IsNullOrEmpty(_props.ApiKey) ||
                string.IsNullOrEmpty(_props.TeamId) ||
                string.IsNullOrEmpty(_props.DeviceId))
            {
                this.LogError("Required configuration missing (apiKey / teamId / deviceId). Device will not poll.");
                return;
            }

            // Playlist poll fires immediately (dueTime=0) so labels are ready before the UI appears.
            _playlistPollTimer = new CTimer(
                PlaylistPollTimerCallback, null, 0, _props.PlaylistPollIntervalMs);

            // Status poll starts 2 seconds later to let the playlist poll finish first.
            _statusPollTimer = new CTimer(
                StatusPollTimerCallback, null, 2000, _props.PollIntervalMs);
        }

        // ──────────────────────────────────────────────
        // Timer callbacks
        // These run on a Crestron system timer thread.
        // All async I/O is dispatched via CrestronInvoke.BeginInvoke to avoid
        // blocking the timer thread and to comply with Crestron threading rules.
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
                var node = await _client.GetDeviceStatusAsync(_props.DeviceId)
                    .ConfigureAwait(false);

                if (node == null)
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= MaxFailuresBeforeOffline && _isOnline)
                    {
                        _isOnline = false;
                        IsOnlineFeedback.FireUpdate();
                        this.LogWarning("OptiSigns device offline after {0} consecutive failures",
                            _consecutiveFailures);
                    }
                    return;
                }

                _consecutiveFailures = 0;

                if (!_isOnline)
                {
                    _isOnline = true;
                    IsOnlineFeedback.FireUpdate();
                    this.LogInformation("OptiSigns device is online");
                }

                _currentType = node.CurrentType;

                var newPowerIsOn = !string.IsNullOrEmpty(node.CurrentType) &&
                    !node.CurrentType.Equals("NONE", StringComparison.OrdinalIgnoreCase);

                if (newPowerIsOn != _powerIsOn)
                {
                    _powerIsOn = newPowerIsOn;
                    PowerIsOnFeedback.FireUpdate();
                    PowerIsOffFeedback.FireUpdate();
                }

                // Track last known playlist for power-on restore
                if (_powerIsOn && !string.IsNullOrEmpty(node.CurrentPlaylistId))
                    _lastActivePlaylistId = node.CurrentPlaylistId;

                if (node.CurrentPlaylistId != _currentPlaylistId)
                {
                    _currentPlaylistId = node.CurrentPlaylistId;
                    _currentPlaylistName = ResolvePlaylistName(_currentPlaylistId);
                    _currentPlaylistIndex = ResolvePlaylistIndex(_currentPlaylistId);
                    CurrentPlaylistNameFeedback.FireUpdate();
                    InputSelectFeedback.FireUpdate();
                }

                var mappedStatus = MapDeviceStatus(node.Status);
                if (mappedStatus != _deviceStatus)
                {
                    _deviceStatus = mappedStatus;
                    DeviceStatusFeedback.FireUpdate();
                }

                if (node.LastHeartBeat != _lastHeartBeat)
                {
                    _lastHeartBeat = node.LastHeartBeat;
                    LastHeartBeatFeedback.FireUpdate();
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
                    // Endpoint may not be live yet (Phase 2 in the OptiSigns SDK).
                    // Log once at debug level; keep the existing (config-provided) list.
                    this.LogDebug(
                        "Playlist API returned null — using config-provided list ({0} items)",
                        _playlists.Count);
                    PlaylistListFeedback.FireUpdate();
                    FirePlaylistNameFeedbacks();
                    return;
                }

                if (apiPlaylists.Count == 0)
                {
                    this.LogWarning("Playlist API returned an empty list");
                    PlaylistListFeedback.FireUpdate();
                    FirePlaylistNameFeedbacks();
                    return;
                }

                // Atomic reference swap — safe on CLR without a lock.
                _playlists = apiPlaylists;
                this.LogDebug("Playlist list refreshed: {0} playlists", _playlists.Count);

                var newIndex = ResolvePlaylistIndex(_currentPlaylistId);
                if (newIndex != _currentPlaylistIndex)
                {
                    _currentPlaylistIndex = newIndex;
                    InputSelectFeedback.FireUpdate();
                }

                var newName = ResolvePlaylistName(_currentPlaylistId);
                if (newName != _currentPlaylistName)
                {
                    _currentPlaylistName = newName;
                    CurrentPlaylistNameFeedback.FireUpdate();
                }

                PlaylistListFeedback.FireUpdate();
                FirePlaylistNameFeedbacks();
            }
            catch (Exception ex)
            {
                this.LogError("Exception during playlist poll: {0}", ex.Message);
            }
        }

        // ──────────────────────────────────────────────
        // Power control
        // ──────────────────────────────────────────────

        /// <summary>
        /// Powers on the display by pushing content via pushToScreens.
        /// Uses the last active playlist, falling back to defaultPlaylistId from config,
        /// then the first playlist in the known list.
        /// </summary>
        public void PowerOn()
        {
            var playlistId = _lastActivePlaylistId
                ?? _props.DefaultPlaylistId
                ?? (_playlists.Count > 0 ? _playlists[0].Id : null);

            if (string.IsNullOrEmpty(playlistId))
            {
                this.LogWarning("PowerOn: no playlist ID available. " +
                    "Configure 'defaultPlaylistId' or ensure playlists are loaded.");
                return;
            }

            this.LogInformation("PowerOn: pushing playlist {0}", playlistId);

            ApplyOptimisticPlaylistState(playlistId, powerOn: true);
            CrestronInvoke.BeginInvoke(_ => PowerOnAsync(playlistId));
        }

        private async void PowerOnAsync(string playlistId)
        {
            SetPolling(true);
            try
            {
                var payload = new PushToScreensInput
                {
                    DeviceIds = new List<string> { _props.DeviceId },
                    CurrentPlaylistId = playlistId,
                    Type = "NOW"
                };

                var success = await _client.PushToScreensAsync(_props.TeamId, payload)
                    .ConfigureAwait(false);

                if (!success)
                    this.LogWarning("PowerOn: pushToScreens returned false for device {0}",
                        _props.DeviceId);

                ScheduleConfirmationPoll();
            }
            catch (Exception ex)
            {
                this.LogError("Exception in PowerOnAsync: {0}", ex.Message);
            }
            finally
            {
                SetPolling(false);
            }
        }

        /// <summary>
        /// Powers off the display by setting currentType to NONE via updateDevice.
        /// Saves the current playlist ID so it can be restored when PowerOn is called.
        /// </summary>
        public void PowerOff()
        {
            this.LogInformation("PowerOff: setting currentType=NONE for device {0}", _props.DeviceId);

            if (!string.IsNullOrEmpty(_currentPlaylistId))
                _lastActivePlaylistId = _currentPlaylistId;

            _powerIsOn = false;
            _currentPlaylistName = string.Empty;
            _currentPlaylistIndex = 0;
            PowerIsOnFeedback.FireUpdate();
            PowerIsOffFeedback.FireUpdate();
            CurrentPlaylistNameFeedback.FireUpdate();
            InputSelectFeedback.FireUpdate();

            CrestronInvoke.BeginInvoke(_ => PowerOffAsync());
        }

        private async void PowerOffAsync()
        {
            SetPolling(true);
            try
            {
                var payload = new UpdateDeviceInput { CurrentType = "NONE" };

                var success = await _client.UpdateDeviceAsync(_props.DeviceId, _props.TeamId, payload)
                    .ConfigureAwait(false);

                if (!success)
                    this.LogWarning("PowerOff: updateDevice returned false for device {0}",
                        _props.DeviceId);

                ScheduleConfirmationPoll();
            }
            catch (Exception ex)
            {
                this.LogError("Exception in PowerOffAsync: {0}", ex.Message);
            }
            finally
            {
                SetPolling(false);
            }
        }

        /// <summary>Toggles power state based on current PowerIsOn feedback.</summary>
        public void PowerToggle()
        {
            if (_powerIsOn) PowerOff();
            else PowerOn();
        }

        // ──────────────────────────────────────────────
        // Playlist / input selection
        // ──────────────────────────────────────────────

        /// <summary>
        /// Selects a playlist by its 1-based index in the resolved playlist list.
        /// This is the handler for the InputSelect analog join from SIMPL.
        /// </summary>
        public void SelectPlaylistByIndex(ushort index)
        {
            if (_playlists == null || _playlists.Count == 0)
            {
                this.LogWarning("SelectPlaylistByIndex({0}): no playlists loaded", index);
                return;
            }

            if (index < 1 || index > _playlists.Count)
            {
                this.LogWarning("SelectPlaylistByIndex({0}): out of range (1-{1})",
                    index, _playlists.Count);
                return;
            }

            var playlist = _playlists[index - 1];
            this.LogDebug("SelectPlaylistByIndex({0}): '{1}' (id={2})",
                index, playlist.Name, playlist.Id);

            SelectPlaylistById(playlist.Id);
        }

        /// <summary>
        /// Selects a playlist by its OptiSigns MongoDB _id string.
        /// This is also the handler for the SelectPlaylistById serial join from SIMPL,
        /// allowing config-driven room logic without needing a numeric index.
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
                var payload = new PushToScreensInput
                {
                    DeviceIds = new List<string> { _props.DeviceId },
                    CurrentPlaylistId = playlistId,
                    Type = "NOW"
                };

                var success = await _client.PushToScreensAsync(_props.TeamId, payload)
                    .ConfigureAwait(false);

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

        /// <summary>
        /// Applies an optimistic UI state for a playlist selection before the API responds.
        /// This gives the control panel immediate visual feedback.
        /// </summary>
        private void ApplyOptimisticPlaylistState(string playlistId, bool powerOn)
        {
            if (powerOn != _powerIsOn)
            {
                _powerIsOn = powerOn;
                PowerIsOnFeedback.FireUpdate();
                PowerIsOffFeedback.FireUpdate();
            }

            _currentPlaylistId = playlistId;
            _currentPlaylistName = ResolvePlaylistName(playlistId);
            _currentPlaylistIndex = ResolvePlaylistIndex(playlistId);
            CurrentPlaylistNameFeedback.FireUpdate();
            InputSelectFeedback.FireUpdate();
        }

        /// <summary>
        /// Schedules a status poll 2 seconds after a control command, to confirm the
        /// API accepted the change and reconcile any difference from optimistic state.
        /// </summary>
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

        private string BuildPlaylistListString()
        {
            if (_playlists == null || _playlists.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (var i = 0; i < _playlists.Count; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(_playlists[i].Name ?? string.Empty);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Maps the raw OptiSigns API status string to an analog value.
        /// 0 = StatusUnknown, 1 = IsOk (ONLINE), 2 = InWarning (SLEEP), 3 = InError (OFFLINE).
        /// </summary>
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

        /// <summary>
        /// Fires PlaylistCountFeedback and all 30 PlaylistNameFeedbacks.
        /// Called whenever the _playlists list changes (poll result or startup seed).
        /// </summary>
        private void FirePlaylistNameFeedbacks()
        {
            PlaylistCountFeedback.FireUpdate();
            foreach (var fb in PlaylistNameFeedbacks)
                fb.FireUpdate();
        }

        private void FireAllFeedbacks()
        {
            IsOnlineFeedback.FireUpdate();
            PowerIsOnFeedback.FireUpdate();
            PowerIsOffFeedback.FireUpdate();
            IsPollingFeedback.FireUpdate();
            InputSelectFeedback.FireUpdate();
            DeviceNameFeedback.FireUpdate();
            CurrentPlaylistNameFeedback.FireUpdate();
            PlaylistListFeedback.FireUpdate();
            DeviceStatusFeedback.FireUpdate();
            LastHeartBeatFeedback.FireUpdate();
            FirePlaylistNameFeedbacks();
        }

        // ──────────────────────────────────────────────
        // IBridgeAdvanced — LinkToApi
        // Connects Essentials feedback and control objects to the EISC bridge signals.
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
            PowerIsOnFeedback.LinkInputSig(trilist.BooleanInput[joinMap.PowerIsOn.JoinNumber]);
            PowerIsOffFeedback.LinkInputSig(trilist.BooleanInput[joinMap.PowerIsOff.JoinNumber]);
            IsPollingFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsPolling.JoinNumber]);

            // ── Digital: FromSIMPL (actions) ─────────────────────────
            trilist.SetSigTrueAction(joinMap.PowerOn.JoinNumber, PowerOn);
            trilist.SetSigTrueAction(joinMap.PowerOff.JoinNumber, PowerOff);
            trilist.SetSigTrueAction(joinMap.PowerToggle.JoinNumber, PowerToggle);
            trilist.SetSigTrueAction(joinMap.PollNow.JoinNumber,
                () => CrestronInvoke.BeginInvoke(_ => PollDeviceStatusAsync()));

            // ── Analog: ToFromSIMPL ───────────────────────────────────
            InputSelectFeedback.LinkInputSig(trilist.UShortInput[joinMap.SelectPlaylistByIndex.JoinNumber]);
            trilist.SetUShortSigAction(joinMap.SelectPlaylistByIndex.JoinNumber, SelectPlaylistByIndex);

            PlaylistCountFeedback.LinkInputSig(trilist.UShortInput[joinMap.PlaylistCount.JoinNumber]);

            // ── Serial: ToSIMPL (feedback) ────────────────────────────
            trilist.SetString(joinMap.DeviceName.JoinNumber, Name);
            CurrentPlaylistNameFeedback.LinkInputSig(
                trilist.StringInput[joinMap.CurrentPlaylistName.JoinNumber]);
            PlaylistListFeedback.LinkInputSig(
                trilist.StringInput[joinMap.PlaylistList.JoinNumber]);
            DeviceStatusFeedback.LinkInputSig(
                trilist.UShortInput[joinMap.DeviceStatus.JoinNumber]);
            LastHeartBeatFeedback.LinkInputSig(
                trilist.StringInput[joinMap.LastHeartBeat.JoinNumber]);

            // ── Serial: PlaylistNames span (S11-S40) ──────────────────
            // PlaylistNames.JoinNumber = 11; wires slot 0 → S11, slot 1 → S12 … slot 29 → S40.
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
