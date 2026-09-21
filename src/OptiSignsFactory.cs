using System.Collections.Generic;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;
using Serilog.Events;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    public class OptiSignsDeviceFactory : EssentialsPluginDeviceFactory<OptiSignsServer>
    {
        public OptiSignsDeviceFactory()
        {
            TypeNames = new List<string>
            {
                "optiSigns",
                "optiSignsServer"
            };

            MinimumEssentialsFrameworkVersion = "2.24.4";
        }

        public override EssentialsDevice BuildDevice(DeviceConfig dc)
        {
            Debug.LogMessage(LogEventLevel.Debug, "Building OptiSigns server: {0}", dc.Key);

            var props = dc.Properties.ToObject<OptiSignsPropertiesConfig>();

            if (props == null)
            {
                Debug.LogMessage(LogEventLevel.Error, 
                    "OptiSigns: failed to deserialize properties for device '{0}'. Check JSON schema.", dc.Key);
                return null;
            }

            if (string.IsNullOrEmpty(props.ApiKey))
            {
                Debug.LogMessage(LogEventLevel.Error, 
                    "OptiSigns: 'apiKey' is required in properties for device '{0}'", dc.Key);
                return null;
            }

            if (props.Players == null || props.Players.Count == 0)
            {
                Debug.LogMessage(LogEventLevel.Error, 
                    "OptiSigns: 'players' array is required and must contain at least one player for device '{0}'", dc.Key);
                return null;
            }

            return new OptiSignsServer(dc.Key, dc.Name, props);
        }
    }
}
