using PepperDash.Essentials.Core;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    /// <summary>
    /// Bridge join map for the OptiSigns EPI device.
    ///
    /// JOIN LAYOUT SUMMARY
    /// ───────────────────
    /// Digital 1   IsOnline            ToSIMPL      High when API contact is healthy
    /// Digital 2   PollNow             FromSIMPL    Pulse to trigger status + playlist poll
    /// Digital 2   IsPolling           ToSIMPL      High while an async API call is in progress
    /// Digital 3   PageFirst           FromSIMPL    Navigate to first page of playlists
    /// Digital 4   PageNext            FromSIMPL    Advance to next page of playlists
    /// Digital 5   PreviousPage        FromSIMPL    Go back to previous page of playlists
    /// Digital 6   PlaylistItemAsJson  FromSIMPL    High=JSON format, Low=Name only
    ///
    /// Analog  1   DeviceStatus        ToSIMPL      0=Unknown, 1=ONLINE, 2=SLEEP, 3=OFFLINE
    /// Analog  5   PlaylistCount       ToSIMPL      Total number of playlists available
    /// Analog  6   SelectByAbsIndex    ToFromSIMPL  Absolute 1-based index (pushToScreens)
    /// Analog  7   SelectByRelIndex    ToFromSIMPL  Page-relative 1-based index (pushToScreens)
    /// Analog  8   AssignByAbsIndex    FromSIMPL    Absolute index (updateDevice - asset mode)
    /// Analog  9   AssignByRelIndex    FromSIMPL    Page-relative index (updateDevice - asset mode)
    ///
    /// Serial  1   DeviceName          ToSIMPL      Plugin device name
    /// Serial  2   LastHeartBeat       ToSIMPL      ISO 8601 timestamp of last heartbeat
    /// Serial  6   SelectPlaylistById  FromSIMPL    Send a playlist _id string (pushToScreens)
    /// Serial  6   CurrentPlaylistName ToSIMPL      Name of the currently active playlist
    /// Serial  7   AssignPlaylistById  FromSIMPL    Send a playlist _id string (updateDevice)
    /// Serial  11  PlaylistItem[1]     ToSIMPL      JSON or name only (see D6)
    ///   ...
    /// Serial  40  PlaylistItem[30]    ToSIMPL      (empty if slot unused)
    /// </summary>
    public class OptiSignsBridgeJoinMap : JoinMapBaseAdvanced
    {
        #region Digital

        [JoinName("IsOnline")]
        public JoinDataComplete IsOnline = new JoinDataComplete(
            new JoinData { JoinNumber = 1, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "High when the OptiSigns API has been successfully contacted. " +
                              "Goes low after consecutive poll failures.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Digital
            });
        
        [JoinName("PollNow")]
        public JoinDataComplete PollNow = new JoinDataComplete(
            new JoinData { JoinNumber = 2, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to trigger an immediate device status and playlist poll.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("IsPolling")]
        public JoinDataComplete IsPolling = new JoinDataComplete(
            new JoinData { JoinNumber = 2, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "High while an async API call is currently in progress.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("PageFirst")]
        public JoinDataComplete PageFirst = new JoinDataComplete(
            new JoinData { JoinNumber = 3, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to navigate to the first page of playlists.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });
            
        [JoinName("PageNext")]
        public JoinDataComplete PageNext = new JoinDataComplete(
            new JoinData { JoinNumber = 4, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to advance to the next page of playlists.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("PreviousPage")]
        public JoinDataComplete PreviousPage = new JoinDataComplete(
            new JoinData { JoinNumber = 5, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to go back to the previous page of playlists.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("PlaylistItemAsJsonObject")]
        public JoinDataComplete PlaylistItemAsJsonObject = new JoinDataComplete(
            new JoinData { JoinNumber = 6, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Controls playlist item format on S11-S40. " +
                              "High = JSON format: {\"id\":\"...\",\"name\":\"...\"}. " +
                              "Low = Name only. " +
                              "Default is JSON (high).",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });



        #endregion

        #region Analog

        [JoinName("DeviceStatus")]
        public JoinDataComplete DeviceStatus = new JoinDataComplete(
            new JoinData { JoinNumber = 1, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "OptiSigns device status as an analog value: " +
                              "0 = StatusUnknown, 1 = IsOk (ONLINE), 2 = InWarning (SLEEP), 3 = InError (OFFLINE).",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Analog
            });

        [JoinName("PlaylistCount")]
        public JoinDataComplete PlaylistCount = new JoinDataComplete(
            new JoinData { JoinNumber = 5, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Total number of playlists available. " +
                              "Use pagination (D6-D8) to navigate when count exceeds 30.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Analog
            });

        [JoinName("SelectPlaylistByAbsoluteIndex")]
        public JoinDataComplete SelectPlaylistByAbsoluteIndex = new JoinDataComplete(
            new JoinData { JoinNumber = 6, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Absolute 1-based index across all playlists. Send from SIMPL to select " +
                              "a playlist by its position in the entire list (ignoring pagination). " +
                              "Feedback shows the current playlist's absolute index, or 0 if unknown.",
                JoinCapabilities = eJoinCapabilities.ToFromSIMPL,
                JoinType = eJoinType.Analog
            });

        [JoinName("SelectPlaylistByRelativeIndex")]
        public JoinDataComplete SelectPlaylistByRelativeIndex = new JoinDataComplete(
            new JoinData { JoinNumber = 7, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Page-relative 1-based index. Send from SIMPL to select a playlist " +
                              "on the current page. On page 2, sending 5 selects playlist 35. " +
                              "Feedback shows the current playlist's index on this page, or 0 " +
                              "if the current playlist is not on the visible page. " +
                              "Uses pushToScreens mutation.",
                JoinCapabilities = eJoinCapabilities.ToFromSIMPL,
                JoinType = eJoinType.Analog
            });

        [JoinName("AssignPlaylistAssetByAbsoluteIndex")]
        public JoinDataComplete AssignPlaylistAssetByAbsoluteIndex = new JoinDataComplete(
            new JoinData { JoinNumber = 8, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Absolute 1-based index across all playlists. Send from SIMPL to assign " +
                              "a playlist using updateDevice mutation with currentType=PLAYLIST. " +
                              "Use this when pushToScreens (A6) doesn't properly switch from asset mode.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Analog
            });

        [JoinName("AssignPlaylistAssetByRelativeIndex")]
        public JoinDataComplete AssignPlaylistAssetByRelativeIndex = new JoinDataComplete(
            new JoinData { JoinNumber = 9, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Page-relative 1-based index. Send from SIMPL to assign a playlist " +
                              "using updateDevice mutation with currentType=PLAYLIST. " +
                              "Use this when pushToScreens (A7) doesn't properly switch from asset mode.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Analog
            });

        #endregion

        #region Serial

        [JoinName("DeviceName")]
        public JoinDataComplete DeviceName = new JoinDataComplete(
            new JoinData { JoinNumber = 1, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "The Essentials device name as configured in the JSON config.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Serial
            });

        [JoinName("LastHeartBeat")]
        public JoinDataComplete LastHeartBeat = new JoinDataComplete(
            new JoinData { JoinNumber = 2, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "ISO 8601 timestamp of the last heartbeat reported by the OptiSigns device.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Serial
            });

        [JoinName("SelectPlaylistById")]
        public JoinDataComplete SelectPlaylistById = new JoinDataComplete(
            new JoinData { JoinNumber = 6, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Send a playlist MongoDB _id string from SIMPL to immediately " +
                              "select that playlist without needing to know its index. " +
                              "Uses pushToScreens mutation. Useful for config-driven room logic.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Serial
            });

        [JoinName("CurrentPlaylistName")]
        public JoinDataComplete CurrentPlaylistName = new JoinDataComplete(
            new JoinData { JoinNumber = 6, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Name of the currently active playlist. Empty string when powered off.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Serial
            });

        [JoinName("AssignPlaylistAssetById")]
        public JoinDataComplete AssignPlaylistAssetById = new JoinDataComplete(
            new JoinData { JoinNumber = 7, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Send a playlist MongoDB _id string from SIMPL to assign that playlist " +
                              "using updateDevice mutation with currentType=PLAYLIST. " +
                              "Use when pushToScreens (S6) doesn't properly switch from asset mode.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Serial
            });


        [JoinName("PlaylistNames")]
        public JoinDataComplete PlaylistNames = new JoinDataComplete(
            new JoinData { JoinNumber = 11, JoinSpan = 30 },
            new JoinMetadata
            {
                Description = "Playlist data. Format controlled by D6 (PlaylistItemAsJsonObject): " +
                              "JSON: {\"id\":\"...\",\"name\":\"...\"} or Name only. " +
                              "S11 = playlist 1, S12 = playlist 2, ..., S40 = playlist 30. " +
                              "Slots beyond the current PlaylistCount are sent as empty strings.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Serial
            });

        #endregion

        public OptiSignsBridgeJoinMap(uint joinStart)
            : base(joinStart, typeof(OptiSignsBridgeJoinMap))
        {
        }
    }
}
