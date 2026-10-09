using System.Globalization;
using System.Text.Json;
using PlcSim.Slmp.Core.Model;

namespace PlcSim.Slmp.Core.Config;

/// <summary>
/// 仮想メモリのスナップショット（0 以外の値のみ保存）。
/// デバイス・バッファメモリ・リンクダイレクト・自局メモリ・ラベルを JSON で保存/復元する。
/// </summary>
public sealed class MemorySnapshot
{
    public string Profile { get; set; } = string.Empty;

    public DateTime SavedAt { get; set; }

    /// <summary>格納キー → { 番地: 値 }。</summary>
    public Dictionary<string, Dictionary<string, ushort>> Words { get; set; } = new();

    /// <summary>格納キー → ON の番地。</summary>
    public Dictionary<string, List<uint>> Bits { get; set; } = new();

    /// <summary>"U0\G" / "U3E0\HG" / "J1\W" / "OWN" → { 番地: 値 }。</summary>
    public Dictionary<string, Dictionary<string, ushort>> Buffers { get; set; } = new();

    /// <summary>ラベルパス → 16 進データ。</summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    public static MemorySnapshot Capture(PlcModel plc)
    {
        lock (plc.SyncRoot)
        {
            var snapshot = new MemorySnapshot { Profile = plc.Profile.Id, SavedAt = DateTime.Now };
            foreach (var (key, words) in plc.Memory.WordAreas)
            {
                if (key == "SD")
                    continue;
                var map = new Dictionary<string, ushort>();
                for (var i = 0; i < words.Length; i++)
                {
                    if (words[i] != 0)
                        map[i.ToString(CultureInfo.InvariantCulture)] = words[i];
                }

                if (map.Count > 0)
                    snapshot.Words[key] = map;
            }

            foreach (var (key, bits) in plc.Memory.BitAreas)
            {
                if (key == "SM")
                    continue;
                var on = new List<uint>();
                for (var i = 0; i < bits.Length; i++)
                {
                    if (bits[i])
                        on.Add((uint)i);
                }

                if (on.Count > 0)
                    snapshot.Bits[key] = on;
            }

            foreach (var (unit, store) in plc.Buffers.Modules)
                AddStore(snapshot, $"U{unit:X}\\G", store);
            foreach (var (unit, store) in plc.Buffers.CpuBufferFixedCycle)
                AddStore(snapshot, $"U{unit:X}\\HG", store);
            foreach (var ((network, device), store) in plc.Buffers.LinkDirect)
                AddStore(snapshot, $"J{network}\\{device}", store);
            AddStore(snapshot, "OWN", plc.Buffers.OwnMemory);

            foreach (var label in plc.Labels.Snapshot())
            {
                if (label.Data.Any(static b => b != 0))
                    snapshot.Labels[label.Path] = Convert.ToHexString(label.Data);
            }

            return snapshot;
        }
    }

    public void Restore(PlcModel plc)
    {
        lock (plc.SyncRoot)
        {
            foreach (var (key, map) in Words)
            {
                if (!plc.Memory.WordAreas.TryGetValue(key, out var words))
                    continue;
                foreach (var (index, value) in map)
                {
                    var i = int.Parse(index, CultureInfo.InvariantCulture);
                    if (i < words.Length)
                        words[i] = value;
                }
            }

            foreach (var (key, on) in Bits)
            {
                if (!plc.Memory.BitAreas.TryGetValue(key, out var bits))
                    continue;
                foreach (var i in on.Where(i => i < bits.Length))
                    bits[i] = true;
            }

            foreach (var (key, map) in Buffers)
            {
                var store = ResolveStore(plc, key);
                if (store is null)
                    continue;
                foreach (var (address, value) in map)
                    store.Write(uint.Parse(address, CultureInfo.InvariantCulture), new[] { value });
            }

            foreach (var (path, hex) in Labels)
            {
                try
                {
                    var slot = plc.Labels.Resolve(path);
                    var data = Convert.FromHexString(hex);
                    if (data.Length == slot.Size)
                        plc.Labels.Write(slot, data, "スナップショット");
                }
                catch (Exception)
                {
                    // 定義が変わったラベルは復元しない
                }
            }

            plc.RefreshSpecialDevices();
        }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, SimulatorConfig.JsonOptions));

    public static MemorySnapshot Load(string path)
        => JsonSerializer.Deserialize<MemorySnapshot>(File.ReadAllText(path), SimulatorConfig.JsonOptions)
           ?? throw new InvalidDataException($"スナップショット {path} が空です");

    private static void AddStore(MemorySnapshot snapshot, string key, SparseWordStore store)
    {
        if (store.NonZeroWords.Count == 0)
            return;
        snapshot.Buffers[key] = store.NonZeroWords.ToDictionary(
            static kv => kv.Key.ToString(CultureInfo.InvariantCulture),
            static kv => kv.Value);
    }

    private static SparseWordStore? ResolveStore(PlcModel plc, string key)
    {
        if (key == "OWN")
            return plc.Buffers.OwnMemory;
        var parts = key.Split('\\');
        if (parts.Length != 2)
            return null;
        if (parts[0].StartsWith('J'))
            return plc.Buffers.Link(byte.Parse(parts[0][1..], CultureInfo.InvariantCulture), parts[1]);
        var unit = ushort.Parse(parts[0][1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return parts[1] == "HG" ? plc.Buffers.Hg(unit) : plc.Buffers.Module(unit);
    }
}
