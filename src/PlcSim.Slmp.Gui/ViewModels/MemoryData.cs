using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Gui.ViewModels;

public static class MemoryData
{
    public static bool[] ReadBits(PlcController controller, MemoryArea area, uint start, int count)
    {
        if (count <= 0)
            return Array.Empty<bool>();
        if (area.Qualifier is not null)
            return controller.Accessor(new DeviceAddress(area.Device!, start, area.Qualifier)).ReadBits(count);
        return controller.Plc.Memory.ReadBits(area.Device!, start, count);
    }

    public static ushort[] ReadWords(PlcController controller, MemoryArea area, uint start, int count)
    {
        if (count <= 0)
            return Array.Empty<ushort>();
        if (area.Key == "自局")
            return controller.Plc.Buffers.OwnMemory.Read(start, count);
        if (area.Qualifier is not null)
            return controller.Accessor(new DeviceAddress(area.Device!, start, area.Qualifier)).ReadWords(count);
        return controller.Plc.Memory.ReadWords(area.Device!, start, count);
    }

    public static string CellAddress(MemoryArea area, MemoryCell cell)
    {
        var name = cell.Role switch
        {
            "contact" => area.Parts[0],
            "coil" => area.Parts[1],
            "value" when area.Parts.Length > 0 => area.Parts[2],
            _ => area.Device?.Name ?? area.Key,
        };
        var number = area.Parts.Length > 0 ? cell.Number.ToString() : area.FormatNumber(cell.Number);
        return area.Qualifier is null ? $"{name}{number}" : $"{area.Qualifier}\\{name}{number}";
    }

    public static SparseWordStore? StoreOf(PlcController controller, MemoryArea area)
    {
        if (area.Qualifier is null || area.Device is null)
            return null;
        var number = Convert.ToUInt16(area.Qualifier[1..], area.Qualifier[0] == 'U' ? 16 : 10);
        var buffers = controller.Plc.Buffers;
        return area.Qualifier[0] == 'J'
            ? buffers.Link((byte)number, area.Device.Name)
            : area.Device.Name == "HG" ? buffers.Hg(number) : buffers.Module(number);
    }
}
