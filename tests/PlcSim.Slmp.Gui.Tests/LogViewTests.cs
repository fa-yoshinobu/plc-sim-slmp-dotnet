using System.Reflection;
using System.Text;
using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Protocol;
using PlcSim.Slmp.Gui.ViewModels;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Gui.Tests;

public sealed class LogViewTests
{
    private static void AddFrame(SimulatorRuntime runtime, int number)
    {
        var request = new SlmpRequest(SlmpFrameKind.Frame3E, SlmpDataCode.Binary,
            0, 0, 0xFF, 0x03FF, 0, 0, 0x0401, 0, [], []);
        runtime.FrameLog.Add(new FrameLogEntry(DateTime.Now.AddTicks(number), "test", request,
            [0], 0, $"D{number}", TimeSpan.Zero));
    }

    [Fact]
    public async Task LogImportsAllRetainedFramesAndFiltersFrozenRows()
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        for (var i = 0; i < 600; i++) AddFrame(runtime, i);
        var vm = new LogViewModel(runtime);
        vm.Refresh();
        Assert.Equal(600, vm.Rows.Count);
        vm.Refresh();
        Assert.Equal(600, vm.Rows.Count);
        vm.Paused = true;
        AddFrame(runtime, 600);
        vm.Filter = "D5";
        Assert.Equal(111, vm.Rows.Count);
        Assert.All(vm.Rows, row => Assert.Contains("D5", row.Summary));
        vm.Filter = string.Empty;
        Assert.Equal(600, vm.Rows.Count);
        Assert.DoesNotContain(vm.Rows, row => row.Summary == "D600");
        vm.Paused = false;
        vm.Refresh();
        Assert.Equal(601, vm.Rows.Count);
        Assert.Equal("D600", vm.Rows[0].Summary);
        for (var i = 601; i < 3000; i++) AddFrame(runtime, i);
        vm.Refresh();
        Assert.Equal(2000, vm.Rows.Count);
        Assert.Equal("D2999", vm.Rows[0].Summary);
        Assert.Equal("D1000", vm.Rows[^1].Summary);
        vm.ClearLogCommand.Execute(null);
        vm.Filter = "D";
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task PendingScriptLogsRemainBoundedBeforeRefreshAndClearFully()
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        var vm = new ScriptsViewModel(runtime);
        var writeLog = typeof(PythonScriptHost).GetMethod("WriteLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var i = 0; i < 5000; i++)
            writeLog.Invoke(runtime.Scripts, [new string('L', 1000)]);
        writeLog.Invoke(runtime.Scripts, [new string('X', 100000) + "TAIL"]);
        var pending = (StringBuilder)typeof(ScriptsViewModel)
            .GetField("_pendingLog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        Assert.InRange(pending.Length, 1, 60000);
        Assert.True(pending.Capacity <= 120000);
        vm.Refresh();
        Assert.InRange(vm.LogText.Length, 1, 60000);
        Assert.EndsWith("TAIL" + Environment.NewLine, vm.LogText);
        writeLog.Invoke(runtime.Scripts, ["old pending"]);
        vm.ClearLogCommand.Execute(null);
        vm.Refresh();
        Assert.Empty(vm.LogText);
    }
}
