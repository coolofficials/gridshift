using System.Reflection;

namespace GridShift;

internal static class GridShiftIcon
{
    public static (Icon Icon, Stream Resource) Load()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GridShift.Assets.gridshift.ico")
            ?? throw new InvalidOperationException("Embedded GridShift icon resource is missing.");
        try { return (new Icon(stream), stream); }
        catch { stream.Dispose(); throw; }
    }
}
