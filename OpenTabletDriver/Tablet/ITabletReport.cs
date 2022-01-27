namespace OpenTabletDriver.Tablet
{
    public interface ITabletReport : IAbsolutePositionReport
    {
        uint Pressure { set; get; }
        bool[] PenButtons { set; get; }
    }
}
