using System.Windows;
using System.Windows.Threading;
using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Gui.ViewModels;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Gui.Tests;

public sealed class ConfigDraftTests
{
    [Fact]
    public async Task MainWindowKeepsOtherFormsDraftsAndResetsOnlyAppliedForm()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
                    var main = new MainViewModel(runtime);
                    Assert.False(main.Labels.HasPendingChanges);
                    Assert.False(main.DeviceRange.HasPendingChanges);
                    main.Labels.AddGlobalCommand.Execute(null);
                    var label = main.Labels.Globals.Last().Name;
                    main.DeviceRange.Rows.Single(r => r.Family == "D").PointsText = "100";
                    main.DeviceRange.NewUnit = "2";
                    main.DeviceRange.AddUnitCommand.Execute(null);
                    await main.Settings.ApplyCommand.ExecuteAsync(null);
                    Assert.Contains(main.Labels.Globals, g => g.Name == label);
                    Assert.Equal("100", main.DeviceRange.Rows.Single(r => r.Family == "D").PointsText);
                    Assert.Contains("U2", main.DeviceRange.Units);
                    Assert.True(main.Labels.HasPendingChanges);
                    Assert.True(main.DeviceRange.HasPendingChanges);
                    await main.DeviceRange.ApplyCommand.ExecuteAsync(null);
                    Assert.False(main.DeviceRange.HasPendingChanges);
                    Assert.True(main.Labels.HasPendingChanges);
                    Assert.Equal(100u, runtime.Plc.Profile.PointsOf("D"));
                    await main.Labels.ApplyCommand.ExecuteAsync(null);
                    Assert.False(main.Labels.HasPendingChanges);
                    Assert.Contains(runtime.Plc.Labels.Definitions.Globals, g => g.Name == label);
                    main.DeviceRange.Rows.Single(r => r.Family == "D").PointsText = "invalid";
                    main.Labels.Globals.Last().LengthText = "invalid";
                    await main.Settings.ApplyCommand.ExecuteAsync(null);
                    Assert.Equal("invalid", main.DeviceRange.Rows.Single(r => r.Family == "D").PointsText);
                    Assert.Equal("invalid", main.Labels.Globals.Last().LengthText);
                    done.SetResult();
                }
                catch (Exception ex) { done.SetException(ex); }
                finally { app.Shutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task DecliningProfileChangeDoesNotRebuildThePlc()
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        var original = runtime.Plc;
        var vm = new SettingsViewModel(runtime);
        vm.ConfirmProfileChange += () => false;
        vm.Profile = vm.Profiles.First(p => p.Id != runtime.Config.Plc.Profile);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Same(original, runtime.Plc);
        Assert.False(vm.Busy);
    }
}
