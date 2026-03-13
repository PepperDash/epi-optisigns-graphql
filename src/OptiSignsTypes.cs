using System.Collections.Generic;
using Newtonsoft.Json;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    // ──────────────────────────────────────────────
    // Generic GraphQL request / response envelope
    // ──────────────────────────────────────────────

    /// <summary>
    /// Body sent as application/json for every GraphQL request.
    /// Variables may be null for queries that take no parameters.
    /// </summary>
    internal class GraphQlRequest
    {
        [JsonProperty("query")]
        public string Query { get; set; }

        [JsonProperty("variables", NullValueHandling = NullValueHandling.Ignore)]
        public object Variables { get; set; }
    }

    /// <summary>
    /// Top-level GraphQL response wrapper.
    /// T is the shape of the "data" object, unique to each operation.
    /// </summary>
    internal class GraphQlResponse<T>
    {
        [JsonProperty("data")]
        public T Data { get; set; }

        [JsonProperty("errors")]
        public List<GraphQlError> Errors { get; set; }
    }

    internal class GraphQlError
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("extensions")]
        public GraphQlErrorExtensions Extensions { get; set; }

        [JsonProperty("path")]
        public List<string> Path { get; set; }
    }

    internal class GraphQlErrorExtensions
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("exception")]
        public GraphQlExceptionInfo Exception { get; set; }
    }

    internal class GraphQlExceptionInfo
    {
        [JsonProperty("stacktrace")]
        public List<string> Stacktrace { get; set; }
    }

    // ──────────────────────────────────────────────
    // Device query
    // query GetDevice($id: String!) {
    //   devices(query: { _id: $id }) { page { edges { node { ... } } } }
    // }
    // ──────────────────────────────────────────────

    internal class DevicesQueryData
    {
        [JsonProperty("devices")]
        public DevicesConnection Devices { get; set; }
    }

    internal class DevicesConnection
    {
        [JsonProperty("page")]
        public DevicesPage Page { get; set; }
    }

    internal class DevicesPage
    {
        [JsonProperty("edges")]
        public List<DeviceEdge> Edges { get; set; }
    }

    internal class DeviceEdge
    {
        [JsonProperty("node")]
        public DeviceNode Node { get; set; }
    }

    /// <summary>
    /// Fields returned by the devices query for the configured screen.
    /// currentType values: "NOW" | "SCHEDULE" | "TEMPORARILY" | "NONE"
    /// status values:      "ONLINE" | "OFFLINE" | "SLEEP"
    /// </summary>
    public class DeviceNode
    {
        [JsonProperty("_id")]
        public string Id { get; set; }

        [JsonProperty("deviceName")]
        public string DeviceName { get; set; }

        [JsonProperty("UUID")]
        public string UUID { get; set; }

        [JsonProperty("pairingCode")]
        public string PairingCode { get; set; }

        [JsonProperty("localAppVersion")]
        public string LocalAppVersion { get; set; }

        [JsonProperty("currentType")]
        public string CurrentType { get; set; }

        [JsonProperty("currentPlaylistId")]
        public string CurrentPlaylistId { get; set; }

        [JsonProperty("currentAssetId")]
        public string CurrentAssetId { get; set; }

        [JsonProperty("currentScheduleId")]
        public string CurrentScheduleId { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("lastHeartBeat")]
        public string LastHeartBeat { get; set; }
    }

    // ──────────────────────────────────────────────
    // Playlists query (unverified endpoint — graceful null on failure)
    // query {
    //   playlists(query: {}) { page { edges { node { ... } } } }
    // }
    // ──────────────────────────────────────────────

    internal class PlaylistsQueryData
    {
        [JsonProperty("playlists")]
        public PlaylistsConnection Playlists { get; set; }
    }

    internal class PlaylistsConnection
    {
        [JsonProperty("page")]
        public PlaylistsPage Page { get; set; }
    }

    internal class PlaylistsPage
    {
        [JsonProperty("edges")]
        public List<PlaylistEdge> Edges { get; set; }
    }

    internal class PlaylistEdge
    {
        [JsonProperty("node")]
        public PlaylistNode Node { get; set; }
    }

    public class PlaylistNode
    {
        [JsonProperty("_id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("totalDuration")]
        public long? TotalDuration { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; }
    }

    // ──────────────────────────────────────────────
    // updateDevice mutation  (power off: currentType = "NONE")
    // mutation UpdateDevice($id: String!, $payload: UpdateDeviceInput!, $teamId: String!)
    // ──────────────────────────────────────────────

    internal class UpdateDeviceMutationData
    {
        [JsonProperty("updateDevice")]
        public UpdateDeviceResult UpdateDevice { get; set; }
    }

    internal class UpdateDeviceResult
    {
        [JsonProperty("_id")]
        public string Id { get; set; }

        [JsonProperty("currentType")]
        public string CurrentType { get; set; }

        [JsonProperty("currentPlaylistId")]
        public string CurrentPlaylistId { get; set; }

        [JsonProperty("currentAssetId")]
        public string CurrentAssetId { get; set; }
    }

    /// <summary>
    /// Payload for the updateDevice mutation.
    /// Only non-null fields are serialized (NullValueHandling.Ignore).
    /// To power off: set CurrentType = "NONE".
    /// </summary>
    public class UpdateDeviceInput
    {
        [JsonProperty("currentType", NullValueHandling = NullValueHandling.Ignore)]
        public string CurrentType { get; set; }

        [JsonProperty("currentPlaylistId", NullValueHandling = NullValueHandling.Ignore)]
        public string CurrentPlaylistId { get; set; }

        [JsonProperty("currentAssetId", NullValueHandling = NullValueHandling.Ignore)]
        public string CurrentAssetId { get; set; }

        [JsonProperty("currentScheduleId", NullValueHandling = NullValueHandling.Ignore)]
        public string CurrentScheduleId { get; set; }
    }

    internal class UpdateDeviceVariables
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("teamId")]
        public string TeamId { get; set; }

        [JsonProperty("payload")]
        public UpdateDeviceInput Payload { get; set; }
    }

    // ──────────────────────────────────────────────
    // pushToScreens mutation  (power on / playlist select)
    // mutation PushToScreens($force: Boolean, $payload: PushToScreensInput!, $teamId: String!)
    // NOTE: Phase 2 in the official SDK, but the mutation and types are fully defined
    //       in the GraphQL schema. We call it directly via raw HTTP/GraphQL.
    // ──────────────────────────────────────────────

    internal class PushToScreensMutationData
    {
        // The mutation returns an object with a status boolean
        [JsonProperty("pushToScreens")]
        public PushToScreensResult PushToScreens { get; set; }
    }

    internal class PushToScreensResult
    {
        [JsonProperty("status")]
        public bool? Status { get; set; }
    }

    /// <summary>
    /// Input for the pushToScreens mutation.
    /// type: "NOW" | "SCHEDULE" | "TEMPORARILY"
    /// </summary>
    public class PushToScreensInput
    {
        [JsonProperty("deviceIds")]
        public List<string> DeviceIds { get; set; }

        [JsonProperty("currentPlaylistId", NullValueHandling = NullValueHandling.Ignore)]
        public string CurrentPlaylistId { get; set; }

        [JsonProperty("currentAssetId", NullValueHandling = NullValueHandling.Ignore)]
        public string CurrentAssetId { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    internal class PushToScreensVariables
    {
        [JsonProperty("teamId")]
        public string TeamId { get; set; }

        [JsonProperty("payload")]
        public PushToScreensInput Payload { get; set; }

        [JsonProperty("force", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Force { get; set; }
    }
}
