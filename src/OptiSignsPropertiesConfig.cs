using System.Collections.Generic;
using Newtonsoft.Json;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    /// <summary>
    /// Deserialized from the "properties" JSON object in the Essentials device configuration.
    /// All required fields (ApiKey, TeamId, DeviceId) are validated in the factory before
    /// the device is constructed.
    /// </summary>
    public class OptiSignsPropertiesConfig
    {
        /// <summary>
        /// OptiSigns API key. Used as the Bearer token in every HTTP request.
        /// Generate at https://app.optisigns.com/account-setting.
        /// </summary>
        [JsonProperty("apiKey")]
        public string ApiKey { get; set; }

        /// <summary>
        /// OptiSigns team ID. Passed as $teamId in all mutation variables.
        /// Found in the OptiSigns account / team settings.
        /// </summary>
        [JsonProperty("teamId")]
        public string TeamId { get; set; }

        /// <summary>
        /// The MongoDB _id of the OptiSigns screen (device) to control.
        /// Obtain by calling listAllDevices in the SDK or via the OptiSigns web app.
        /// </summary>
        [JsonProperty("deviceId")]
        public string DeviceId { get; set; }

        /// <summary>
        /// Interval in ms for polling device status (currentType, status, heartbeat).
        /// Default: 30000 (30 seconds).
        /// </summary>
        [JsonProperty("pollIntervalMs")]
        public int PollIntervalMs { get; set; }

        /// <summary>
        /// Interval in ms for refreshing the available playlist list from the API.
        /// Playlists change infrequently; a longer interval reduces API traffic.
        /// Default: 300000 (5 minutes).
        /// </summary>
        [JsonProperty("playlistPollIntervalMs")]
        public int PlaylistPollIntervalMs { get; set; }

        /// <summary>
        /// Playlist _id pushed to the screen when PowerOn() is called and no prior
        /// playlist has been tracked. Falls back to the first item in Playlists if null.
        /// </summary>
        [JsonProperty("defaultPlaylistId")]
        public string DefaultPlaylistId { get; set; }

        /// <summary>
        /// Static fallback playlist list. Used when the API playlists query is unavailable
        /// (the endpoint is Phase 2 and may not yet be live). Also used on startup before
        /// the first playlist poll completes.
        /// If the API poll succeeds, its result overwrites this list at runtime.
        /// </summary>
        [JsonProperty("playlists")]
        public List<PlaylistConfigItem> Playlists { get; set; }

        public OptiSignsPropertiesConfig()
        {
            PollIntervalMs = 30000;
            PlaylistPollIntervalMs = 300000;
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
