using PepperDash.Essentials.Core;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    /// <summary>
    /// Bridge join map for the OptiSigns EPI device.
    ///
    /// JOIN LAYOUT SUMMARY
    /// ───────────────────
    /// Digital 1   IsOnline            ToSIMPL      High when API contact is healthy
    /// Digital 2   PowerOn             FromSIMPL    Pulse to power on (restore last/default playlist)
    /// Digital 2   PowerIsOn           ToSIMPL      High when currentType != NONE
    /// Digital 3   PowerOff            FromSIMPL    Pulse to power off (sets currentType = NONE)
    /// Digital 3   PowerIsOff          ToSIMPL      High when currentType == NONE
    /// Digital 4   PowerToggle         FromSIMPL    Pulse to toggle power state
    /// Digital 5   PollNow             FromSIMPL    Pulse to trigger an immediate status poll
    /// Digital 6   IsPolling           ToSIMPL      High while an async API call is in progress
    ///
    /// Analog  1   DeviceStatus        ToSIMPL      0=Unknown, 1=Ok, 2=Warning, 3=Error
    /// Analog  5   PlaylistCount       ToSIMPL      Number of playlists available (capped at 30)
    /// Analog  6   SelectPlaylistByIndex ToFromSIMPL 1-based playlist index; 0 = unknown/off
    ///
    /// Serial  1   DeviceName          ToSIMPL      Plugin device name (from Essentials config)
    /// Serial  2   LastHeartBeat       ToSIMPL      ISO 8601 timestamp of last device heartbeat
    /// Serial  6   SelectPlaylistById  FromSIMPL    Send a playlist _id string to select directly
    /// Serial  6   CurrentPlaylistName ToSIMPL      Name of the currently active playlist
    /// Serial  10  PlaylistList        ToSIMPL      Pipe-delimited list: "Name1|Name2|Name3"
    /// Serial  11  PlaylistName[1]     ToSIMPL      Name of playlist 1 (empty if slot unused)
    ///   ...
    /// Serial  40  PlaylistName[30]    ToSIMPL      Name of playlist 30 (empty if slot unused)
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

        [JoinName("PowerOn")]
        public JoinDataComplete PowerOn = new JoinDataComplete(
            new JoinData { JoinNumber = 2, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to power on: pushes last active or default playlist " +
                              "to the screen via pushToScreens (type: NOW).",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("PowerIsOn")]
        public JoinDataComplete PowerIsOn = new JoinDataComplete(
            new JoinData { JoinNumber = 2, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "High when the screen's currentType is not NONE (content is playing).",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("PowerOff")]
        public JoinDataComplete PowerOff = new JoinDataComplete(
            new JoinData { JoinNumber = 3, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to power off: sets currentType to NONE via updateDevice.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("PowerIsOff")]
        public JoinDataComplete PowerIsOff = new JoinDataComplete(
            new JoinData { JoinNumber = 3, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "High when the screen's currentType is NONE (idle/blank).",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("PowerToggle")]
        public JoinDataComplete PowerToggle = new JoinDataComplete(
            new JoinData { JoinNumber = 4, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to toggle power state.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("PollNow")]
        public JoinDataComplete PollNow = new JoinDataComplete(
            new JoinData { JoinNumber = 5, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to trigger an immediate device status poll.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("IsPolling")]
        public JoinDataComplete IsPolling = new JoinDataComplete(
            new JoinData { JoinNumber = 5, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "High while an async API call is currently in progress.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
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
                Description = "Number of playlists currently available, capped at 30. " +
                              "Use this to know how many PlaylistName[N] serial joins are populated.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Analog
            });

        [JoinName("SelectPlaylistByIndex")]
        public JoinDataComplete SelectPlaylistByIndex = new JoinDataComplete(
            new JoinData { JoinNumber = 6, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "1-based index into the available playlist list. " +
                              "Send an index from SIMPL to select that playlist. " +
                              "Feedback reflects the currently active playlist. " +
                              "Value of 0 means no playlist is active or the current playlist " +
                              "is not in the known list.",
                JoinCapabilities = eJoinCapabilities.ToFromSIMPL,
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
                              "Useful for config-driven room logic.",
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

        [JoinName("PlaylistList")]
        public JoinDataComplete PlaylistList = new JoinDataComplete(
            new JoinData { JoinNumber = 10, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pipe-delimited (|) list of playlist names in index order. " +
                              "Suitable for populating a SIMPL+ string array or button list. " +
                              "Index 1 corresponds to the first name in the list.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Serial
            });


        [JoinName("PlaylistNames")]
        public JoinDataComplete PlaylistNames = new JoinDataComplete(
            new JoinData { JoinNumber = 11, JoinSpan = 30 },
            new JoinMetadata
            {
                Description = "Names of available playlists. " +
                              "S11 = playlist 1, S12 = playlist 2, ..., S40 = playlist 30. " +
                              "Slots beyond the current PlaylistCount are sent as empty strings. " +
                              "Use PlaylistCount (A5) to know how many slots are populated.",
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
