using PepperDash.Essentials.Core;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    /// <summary>
    /// Bridge join map for the OptiSigns Server device.
    ///
    /// The server provides account-level information and device discovery.
    /// Individual players are bridged separately using OptiSignsBridgeJoinMap.
    ///
    /// JOIN LAYOUT SUMMARY
    /// ───────────────────
    /// Digital 1   IsOnline              ToSIMPL      High when API contact is healthy
    /// Digital 2   FetchDevices          FromSIMPL    Pulse to trigger device discovery
    /// Digital 2   IsFetching            ToSIMPL      High while fetching devices
    ///
    /// Analog  1   ConfiguredPlayerCount ToSIMPL      Number of players configured in config
    /// Analog  2   DiscoveredDeviceCount ToSIMPL      Number of devices discovered from API
    ///
    /// Serial  1   ServerName            ToSIMPL      Server device name
    /// Serial  11  DiscoveredDevice[1]   ToSIMPL      JSON: {"id":"...", "name":"...", "status":"..."}
    ///   ...
    /// Serial  40  DiscoveredDevice[30]  ToSIMPL      JSON for device 30
    /// </summary>
    public class OptiSignsServerBridgeJoinMap : JoinMapBaseAdvanced
    {
        #region Digital

        [JoinName("IsOnline")]
        public JoinDataComplete IsOnline = new JoinDataComplete(
            new JoinData { JoinNumber = 1, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "High when the OptiSigns API has been successfully contacted.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("FetchDevices")]
        public JoinDataComplete FetchDevices = new JoinDataComplete(
            new JoinData { JoinNumber = 2, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to trigger device discovery from the API.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("IsFetching")]
        public JoinDataComplete IsFetching = new JoinDataComplete(
            new JoinData { JoinNumber = 2, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "High while an API call is in progress.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("PageFirst")]
        public JoinDataComplete PageFirst = new JoinDataComplete(
            new JoinData { JoinNumber = 3, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to navigate to the first page of devices.",

                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });


        [JoinName("PageNext")]
        public JoinDataComplete PageNext = new JoinDataComplete(
            new JoinData { JoinNumber = 4, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to advance to the next page of devices.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });

        [JoinName("PreviousPage")]
        public JoinDataComplete PreviousPage = new JoinDataComplete(
            new JoinData { JoinNumber = 5, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Pulse from SIMPL to go back to the previous page of devices.",
                JoinCapabilities = eJoinCapabilities.FromSIMPL,
                JoinType = eJoinType.Digital
            });

        #endregion

        #region Analog

        [JoinName("ConfiguredPlayerCount")]
        public JoinDataComplete ConfiguredPlayerCount = new JoinDataComplete(
            new JoinData { JoinNumber = 1, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Number of players configured in the Essentials config.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Analog
            });

        [JoinName("DiscoveredDeviceCount")]
        public JoinDataComplete DiscoveredDeviceCount = new JoinDataComplete(
            new JoinData { JoinNumber = 2, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Number of devices discovered from the OptiSigns API.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Analog
            });

        #endregion

        #region Serial

        [JoinName("ServerName")]
        public JoinDataComplete ServerName = new JoinDataComplete(
            new JoinData { JoinNumber = 1, JoinSpan = 1 },
            new JoinMetadata
            {
                Description = "Server device name from Essentials config.",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Serial
            });

        [JoinName("DiscoveredDevices")]
        public JoinDataComplete DiscoveredDevices = new JoinDataComplete(
            new JoinData { JoinNumber = 11, JoinSpan = 30 },
            new JoinMetadata
            {
                Description = "JSON representation of each discovered device (slots 1-30). " +
                              "Format: {\"id\":\"...\",\"name\":\"...\",\"status\":\"...\",\"uuid\":\"...\"}",
                JoinCapabilities = eJoinCapabilities.ToSIMPL,
                JoinType = eJoinType.Serial
            });

        #endregion

        public OptiSignsServerBridgeJoinMap(uint joinStart)
            : base(joinStart, typeof(OptiSignsServerBridgeJoinMap))
        {
        }
    }
}
