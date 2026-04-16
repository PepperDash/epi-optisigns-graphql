using System.Collections.Generic;
using Newtonsoft.Json;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    /// <summary>
    /// Deserialized from the "properties" JSON object in the Essentials device configuration.
    /// Represents a server that manages multiple OptiSigns players through a shared API connection.
    /// </summary>
    public class OptiSignsPropertiesConfig
    {
        /// <summary>
        /// OptiSigns API key. Used as the Bearer token in every HTTP request.
        /// Generate at https://app.optisigns.com/account-setting.
        /// Shared across all players managed by this server.
        /// </summary>
        [JsonProperty("apiKey")]
        public string ApiKey { get; set; }

        /// <summary>
        /// Interval in ms for polling device status (currentType, status, heartbeat).
        /// Default: 30000 (30 seconds). Applied to all players.
        /// </summary>
        [JsonProperty("pollIntervalMs")]
        public int PollIntervalMs { get; set; }

        /// <summary>
        /// Interval in ms for refreshing the available playlist list from the API.
        /// Playlists change infrequently; a longer interval reduces API traffic.
        /// Default: 300000 (5 minutes). Applied to all players.
        /// </summary>
        [JsonProperty("playlistPollIntervalMs")]
        public int PlaylistPollIntervalMs { get; set; }

        /// <summary>
        /// Maximum number of playlists to fetch from the API per request.
        /// The API default is 50; increase this if you have more playlists.
        /// Default: 100.
        /// </summary>
        [JsonProperty("playlistLimit")]
        public int PlaylistLimit { get; set; }

        /// <summary>
        /// List of OptiSigns players (screens) managed by this server.
        /// Each player has its own deviceId, teamId, and playlist configuration.
        /// </summary>
        [JsonProperty("players")]
        public List<OptiSignsPlayerConfig> Players { get; set; }

        public OptiSignsPropertiesConfig()
        {
            PollIntervalMs = 30000;
            PlaylistPollIntervalMs = 300000;
            PlaylistLimit = 100;
            Players = new List<OptiSignsPlayerConfig>();
        }
    }

    /// <summary>
    /// Configuration for an individual OptiSigns player (screen).
    /// </summary>
    public class OptiSignsPlayerConfig
    {
        /// <summary>
        /// Unique key for this player. Combined with server key to form the device key.
        /// Example: server "optisign-server" + player "player-1" = "optisign-server-player-1"
        /// </summary>
        [JsonProperty("key")]
        public string Key { get; set; }

        /// <summary>
        /// Display name for this player.
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// The MongoDB _id of the OptiSigns screen (device) to control.
        /// Obtain by calling listAllDevices in the SDK or via the OptiSigns web app.
        /// </summary>
        [JsonProperty("deviceId")]
        public string DeviceId { get; set; }

        /// <summary>
        /// OptiSigns team ID for this player. Passed as $teamId in all mutation variables.
        /// Found in the OptiSigns account / team settings.
        /// </summary>
        [JsonProperty("teamId")]
        public string TeamId { get; set; }

        /// <summary>
        /// Playlist _id pushed to the screen when PowerOn() is called and no prior
        /// playlist has been tracked. Falls back to the first item in Playlists if null.
        /// </summary>
        [JsonProperty("defaultPlaylistId")]
        public string DefaultPlaylistId { get; set; }

        /// <summary>
        /// Static fallback playlist list for this player. Used when the API playlists 
        /// query is unavailable. Also used on startup before the first playlist poll completes.
        /// </summary>
        [JsonProperty("playlists")]
        public List<PlaylistConfigItem> Playlists { get; set; }

        public OptiSignsPlayerConfig()
        {
            Playlists = new List<PlaylistConfigItem>();
        }
    }

    /// <summary>
    /// A statically configured playlist entry used as a fallback when the API
    /// playlists query is not available.
    /// </summary>
    public class PlaylistConfigItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }
}
