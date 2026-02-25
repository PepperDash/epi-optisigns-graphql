using System.Collections.Generic;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;
using Serilog.Events;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    public class OptiSignsDeviceFactory : EssentialsPluginDeviceFactory<OptiSignsDevice>
    {
        public OptiSignsDeviceFactory()
        {
            TypeNames = new List<string>
            {
                "optiSigns",
                "optiSignsDevice"
            };

            MinimumEssentialsFrameworkVersion = "2.24.4";
        }

        public override EssentialsDevice BuildDevice(DeviceConfig dc)
        {
            this.LogDebug("Building OptiSigns device: {0}", dc.Key);

            var props = dc.Properties.ToObject<OptiSignsPropertiesConfig>();

            if (props == null)
            {
                this.LogError("OptiSigns: failed to deserialize properties for device '{0}'. Check JSON schema.", dc.Key);
                return null;
            }

            if (string.IsNullOrEmpty(props.ApiKey))
            {
                this.LogError("OptiSigns: 'apiKey' is required in properties for device '{0}'", dc.Key);
                return null;
            }

            if (string.IsNullOrEmpty(props.TeamId))
            {
                this.LogError("OptiSigns: 'teamId' is required in properties for device '{0}'", dc.Key);
                return null;
            }

            if (string.IsNullOrEmpty(props.DeviceId))
            {
                this.LogError("OptiSigns: 'deviceId' is required in properties for device '{0}'", dc.Key);
                return null;
            }

            return new OptiSignsDevice(dc.Key, dc.Name, props);
        }
    }
}
