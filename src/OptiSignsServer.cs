using System.Collections.Generic;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    /// <summary>
    /// OptiSigns server device that manages a shared GraphQL client and multiple player devices.
    /// 
    /// The server owns the API connection and creates child player devices for each configured
    /// screen. Each player is independently bridgeable and registered with DeviceManager.
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
    public class OptiSignsServer : EssentialsDevice
    {
        private readonly OptiSignsPropertiesConfig _props;
        private readonly OptiSignsGraphQLClient _client;
        private readonly List<OptiSignsPlayer> _players = new List<OptiSignsPlayer>();

        /// <summary>
        /// Gets the list of player devices managed by this server.
        /// </summary>
        public IReadOnlyList<OptiSignsPlayer> Players => _players;

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

            this.LogDebug("OptiSignsServer created with {0} player(s) configured", 
                props.Players?.Count ?? 0);
        }

        /// <summary>
        /// Initializes the server and creates all player devices.
        /// Players are registered with DeviceManager and will be bridgeable.
        /// </summary>
        public override void Initialize()
        {
            base.Initialize();

            this.LogInformation("Initializing OptiSigns server: {0}", Key);

            if (_props.Players == null || _props.Players.Count == 0)
            {
                this.LogWarning("No players configured for OptiSigns server '{0}'", Key);
                return;
            }

            foreach (var playerConfig in _props.Players)
            {
                if (string.IsNullOrEmpty(playerConfig.Key))
                {
                    this.LogError("Player configuration missing 'key' property — skipping");
                    continue;
                }

                if (string.IsNullOrEmpty(playerConfig.DeviceId))
                {
                    this.LogError("Player '{0}' missing 'deviceId' — skipping", playerConfig.Key);
                    continue;
                }

                if (string.IsNullOrEmpty(playerConfig.TeamId))
                {
                    this.LogError("Player '{0}' missing 'teamId' — skipping", playerConfig.Key);
                    continue;
                }

                // Form the player device key: server-key + player-key
                var playerKey = string.Format("{0}-{1}", Key, playerConfig.Key);
                var playerName = !string.IsNullOrEmpty(playerConfig.Name) 
                    ? playerConfig.Name 
                    : playerKey;

                this.LogDebug("Creating player device: Key={0}, Name={1}, DeviceId={2}",
                    playerKey, playerName, playerConfig.DeviceId);

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

                this.LogInformation("Registered OptiSigns player: {0}", playerKey);
            }

            // Initialize all players after registration
            foreach (var player in _players)
            {
                player.Initialize();
            }

            this.LogInformation("OptiSigns server initialized with {0} player(s)", _players.Count);
        }
    }
}
