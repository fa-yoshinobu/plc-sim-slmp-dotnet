using System.Text.Json;
using PlcSim.Slmp.Core.Config;

namespace PlcSim.Slmp.Gui.ViewModels;

internal static class ConfigEditing
{
    /// <summary>設定の複製（編集して再構築に渡す）。</summary>
    public static SimulatorConfig Clone(SimulatorConfig config)
    {
        var json = JsonSerializer.Serialize(config, SimulatorConfig.JsonOptions);
        var copy = JsonSerializer.Deserialize<SimulatorConfig>(json, SimulatorConfig.JsonOptions)!;
        copy.BaseDirectory = config.BaseDirectory;
        return copy;
    }
}
