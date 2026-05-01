using System;
using System.Collections.Generic;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.DeviceSupport;
using Newtonsoft.Json;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using CrestronIO = Crestron.SimplSharp.CrestronIO;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    /// <summary>
    /// OptiSigns server device that manages a shared GraphQL client and multiple player devices.
    /// 
    /// The server owns the API connection and creates child player devices for each configured
    /// screen. Each player is independently bridgeable and registered with DeviceManager.
    /// 
    /// The server itself is also bridgeable and provides:
    /// - Device discovery: fetch all available devices from the OptiSigns account
    /// - Configured player count
    /// - API connectivity status
    /// 
    /// Configuration structure:
    /// {
    ///   "key": "optisign-server",
    ///   "type": "optiSigns",
    ///   "properties": {
    ///     "apiKey": "...",
    ///     "pollIntervalMs": 30000,
    ///     "playlistPollIntervalMs": 300000,
    ///     "players": [
    ///       { "key": "player-1", "deviceId": "...", "teamId": "...", ... },
    ///       { "key": "player-2", "deviceId": "...", "teamId": "...", ... }
    ///     ]
    ///   }
    /// }
    /// 
    /// Player device keys are formed as: {server-key}-{player-key}
    /// Example: "optisign-server-player-1"
    /// </summary>
    public class OptiSignsServer : EssentialsBridgeableDevice
    {
        private readonly OptiSignsPropertiesConfig _props;
        private readonly OptiSignsGraphQLClient _client;
        private readonly List<OptiSignsPlayer> _players = new List<OptiSignsPlayer>();

        // ──────────────────────────────────────────────
        // Internal state
        // ──────────────────────────────────────────────

        private bool _isOnline;
        private bool _isFetching;
        private List<DeviceNode> _discoveredDevices = new List<DeviceNode>();
        private int _deviceGroupOffset;

        // Maximum number of discovered device serial joins bridged to SIMPL (S11-S40).
        public const int MaxDiscoveredDeviceBridgeCount = 30;

        // ──────────────────────────────────────────────
        // Feedbacks
        // ──────────────────────────────────────────────

        public BoolFeedback IsOnlineFeedback { get; private set; }
        public BoolFeedback IsFetchingFeedback { get; private set; }
        public IntFeedback ConfiguredPlayerCountFeedback { get; private set; }
        public IntFeedback DiscoveredDeviceCountFeedback { get; private set; }
        public StringFeedback[] DiscoveredDeviceFeedbacks { get; private set; }

        /// <summary>
        /// Gets the list of player devices managed by this server.
        /// </summary>
        public IReadOnlyList<OptiSignsPlayer> Players => _players;

        /// <summary>
        /// Gets the list of devices discovered from the OptiSigns API.
        /// </summary>
        public IReadOnlyList<DeviceNode> DiscoveredDevices => _discoveredDevices;

        /// <summary>
        /// Creates a new OptiSigns server instance.
        /// </summary>
        /// <param name="key">Unique device key</param>
        /// <param name="name">Display name</param>
        /// <param name="props">Server configuration including player definitions</param>
        public OptiSignsServer(string key, string name, OptiSignsPropertiesConfig props)
            : base(key, name)
        {
            _props = props;
            _client = new OptiSignsGraphQLClient(key, props.ApiKey);

            IsOnlineFeedback = new BoolFeedback(key + "-IsOnline", () => _isOnline);
            IsFetchingFeedback = new BoolFeedback(key + "-IsFetching", () => _isFetching);
            ConfiguredPlayerCountFeedback = new IntFeedback(
                key + "-ConfiguredPlayerCount", 
                () => _props.Players?.Count ?? 0);
            DiscoveredDeviceCountFeedback = new IntFeedback(
                key + "-DiscoveredDeviceCount",
                () => _discoveredDevices.Count);

            // One feedback per discovered device slot
            DiscoveredDeviceFeedbacks = new StringFeedback[MaxDiscoveredDeviceBridgeCount];
            for (var i = 0; i < MaxDiscoveredDeviceBridgeCount; i++)
            {
                var capturedI = i;
                DiscoveredDeviceFeedbacks[i] = new StringFeedback(
                    string.Format("{0}-DiscoveredDevice-{1}", key, capturedI + 1),
                    () => BuildDiscoveredDeviceJson(capturedI));
            }

            this.LogDebug("Server created: {0} player(s)", 
                props.Players?.Count ?? 0);

            // Create player devices in constructor so they exist before DeviceManager initialization
            CreatePlayers();
        }

        /// <summary>
        /// Creates player devices from configuration and registers them with DeviceManager.
        /// Called from constructor to ensure players are available before initialization phase.
        /// </summary>
        private void CreatePlayers()
        {
            if (_props.Players == null || _props.Players.Count == 0)
            {
                this.LogWarning("No players configured");
                return;
            }

            foreach (var playerConfig in _props.Players)
            {
                if (string.IsNullOrEmpty(playerConfig.Key))
                {
                    this.LogError("Player missing 'key' — skipped");
                    continue;
                }

                if (string.IsNullOrEmpty(playerConfig.DeviceId))
                {
                    this.LogError("Player '{0}' missing 'deviceId' — skipped", playerConfig.Key);
                    continue;
                }

                if (string.IsNullOrEmpty(playerConfig.TeamId))
                {
                    this.LogError("Player '{0}' missing 'teamId' — skipped", playerConfig.Key);
                    continue;
                }

                // Form the player device key: server-key + player-key
                var playerKey = string.Format("{0}-{1}", Key, playerConfig.Key);
                var playerName = !string.IsNullOrEmpty(playerConfig.Name) 
                    ? playerConfig.Name 
                    : playerKey;

                this.LogVerbose("Creating player: {0} ({1})",
                    playerKey, playerConfig.DeviceId);

                var player = new OptiSignsPlayer(
                    playerKey,
                    playerName,
                    playerConfig,
                    _client,
                    _props.PollIntervalMs,
                    _props.PlaylistPollIntervalMs);

                _players.Add(player);

                // Register with DeviceManager so the player can be bridged
                DeviceManager.AddDevice(player);

                this.LogInformation("Player registered: {0}", playerKey);
            }

            this.LogDebug("{0} player(s) created", _players.Count);
        }

        /// <summary>
        /// Initializes the server and all player devices.
        /// Players were created and registered with DeviceManager in the constructor.
        /// </summary>
        public override void Initialize()
        {
            base.Initialize();

            this.LogInformation("Initializing: {0}", Key);

            // Initialize all player devices (they were created and registered in constructor)
            foreach (var player in _players)
            {
                player.Initialize();
            }

            this.LogInformation("Initialized: {0} player(s)", _players.Count);

            // Fetch devices on startup to populate discovered devices list
            CrestronInvoke.BeginInvoke(_ => FetchDevicesAsync());
        }

        // ──────────────────────────────────────────────
        // Device Discovery
        // ──────────────────────────────────────────────

        /// <summary>
        /// Fetches all devices available in the OptiSigns account.
        /// </summary>
        public void FetchDevices()
        {
            CrestronInvoke.BeginInvoke(_ => FetchDevicesAsync());
        }

        private async void FetchDevicesAsync()
        {
            if (_isFetching)
            {
                this.LogVerbose("FetchDevices: busy");
                return;
            }

            _isFetching = true;
            IsFetchingFeedback.FireUpdate();

            try
            {
                this.LogDebug("Fetching devices...");

                var devices = await _client.ListAllDevicesAsync(Key).ConfigureAwait(false);

                if (devices == null)
                {
                    this.LogWarning("FetchDevices: API null");
                    if (_isOnline)
                    {
                        _isOnline = false;
                        IsOnlineFeedback.FireUpdate();
                    }
                    return;
                }

                if (!_isOnline)
                {
                    _isOnline = true;
                    IsOnlineFeedback.FireUpdate();
                }

                _discoveredDevices = devices;

                this.LogInformation("Discovered {0} device(s)", devices.Count);

                foreach (var device in devices)
                {
                    this.LogVerbose(
                        "Device: {0} ({1}) status={2}",
                        device.DeviceName ?? "-",
                        device.Id,
                        device.Status ?? "-");
                }

                DiscoveredDeviceCountFeedback.FireUpdate();
                FireDiscoveredDeviceFeedbacks();
            }
            catch (Exception ex)
            {
                this.LogError("FetchDevices failed: {0}", ex.Message);
            }
            finally
            {
                _isFetching = false;
                IsFetchingFeedback.FireUpdate();
            }
        }

        // ──────────────────────────────────────────────
        // Playlist Export
        // ──────────────────────────────────────────────

        /// <summary>
        /// Exports all playlists from the OptiSigns account to a JSON file.
        /// File is saved as: YYYY-MM-dd-optisigns-playlist-export.json
        /// </summary>
        public void ExportPlaylistsToJson()
        {
            CrestronInvoke.BeginInvoke(_ => ExportPlaylistsToJsonAsync());
        }

        private async void ExportPlaylistsToJsonAsync()
        {
            if (_isFetching)
            {
                this.LogVerbose("ExportPlaylists: busy");
                return;
            }

            _isFetching = true;
            IsFetchingFeedback.FireUpdate();

            try
            {
                this.LogDebug("Exporting playlists...");

                var playlists = await _client.GetPlaylistsAsync(Key).ConfigureAwait(false);

                if (playlists == null)
                {
                    this.LogWarning("ExportPlaylists: API null");
                    return;
                }

                if (playlists.Count == 0)
                {
                    this.LogWarning("ExportPlaylists: empty");
                    return;
                }

                // Build export data with id and name
                var exportData = new List<object>();
                foreach (var playlist in playlists)
                {
                    exportData.Add(new
                    {
                        id = playlist.Id,
                        name = playlist.Name
                    });
                }

                // Format filename: YYYY-MM-dd-optisigns-playlist-export.json
                var dateStr = DateTime.Now.ToString("yyyy-MM-dd");
                var filename = string.Format("{0}-optisigns-playlist-export.json", dateStr);
                var programFolder = string.Format("program{0}", InitialParametersClass.ApplicationNumber);
                var userProgramPath = CrestronIO.Path.Combine("\\user", programFolder);
                var filePath = CrestronIO.Path.Combine(userProgramPath, filename);

                // Serialize with formatting for readability
                var json = JsonConvert.SerializeObject(exportData, Formatting.Indented);

                // Write to file
                using (var writer = new CrestronIO.StreamWriter(filePath, false, Encoding.UTF8))
                {
                    writer.Write(json);
                }

                this.LogInformation("Exported {0} playlists to {1}", playlists.Count, filePath);
            }
            catch (Exception ex)
            {
                this.LogError("ExportPlaylists failed: {0}", ex.Message);
            }
            finally
            {
                _isFetching = false;
                IsFetchingFeedback.FireUpdate();
            }
        }

        // ──────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────

        private string BuildDiscoveredDeviceJson(int index)
        {
            var actualIndex = _deviceGroupOffset + index;
            if (actualIndex < 0 || actualIndex >= _discoveredDevices.Count)
                return string.Empty;

            var device = _discoveredDevices[actualIndex];
            return JsonConvert.SerializeObject(new
            {
                id = device.Id,
                name = device.DeviceName,
                status = device.Status,
                uuid = device.UUID,
                pairingCode = device.PairingCode,
                currentType = device.CurrentType,
                currentPlaylistId = device.CurrentPlaylistId
            });
        }

        private void FireDiscoveredDeviceFeedbacks()
        {
            foreach (var fb in DiscoveredDeviceFeedbacks)
                fb.FireUpdate();
        }

        /// <summary>
        /// Navigates to the first page of discovered devices (offset = 0).
        /// </summary>
        public void FirstDevicePage()
        {
            if (_deviceGroupOffset == 0)
            {
                this.LogVerbose("DevicePage: at start");
                return;
            }

            _deviceGroupOffset = 0;
            this.LogDebug("DevicePage: first");
            FireDiscoveredDeviceFeedbacks();
        }

        /// <summary>
        /// Advances to the next page of discovered devices (offset += MaxDiscoveredDeviceBridgeCount).
        /// </summary>
        public void NextDevicePage()
        {
            if (_deviceGroupOffset + MaxDiscoveredDeviceBridgeCount >= _discoveredDevices.Count)
            {
                this.LogVerbose("DevicePage: at end");
                return;
            }

            _deviceGroupOffset += MaxDiscoveredDeviceBridgeCount;
            this.LogDebug("DevicePage: next (offset={0})", _deviceGroupOffset);
            FireDiscoveredDeviceFeedbacks();
        }

        /// <summary>
        /// Goes back to the previous page of discovered devices (offset -= MaxDiscoveredDeviceBridgeCount).
        /// </summary>
        public void PreviousDevicePage()
        {
            if (_deviceGroupOffset <= 0)
            {
                this.LogVerbose("DevicePage: at start");
                return;
            }

            _deviceGroupOffset = Math.Max(0, _deviceGroupOffset - MaxDiscoveredDeviceBridgeCount);
            this.LogDebug("DevicePage: prev (offset={0})", _deviceGroupOffset);
            FireDiscoveredDeviceFeedbacks();
        }

        private void FireAllFeedbacks()
        {
            IsOnlineFeedback.FireUpdate();
            IsFetchingFeedback.FireUpdate();
            ConfiguredPlayerCountFeedback.FireUpdate();
            DiscoveredDeviceCountFeedback.FireUpdate();
            FireDiscoveredDeviceFeedbacks();
        }

        // ──────────────────────────────────────────────
        // IBridgeAdvanced — LinkToApi
        // ──────────────────────────────────────────────

        public override void LinkToApi(BasicTriList trilist, uint joinStart,
            string joinMapKey, EiscApiAdvanced bridge)
        {
            var joinMap = new OptiSignsServerBridgeJoinMap(joinStart);

            bridge?.AddJoinMap(Key, joinMap);

            var customJoins = JoinMapHelper.TryGetJoinMapAdvancedForDevice(joinMapKey);
            if (customJoins != null)
                joinMap.SetCustomJoinData(customJoins);

            this.LogDebug("Bridge: IPID=0x{0}", trilist.ID.ToString("X"));

            // ── Digital: ToSIMPL (feedback) ──────────────────────────
            IsOnlineFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsOnline.JoinNumber]);
            IsFetchingFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsFetching.JoinNumber]);

            // ── Digital: FromSIMPL (actions) ─────────────────────────
            trilist.SetSigTrueAction(joinMap.FetchDevices.JoinNumber, FetchDevices);
            trilist.SetSigTrueAction(joinMap.PageFirst.JoinNumber, FirstDevicePage);
            trilist.SetSigTrueAction(joinMap.PageNext.JoinNumber, NextDevicePage);
            trilist.SetSigTrueAction(joinMap.PreviousPage.JoinNumber, PreviousDevicePage);
            trilist.SetSigTrueAction(joinMap.ExportPlaylistAsJson.JoinNumber, ExportPlaylistsToJson);

            // ── Analog: ToSIMPL ───────────────────────────────────────
            ConfiguredPlayerCountFeedback.LinkInputSig(
                trilist.UShortInput[joinMap.ConfiguredPlayerCount.JoinNumber]);
            DiscoveredDeviceCountFeedback.LinkInputSig(
                trilist.UShortInput[joinMap.DiscoveredDeviceCount.JoinNumber]);

            // ── Serial: ToSIMPL ───────────────────────────────────────
            trilist.SetString(joinMap.ServerName.JoinNumber, Name);

            // ── Serial: DiscoveredDevices span (S11-S40) ──────────────
            for (uint i = 0; i < MaxDiscoveredDeviceBridgeCount; i++)
            {
                DiscoveredDeviceFeedbacks[i].LinkInputSig(
                    trilist.StringInput[joinMap.DiscoveredDevices.JoinNumber + i]);
            }

            // ── Re-fire all feedback when the bridge (re)connects ─────
            trilist.OnlineStatusChange += (sender, args) =>
            {
                if (!args.DeviceOnLine) return;

                trilist.SetString(joinMap.ServerName.JoinNumber, Name);
                FireAllFeedbacks();
            };
        }
    }
}
