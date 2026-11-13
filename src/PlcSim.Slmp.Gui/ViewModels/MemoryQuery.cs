using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Gui.ViewModels;

public static class MemoryQuery
{
    public const int MaxNonZeroRows = 20000;

    public static List<int> NonZeroRows(PlcController controller, MemoryArea area, int per)
    {
        var rows = new SortedSet<int>();
        lock (controller.Plc.SyncRoot)
        {
            var memory = controller.Plc.Memory;
            void AddNonZeroWords(ushort[] words, int wordsPerPoint, uint points)
            {
                var limit = Math.Min(words.Length, (long)points * wordsPerPoint);
                for (var i = 0; i < limit && rows.Count < MaxNonZeroRows; i++)
                {
                    if (words[i] != 0)
                        rows.Add((int)(i / wordsPerPoint / per));
                }
            }

            void AddNonZeroBits(bool[] bits, uint points)
            {
                var limit = Math.Min(bits.Length, points);
                for (var i = 0; i < limit && rows.Count < MaxNonZeroRows; i++)
                {
                    if (bits[i])
                        rows.Add(i / per);
                }
            }

            switch (area.Kind)
            {
                case AreaKind.Label:
                    var labels = controller.Plc.Labels.Snapshot();
                    for (var i = 0; i < labels.Count; i++)
                    {
                        if (labels[i].Data.Any(static b => b != 0))
                            rows.Add(i);
                    }

                    break;
                case AreaKind.Timer:
                case AreaKind.LongTimer:
                case AreaKind.LongCounter:
                    foreach (var part in area.Parts)
                    {
                        var def = Def(part);
                        var key = memory.StorageKey(def);
                        if (def.Kind == DeviceKind.Bit && memory.BitAreas.TryGetValue(key, out var b))
                            AddNonZeroBits(b, area.Points);
                        else if (memory.WordAreas.TryGetValue(key, out var w))
                            AddNonZeroWords(w, def.Kind == DeviceKind.LongTimer ? 4 : def.Kind == DeviceKind.DoubleWord ? 2 : 1, area.Points);
                    }

                    break;
                default:
                    if (area.Key == "自局" || area.Qualifier is not null)
                    {
                        var store = area.Key == "自局" ? controller.Plc.Buffers.OwnMemory : MemoryData.StoreOf(controller, area);
                        foreach (var address in store?.NonZeroWords.Keys ?? Enumerable.Empty<uint>())
                        {
                            if (area.Kind == AreaKind.Bit)
                            {
                                for (var b = 0u; b < 16; b++)
                                {
                                    if ((store!.NonZeroWords[address] & (1 << (int)b)) != 0)
                                        rows.Add((int)(((address * 16) + b) / (uint)per));
                                }
                            }
                            else
                            {
                                rows.Add((int)(address / (uint)per));
                            }
                        }
                    }
                    else if (area.Device is { } device)
                    {
                        var key = memory.StorageKey(device);
                        if (area.Kind == AreaKind.Bit && memory.BitAreas.TryGetValue(key, out var bits))
                            AddNonZeroBits(bits, area.Points);
                        else if (memory.WordAreas.TryGetValue(key, out var words))
                            AddNonZeroWords(words, area.Kind == AreaKind.DoubleWord ? 2 : 1, area.Points);
                    }

                    break;
            }
        }

        return rows.Take(MaxNonZeroRows).ToList();
    }

    private static DeviceDefinition Def(string name) => DeviceCatalog.FindByName(name)!;
}
