using System.Buffers.Binary;
using System.Text;
using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Gui.ViewModels;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Gui.Tests;

public sealed class MemoryEditingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0x0041)]
    [InlineData(0x7F41)]
    [InlineData(0x8041)]
    [InlineData(0x4241)]
    public async Task AsciiEditorPreservesPrintableAndControlBytes(ushort value)
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        runtime.Controller.SetWord("D0", value, "test");
        var vm = new MemoryViewModel(runtime) { Format = "asc" };
        var cell = vm.Rows[0].Cells[0];
        var input = vm.EditText(cell);
        Assert.DoesNotContain('·', input);
        Assert.Null(vm.Edit(cell, input));
        Assert.Equal(value, runtime.Controller.GetWord("D0"));
        Assert.Null(vm.Edit(cell, "AB"));
        Assert.Equal((ushort)0x4241, runtime.Controller.GetWord("D0"));
    }

    [Theory]
    [InlineData("u32", "2", 2u, 2)]
    [InlineData("u32", "4294967295", 4294967295u, 2)]
    [InlineData("f32", "2", 0x40000000u, 2)]
    [InlineData("asc", "AB", 0x00014241u, 1)]
    [InlineData("asc", "0x0010", 0x00010010u, 1)]
    [InlineData("s16", "-1", 0x0001FFFFu, 1)]
    public async Task OwnMemoryWritesTheCorrectWordsAndNotification(string format, string input, uint expected, int count)
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        runtime.Plc.Buffers.OwnMemory.Write(0, new ushort[] { 0, 1 });
        var vm = new MemoryViewModel(runtime);
        vm.SelectedArea = vm.Areas.Single(a => a.Key == "自局");
        vm.Format = format;
        MemoryChange? change = null;
        runtime.Plc.BufferChanged += value => change = value;
        Assert.Null(vm.Edit(vm.Rows[0].Cells[0], input));
        var words = runtime.Plc.Buffers.OwnMemory.Read(0, 2);
        Assert.Equal(expected, (uint)(words[0] | (words[1] << 16)));
        Assert.NotNull(change);
        Assert.Equal(count, change.Count);
    }

    [Theory]
    [InlineData("D", 16u)]
    [InlineData("D", 0xABCDu)]
    [InlineData("LZ", 0xABCDEF01u)]
    public async Task HexEditorPreservesUnchangedValues(string area, uint value)
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        var vm = new MemoryViewModel(runtime);
        vm.SelectedArea = vm.Areas.Single(a => a.Key == area);
        if (area == "LZ") runtime.Controller.SetDWord("LZ0", value, "test");
        else runtime.Controller.SetWord("D0", (ushort)value, "test");
        vm.Format = "hex";
        var cell = vm.Rows[0].Cells[0];
        Assert.StartsWith("0x", vm.EditText(cell));
        Assert.Null(vm.Edit(cell, vm.EditText(cell)));
        Assert.Equal(value, area == "LZ" ? runtime.Controller.GetDWord("LZ0") : runtime.Controller.GetWord("D0"));
    }

    [Theory]
    [InlineData("dec", "65536")]
    [InlineData("dec", "-1")]
    [InlineData("s16", "32768")]
    [InlineData("s16", "-32769")]
    [InlineData("u32", "4294967296")]
    [InlineData("u32", "-1")]
    [InlineData("hex", "0x10000")]
    public async Task InvalidIntegerDoesNotChangeMemory(string format, string input)
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        runtime.Controller.SetDWord("D0", 123, "test");
        var vm = new MemoryViewModel(runtime) { Format = format };
        Assert.NotNull(vm.Edit(vm.Rows[0].Cells[0], input));
        Assert.Equal(123u, runtime.Controller.GetDWord("D0"));
    }

    [Theory]
    [InlineData("dec", "65535", 65535u)]
    [InlineData("s16", "-32768", 32768u)]
    [InlineData("s16", "0xFFFF", 65535u)]
    [InlineData("u32", "4294967295", 4294967295u)]
    public async Task IntegerBoundariesRemainWritable(string format, string input, uint expected)
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        var vm = new MemoryViewModel(runtime) { Format = format };
        Assert.Null(vm.Edit(vm.Rows[0].Cells[0], input));
        Assert.Equal(expected, format == "u32" ? runtime.Controller.GetDWord("D0") : runtime.Controller.GetWord("D0"));
    }

    [Theory]
    [InlineData("u32")]
    [InlineData("f32")]
    public async Task PairedValuesCannotBeEditedFromTheirSecondWord(string format)
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        runtime.Controller.SetDWord("D0", 0x12345678, "test");
        runtime.Controller.SetWord("D2", 0xABCD, "test");
        var vm = new MemoryViewModel(runtime) { Format = format };
        var cell = vm.Rows[0].Cells[1];
        Assert.Equal(string.Empty, cell.Text);
        Assert.False(cell.Editable);
        vm.Edit(cell, "1");
        Assert.Equal(0x12345678u, runtime.Controller.GetDWord("D0"));
        Assert.Equal((ushort)0xABCD, runtime.Controller.GetWord("D2"));
    }

    [Fact]
    public async Task IncompleteFinalPairCannotBeEdited()
    {
        var config = new SimulatorConfig();
        config.Plc.DevicePoints["D"] = 3;
        await using var runtime = new SimulatorRuntime(config, null);
        var vm = new MemoryViewModel(runtime) { Format = "u32" };
        Assert.True(vm.Rows[0].Cells[0].Editable);
        Assert.False(vm.Rows[0].Cells[2].Editable);
    }

    [Fact]
    public async Task FloatEditorPreservesAllBits()
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        runtime.Controller.SetFloat("D0", 1.2345679f, "test");
        var before = runtime.Controller.GetDWord("D0");
        var vm = new MemoryViewModel(runtime) { Format = "f32" };
        Assert.Null(vm.Edit(vm.Rows[0].Cells[0], vm.EditText(vm.Rows[0].Cells[0])));
        Assert.Equal(before, runtime.Controller.GetDWord("D0"));
    }

    [Theory]
    [InlineData("REAL")]
    [InlineData("LREAL")]
    [InlineData("WSTRING(8)")]
    public async Task LabelsPreservePrecisionAndUnicode(string type)
    {
        var config = new SimulatorConfig();
        config.Plc.Labels = new LabelDefinitions { Globals = { new("Value", type) } };
        await using var runtime = new SimulatorRuntime(config, null);
        var data = new byte[runtime.Plc.Labels.Snapshot().Single().Data.Length];
        if (type == "REAL") BinaryPrimitives.WriteSingleLittleEndian(data, 1.2345679f);
        else if (type == "LREAL") BinaryPrimitives.WriteDoubleLittleEndian(data, 1.2345678901234567);
        else Encoding.Unicode.GetBytes("日本😀").CopyTo(data, 0);
        runtime.Controller.WriteLabel("Value", data, "test");
        var vm = new MemoryViewModel(runtime);
        vm.SelectedArea = vm.Areas.Single(a => a.Kind == AreaKind.Label);
        var cell = vm.Rows[0].Cells[1];
        if (type.StartsWith("WSTRING")) Assert.Equal("日本😀", vm.EditText(cell));
        Assert.Null(vm.Edit(cell, vm.EditText(cell)));
        Assert.Equal(data, runtime.Plc.Labels.Snapshot().Single().Data);
    }

    [Theory]
    [InlineData("WORD", "65536")]
    [InlineData("INT", "-32769")]
    [InlineData("DWORD", "4294967296")]
    [InlineData("DINT", "2147483648")]
    public async Task InvalidLabelIntegerDoesNotChangeMemory(string type, string input)
    {
        var config = new SimulatorConfig();
        config.Plc.Labels = new LabelDefinitions { Globals = { new("Value", type) } };
        await using var runtime = new SimulatorRuntime(config, null);
        var vm = new MemoryViewModel(runtime);
        vm.SelectedArea = vm.Areas.Single(a => a.Kind == AreaKind.Label);
        Assert.NotNull(vm.Edit(vm.Rows[0].Cells[1], input));
        Assert.All(runtime.Plc.Labels.Snapshot().Single().Data, b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public async Task NonzeroFilterTracksWritesAndClearsAfterRebuild()
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        var vm = new MemoryViewModel(runtime);
        vm.SelectedArea = vm.Areas.Single(a => a.Key == "W");
        vm.NonZeroOnly = true;
        Assert.DoesNotContain(vm.Rows, r => r.IsVisible);
        runtime.Controller.SetWord("W100", 42, "test");
        vm.Refresh();
        Assert.Contains(vm.Rows, r => r.IsVisible && r.Address == "W100");
        runtime.Controller.SetWord("W100", 0, "test");
        vm.Refresh();
        Assert.DoesNotContain(vm.Rows, r => r.IsVisible);
        await runtime.RebuildAsync(new SimulatorConfig());
        vm.Rebuild();
        runtime.Controller.SetWord("W200", 42, "test");
        vm.Refresh();
        Assert.Contains(vm.Rows, r => r.IsVisible && r.Address == "W200");
    }
}
