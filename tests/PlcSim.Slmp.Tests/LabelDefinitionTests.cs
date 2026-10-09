using PlcComm.Slmp;
using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Server;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Tests;

/// <summary>ラベル定義の追加・編集（GUI の「ラベル」画面と設定の plc.labels）。</summary>
public sealed class LabelDefinitionTests
{
    private static LabelDefinitions Recipe() => new()
    {
        Structs =
        {
            ["RECIPE"] = new() { new("Speed", "WORD"), new("Target", "DINT"), new("Values", "WORD", 3) },
        },
        Globals = { new("Recipe", "RECIPE"), new("Count", "WORD"), new("温度", "REAL") },
    };

    [Fact]
    public void ValidateAcceptsUserDefinitions()
    {
        Assert.Empty(Recipe().Validate());
        Assert.Empty(LabelDefinitions.SlmpTestDefault().Validate());
    }

    [Theory]
    [InlineData("1Bad", "WORD", 0, "使えない文字")]
    [InlineData("A.B", "WORD", 0, "使えない文字")]
    [InlineData("Good", "NO_TYPE", 0, "未定義")]
    [InlineData("Good", "WORD", -1, "配列要素数")]
    [InlineData("Good", "WORD", 70000, "配列要素数")]
    public void ValidateRejectsBadGlobals(string name, string type, int length, string expected)
    {
        var defs = new LabelDefinitions { Globals = { new(name, type, length) } };
        Assert.Contains(defs.Validate(), e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsDuplicatesAndCycles()
    {
        var duplicate = new LabelDefinitions { Globals = { new("A", "WORD"), new("A", "DWORD") } };
        Assert.Contains(duplicate.Validate(), e => e.Contains("重複", StringComparison.Ordinal));

        var cycle = new LabelDefinitions
        {
            Structs = { ["S1"] = new() { new("Next", "S2") }, ["S2"] = new() { new("Back", "S1") } },
            Globals = { new("Root", "S1") },
        };
        Assert.Contains(cycle.Validate(), e => e.Contains("循環", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => new LabelStore(cycle));
    }

    [Fact]
    public async Task UserDefinedLabelsAreReadableFromClient()
    {
        var plc = new PlcModel(CpuProfiles.Get(CpuProfiles.IqR), null, Recipe());
        await using var server = new SlmpServer(new SlmpEngine(plc), new SlmpServerOptions { TcpPort = 0, UdpPort = 0 });
        await server.StartAsync();
        var options = new SlmpConnectionOptions("127.0.0.1", SlmpPlcProfile.IqR, server.TcpPort, SlmpTransportMode.Tcp, SlmpTargetAddress.OwnStation);
        await using var client = await SlmpClientFactory.OpenAndConnectAsync(options);

        // 構造体 RECIPE: Speed(2) Target(4) Values[3](6) → Values[1] は先頭から 8 バイト目。
        await client.WriteArrayLabelsAsync(new[] { new SlmpLabelArrayWritePoint("Recipe.Values[1]", 1, 2, new byte[] { 0x22, 0x11 }) });
        var slot = plc.Labels.Resolve("Recipe.Values[1]");
        Assert.Equal(8, slot.Offset);
        Assert.Equal(new byte[] { 0x22, 0x11 }, plc.Labels.Read(slot, 2));

        await client.WriteRandomLabelsAsync(new[] { new SlmpLabelRandomWritePoint("Count", new byte[] { 5, 0 }) });
        var read = await client.ReadRandomLabelsAsync(new[] { "Count" });
        Assert.Equal(new byte[] { 5, 0 }, read[0].Data);
    }

    [Fact]
    public async Task RebuildWithNewLabelsKeepsUnchangedValues()
    {
        var config = new SimulatorConfig();
        config.Server.TcpPort = 0;
        config.Server.UdpPort = 0;
        config.Api.Enabled = false;
        config.Plc.Labels = Recipe();
        await using var runtime = new SimulatorRuntime(config, null);
        var labels = runtime.Plc.Labels;
        labels.Write(labels.Resolve("Count"), new byte[] { 7, 0 }, "test");
        labels.Write(labels.Resolve("Recipe.Speed"), new byte[] { 9, 0 }, "test");

        // Count はそのまま、Recipe.Speed は WORD → DWORD に変更、Added を追加。
        var next = new SimulatorConfig { Server = config.Server, Api = config.Api };
        next.Plc.Labels = Recipe();
        next.Plc.Labels.Structs["RECIPE"][0] = new("Speed", "DWORD");
        next.Plc.Labels.Globals.Add(new("Added", "WORD", 4));
        await runtime.RebuildAsync(next);

        labels = runtime.Plc.Labels;
        Assert.Equal(new byte[] { 7, 0 }, labels.Read(labels.Resolve("Count"), 2));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, labels.Read(labels.Resolve("Recipe.Speed"), 4));
        Assert.Equal(8, labels.Resolve("Added").Size);
    }
}
