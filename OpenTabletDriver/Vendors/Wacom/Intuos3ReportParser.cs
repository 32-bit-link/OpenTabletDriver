using OpenTabletDriver.Plugin.Tablet;
using OpenTabletDriver.Tablet;

namespace OpenTabletDriver.Vendors.Wacom
{
    public class Intuos3ReportParser : IReportParser<IDeviceReport>
    {
        public virtual IDeviceReport Parse(byte[] data)
        {
            return data[0] switch
            {
                0x02 => GetToolReport(data),
                0x10 => new IntuosV2TabletReport(data),
                0x03 => new IntuosV2AuxReport(data),
                0x0C => new Intuos3AuxReport(data),
                _ => new DeviceReport(data)
            };
        }

        private IDeviceReport GetToolReport(byte[] data)
        {
            return data[1] switch
            {
                0xE0 => new IntuosV2TabletReport(data),
                0xF0 => new Intuos3MouseReport(data),
                _ => new DeviceReport(data)
            };
        }
    }
}