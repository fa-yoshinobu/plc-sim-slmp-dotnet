using System.IO;
using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Gui.ViewModels;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Gui.Tests;

public sealed class ScriptDraftTests
{
    [Fact]
    public async Task DraftsSurviveSelectionReloadAndRemovalUntilSaved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "plc-sim-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "a.py");
        var second = Path.Combine(directory, "b.py");
        try
        {
            File.WriteAllText(first, "# original A");
            File.WriteAllText(second, "# original B");
            await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
            var a = runtime.Scripts.Add(first);
            var b = runtime.Scripts.Add(second);
            var vm = new ScriptsViewModel(runtime);
            vm.Reload();
            vm.Selected = vm.Scripts.Single(s => s.Script == a);
            vm.EditorChanged("# draft A");
            vm.Selected = vm.Scripts.Single(s => s.Script == b);
            vm.EditorChanged("# draft B");
            vm.Selected = vm.Scripts.Single(s => s.Script == a);
            Assert.Equal("# draft A", vm.Code);
            Assert.True(vm.IsDirty);
            Assert.Equal("# original A", File.ReadAllText(first));
            vm.Reload();
            Assert.Equal("# draft A", vm.Code);
            Assert.True(vm.IsDirty);
            vm.RemoveCommand.Execute(null);
            runtime.Scripts.Add(first);
            vm.Reload();
            vm.Selected = vm.Scripts.Single(s => s.Path == first);
            Assert.Equal("# draft A", vm.Code);
            vm.SaveCommand.Execute(null);
            Assert.Equal("# draft A", File.ReadAllText(first));
            Assert.False(vm.IsDirty);
            vm.Selected = vm.Scripts.Single(s => s.Script == b);
            Assert.Equal("# draft B", vm.Code);
            Assert.True(vm.IsDirty);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
            Directory.Delete(directory);
        }
    }
}
