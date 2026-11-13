using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;
using static PlcSim.Slmp.Gui.ViewModels.MemoryValueCodec;

namespace PlcSim.Slmp.Gui.ViewModels;

public static class MemoryEditor
{
    public static string? Edit(PlcController controller, MemoryArea area, MemoryCell cell, string? input, string format)
    {
        try
        {
            if (area.Kind == AreaKind.Label)
            {
                WriteLabel(controller, cell.LabelPath!, input ?? string.Empty);
                return null;
            }

            var address = MemoryData.CellAddress(area, cell);
            if (cell.IsBit)
            {
                controller.SetBit(address, !cell.IsOn, "GUI");
            }
            else if (area.Key == "自局")
            {
                ushort[] values;
                if (format is "u32" or "f32")
                {
                    var value = format == "f32"
                        ? BitConverter.SingleToUInt32Bits(float.Parse(input!, CultureInfo.InvariantCulture))
                        : ParseDWord(input!);
                    values = [(ushort)value, (ushort)(value >> 16)];
                }
                else
                    values = [format == "asc" ? ParseAsciiWord(input!) : ParseWord(input!, format == "s16")];
                lock (controller.Plc.SyncRoot)
                    controller.Plc.Buffers.OwnMemory.Write(cell.Number, values);
                controller.Plc.RaiseBufferChanged(new MemoryChange("自局", cell.Number, values.Length, "GUI", DateTime.Now));
            }
            else if (area.Kind is AreaKind.LongTimer or AreaKind.LongCounter or AreaKind.DoubleWord && cell.Role == "value")
            {
                controller.SetDWord(address, ParseDWord(input!), "GUI");
            }
            else if (format == "f32" && area.Kind == AreaKind.Word)
            {
                controller.SetFloat(address, float.Parse(input!, CultureInfo.InvariantCulture), "GUI");
            }
            else if (format == "u32" && area.Kind == AreaKind.Word)
            {
                controller.SetDWord(address, ParseDWord(input!), "GUI");
            }
            else if (format == "asc" && area.Kind == AreaKind.Word)
            {
                controller.SetWord(address, ParseAsciiWord(input ?? string.Empty), "GUI");
            }
            else
            {
                controller.SetWord(address, ParseWord(input!, format == "s16" && area.Kind == AreaKind.Word), "GUI");
            }

            return null;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or SlmpEndCodeException or ArgumentException)
        {
            return ex.Message;
        }
    }

    private static void WriteLabel(PlcController controller, string path, string input)
    {
        var label = controller.Plc.Labels.Snapshot().First(l => l.Path == path);
        var data = new byte[label.Data.Length];
        switch (label.Type.ToUpperInvariant())
        {
            case "BIT":
            case "BOOL":
                data[0] = (byte)(label.Data[0] == 0 ? 1 : 0);
                break;
            case "WORD":
            case "INT":
                BinaryPrimitives.WriteUInt16LittleEndian(data, ParseWord(input, label.Type == "INT"));
                break;
            case "DWORD":
            case "DINT":
                BinaryPrimitives.WriteUInt32LittleEndian(data, ParseDWord(input, label.Type == "DINT"));
                break;
            case "REAL":
            case "FLOAT":
                BinaryPrimitives.WriteSingleLittleEndian(data, float.Parse(input, CultureInfo.InvariantCulture));
                break;
            case "LREAL":
            case "DOUBLE":
                BinaryPrimitives.WriteDoubleLittleEndian(data, double.Parse(input, CultureInfo.InvariantCulture));
                break;
            default:
                var wide = label.Type.StartsWith("WSTRING(", StringComparison.OrdinalIgnoreCase);
                var encoding = wide ? Encoding.Unicode : Encoding.ASCII;
                encoding.GetEncoder().Convert(input.AsSpan(), data.AsSpan(0, data.Length - (wide ? 2 : 1)),
                    true, out _, out _, out _);
                break;
        }

        controller.WriteLabel(path, data, "GUI");
    }
}
