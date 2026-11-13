using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Gui.ViewModels;
using PlcSim.Slmp.Scripting;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace PlcSim.Slmp.Gui.Tests;

public sealed class MemoryServicesTests
{
    [Theory]
    [InlineData("hex", "FFFF")]
    [InlineData("s16", "-1")]
    [InlineData("dec", "65535")]
    public void FormattingPreservesWordBits(string format, string expected)
        => Assert.Equal(expected, MemoryValueCodec.FormatWord([65535], 0, format));

    [Fact]
    public void ConversionsRejectOverflowAndPreserveSignedHex()
    {
        Assert.Equal(65535, MemoryValueCodec.ParseWord("0xFFFF", signed: true));
        Assert.Throws<OverflowException>(() => MemoryValueCodec.ParseWord("65536"));
        Assert.Equal(0x4241, MemoryValueCodec.ParseAsciiWord("AB"));
        Assert.Equal("1", MemoryValueCodec.FormatWord([0, 0x3f80], 0, "f32"));
        Assert.Equal(string.Empty, MemoryValueCodec.FormatWord([1, 2], 1, "u32"));
    }

    [Fact]
    public async Task QueryEditorAndRendererWorkWithoutAWindow()
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        var controller = runtime.Controller;
        var areas = MemoryAreaCatalog.BuildAreas(controller, "U0", "U3E0", "J1").ToArray();
        var area = areas.Single(a => a.Key == "D");
        Assert.Equal(("ZR", 12u), MemoryAreaCatalog.JumpTarget(controller, "R12"));
        var row = new MemoryRow(8);
        MemoryRowRenderer.FillRow(controller, 8, "dec", area, row, 8, []);
        Assert.Null(MemoryEditor.Edit(controller, area, row.Cells[1], "42", "dec"));
        Assert.Equal(42, controller.GetWord("D9"));
        Assert.Equal(new[] { 1 }, MemoryQuery.NonZeroRows(controller, area, 8));
        MemoryRowRenderer.FillRow(controller, 8, "hex", area, row, 8, [(9, 1)]);
        Assert.Equal("002A", row.Cells[1].Text);
        Assert.True(row.Cells[1].IsRecent);
        Assert.False(row.Cells[0].IsRecent);
    }

    [Fact]
    public void CsvIncludesOnlyVisibleRowsAndSelectedColumns()
    {
        var row = new MemoryRow(2) { Address = "D0", IsVisible = true };
        row.Cells[0].Text = "42";
        row.Cells[1].Text = "hidden column";
        var hidden = new MemoryRow(1) { Address = "D1", IsVisible = false };
        Assert.Equal($"番地,+0{Environment.NewLine}D0,42{Environment.NewLine}", MemoryCsv.Format(["+0"], [row, hidden]));
    }

    [Theory]
    [InlineData("A,B", "\"A,B\"")]
    [InlineData("A\"B", "\"A\"\"B\"")]
    [InlineData("A\rB", "\"A\rB\"")]
    [InlineData("A\nB", "\"A\nB\"")]
    [InlineData("A\r\nB", "\"A\r\nB\"")]
    public void CsvQuotesSpecialCharacters(string value, string field)
    {
        var row = new MemoryRow(1) { Address = "D0", IsVisible = true };
        row.Cells[0].Text = value;
        Assert.Equal($"番地,+0{Environment.NewLine}D0,{field}{Environment.NewLine}", MemoryCsv.Format(["+0"], [row]));
    }

    [Fact]
    public void CsvFileRoundTripsQuotedFieldsAndFollowingRows()
    {
        var row = new MemoryRow(2) { Address = "ラベル,\"値\"", IsVisible = true };
        row.Cells[0].Text = "A,\"B\"\r\n日本語";
        row.Cells[1].Text = string.Empty;
        var next = new MemoryRow(2) { Address = "D1", IsVisible = true };
        next.Cells[0].Text = "42";
        next.Cells[1].Text = "末尾";
        var path = Path.GetTempFileName();
        try
        {
            MemoryCsv.Write(path, ["値,1", "値\"2"], [row, next]);
            using var csv = new TextFieldParser(path, System.Text.Encoding.UTF8)
            {
                Delimiters = [","], HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false,
            };
            Assert.Equal(new[] { "番地", "値,1", "値\"2" }, csv.ReadFields());
            Assert.Equal(new[] { row.Address, row.Cells[0].Text, string.Empty }, csv.ReadFields());
            Assert.Equal(new[] { "D1", "42", "末尾" }, csv.ReadFields());
            Assert.True(csv.EndOfData);
        }
        finally { File.Delete(path); }
    }
}
