using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Gui.ViewModels;

public static class MemoryRowRenderer
{
    public static (int Columns, IReadOnlyList<string> Headers) Layout(MemoryArea area)
    {
        var columns = area.Kind switch
        {
            AreaKind.Bit => 16,
            AreaKind.Word => area.Radix == 16 ? 16 : 8,
            AreaKind.Timer or AreaKind.LongCounter => 3,
            AreaKind.LongTimer => 4,
            AreaKind.DoubleWord => 1,
            _ => 3,
        };
        var headers = new List<string>();
        switch (area.Kind)
        {
            case AreaKind.Bit:
            case AreaKind.Word:
                for (var i = 0; i < columns; i++)
                    headers.Add("+" + (area.Radix == 16 || area.Kind == AreaKind.Bit ? i.ToString("X") : i.ToString()));
                break;
            case AreaKind.Timer:
            case AreaKind.LongCounter:
                headers.Add($"接点 {area.Parts[0]}");
                headers.Add($"コイル {area.Parts[1]}");
                headers.Add($"現在値 {area.Parts[2]}");
                break;
            case AreaKind.LongTimer:
                headers.Add($"接点 {area.Parts[0]}");
                headers.Add($"コイル {area.Parts[1]}");
                headers.Add($"現在値 {area.Parts[2]}（32bit）");
                headers.Add("状態ワード");
                break;
            case AreaKind.DoubleWord:
                headers.Add("値（32bit）");
                break;
            case AreaKind.Label:
                headers.Add("型");
                headers.Add("値");
                headers.Add("16進");
                break;
        }

        return (columns, headers);
    }

    public static void FillRow(PlcController controller, int columns, string format, MemoryArea area, MemoryRow row, uint start, IReadOnlyList<(uint Start, int Count)> recent)
    {
        bool IsRecent(uint n) => recent.Any(r => n >= r.Start && n < r.Start + (uint)Math.Max(1, r.Count));
        var hasValue = false;
        switch (area.Kind)
        {
            case AreaKind.Bit:
                {
                    row.Address = area.Prefix + area.FormatNumber(start);
                    var count = (int)Math.Min(columns, area.Points - start);
                    var bits = MemoryData.ReadBits(controller, area, start, count);
                    for (var i = 0; i < columns; i++)
                    {
                        var cell = row.Cells[i];
                        cell.IsBit = true;
                        cell.Number = start + (uint)i;
                        cell.Editable = i < count && !area.ReadOnly;
                        cell.IsOn = i < count && bits[i];
                        cell.Text = i < count ? (bits[i] ? "ON" : "·") : string.Empty;
                        cell.IsRecent = i < count && IsRecent(cell.Number);
                        hasValue |= cell.IsOn;
                    }

                    break;
                }

            case AreaKind.Word:
                {
                    row.Address = area.Prefix + area.FormatNumber(start);
                    var count = (int)Math.Min(columns, area.Points - start);
                    var extra = format is "u32" or "f32" && start + (uint)count < area.Points ? 1 : 0;
                    var words = MemoryData.ReadWords(controller, area, start, count + extra);
                    for (var i = 0; i < columns; i++)
                    {
                        var cell = row.Cells[i];
                        cell.IsBit = false;
                        cell.Number = start + (uint)i;
                        cell.Editable = i < count && !area.ReadOnly &&
                            (format is not ("u32" or "f32") || (i % 2 == 0 && i + 1 < count));
                        cell.Text = i < count ? MemoryValueCodec.FormatWord(words, i, format) : string.Empty;
                        cell.IsRecent = i < count && IsRecent(cell.Number);
                        hasValue |= i < count && words[i] != 0;
                    }

                    break;
                }

            case AreaKind.Timer:
            case AreaKind.LongTimer:
            case AreaKind.LongCounter:
                {
                    row.Address = area.Prefix + start;
                    var memory = controller.Plc.Memory;
                    var contact = memory.ReadBits(Def(area.Parts[0]), start, 1)[0];
                    var coil = memory.ReadBits(Def(area.Parts[1]), start, 1)[0];
                    uint value;
                    ushort status = 0;
                    if (area.Kind == AreaKind.LongTimer)
                    {
                        var w = memory.ReadWords(Def(area.Parts[2]), start, 4);
                        value = (uint)(w[0] | (w[1] << 16));
                        status = w[2];
                    }
                    else if (area.Kind == AreaKind.LongCounter)
                    {
                        value = memory.ReadDWordPoint(Def(area.Parts[2]), start);
                    }
                    else
                    {
                        value = memory.ReadWords(Def(area.Parts[2]), start, 1)[0];
                    }

                    SetBitCell(row.Cells[0], start, contact, "contact", recent: false);
                    SetBitCell(row.Cells[1], start, coil, "coil", recent: false);
                    var valueCell = row.Cells[2];
                    valueCell.IsBit = false;
                    valueCell.Role = "value";
                    valueCell.Number = start;
                    valueCell.Editable = true;
                    valueCell.Text = value.ToString("N0");
                    valueCell.IsRecent = IsRecent(start);
                    if (area.Kind == AreaKind.LongTimer)
                    {
                        var statusCell = row.Cells[3];
                        statusCell.IsBit = false;
                        statusCell.Role = "status";
                        statusCell.Editable = false;
                        statusCell.Text = $"{status:X4}h";
                    }

                    hasValue = contact || coil || value != 0;
                    break;
                }

            case AreaKind.DoubleWord:
                {
                    row.Address = area.Prefix + start;
                    var value = controller.Plc.Memory.ReadDWordPoint(area.Device!, start);
                    var cell = row.Cells[0];
                    cell.Number = start;
                    cell.Role = "value";
                    cell.Text = format == "hex" ? $"{value:X8}" : value.ToString();
                    cell.IsRecent = IsRecent(start);
                    hasValue = value != 0;
                    break;
                }

            case AreaKind.Label:
                {
                    var labels = controller.Plc.Labels.Snapshot();
                    if (start >= labels.Count)
                        break;
                    var label = labels[(int)start];
                    row.Address = label.Path;
                    row.Cells[0].Text = label.Type;
                    row.Cells[0].Editable = false;
                    row.Cells[1].Text = MemoryValueCodec.LabelValueText(label);
                    row.Cells[1].LabelPath = label.Path;
                    row.Cells[1].Role = "label";
                    row.Cells[1].Editable = true;
                    row.Cells[2].Text = Convert.ToHexString(label.Data);
                    row.Cells[2].Editable = false;
                    hasValue = label.Data.Any(static b => b != 0);
                    break;
                }
        }

        row.HasValue = hasValue;

        void SetBitCell(MemoryCell cell, uint number, bool on, string role, bool recent)
        {
            cell.IsBit = true;
            cell.Role = role;
            cell.Number = number;
            cell.Editable = true;
            cell.IsOn = on;
            cell.Text = on ? "ON" : "·";
            cell.IsRecent = recent;
        }
    }

    private static DeviceDefinition Def(string name) => DeviceCatalog.FindByName(name)!;
}
