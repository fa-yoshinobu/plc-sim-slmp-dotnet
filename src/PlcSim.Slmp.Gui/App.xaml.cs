using System.IO;
using System.Windows;
using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Gui.ViewModels;
using PlcSim.Slmp.Scripting;
using Wpf.Ui.Appearance;

namespace PlcSim.Slmp.Gui;

public partial class App : Application
{
    private SimulatorRuntime? _runtime;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            ReportCrash(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ReportCrash(args.ExceptionObject as Exception);
        ApplicationThemeManager.Changed += (theme, _) => Dispatcher.Invoke(() => ThemeService.ApplyDadsPalette(theme));

        var configPath = ArgValue(e.Args, "--config") ?? FindDefaultConfig();
        SimulatorConfig config;
        try
        {
            config = configPath is not null ? SimulatorConfig.Load(configPath) : new SimulatorConfig();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"設定ファイルを読めません: {configPath}\n{ex.Message}\n既定設定で起動します。", "PLC Sim (SLMP)", MessageBoxButton.OK, MessageBoxImage.Warning);
            config = new SimulatorConfig();
        }

        _runtime = new SimulatorRuntime(config, configPath);
        var main = new MainViewModel(_runtime);
        if (ArgValue(e.Args, "--page") is { } page && main.Navigation.FirstOrDefault(n => n.Key == page) is { } nav)
            main.SelectedNav = nav;
        if (ArgValue(e.Args, "--address") is { } address)
        {
            main.SelectedNav = main.Navigation.First(static n => n.Key == "mem");
            main.Memory.JumpText = address;
            main.Memory.JumpCommand.Execute(null);
        }
        var window = new MainWindow { DataContext = main };
        MainWindow = window;
        // 表示テーマ: 引数 --theme が優先、なければ設定ファイルの gui.theme
        ThemeService.Apply(ArgValue(e.Args, "--theme") ?? config.Gui.Theme);
        window.Show();

        await _runtime.StartAsync();
        _runtime.StartConfiguredScripts();
        main.AfterStart();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_runtime is not null)
            await _runtime.DisposeAsync();
        base.OnExit(e);
    }

    /// <summary>未処理例外を %TEMP%\plc_sim_gui_error.log に記録して表示する。</summary>
    private static void ReportCrash(Exception? ex)
    {
        var path = Path.Combine(Path.GetTempPath(), "plc_sim_gui_error.log");
        try
        {
            File.AppendAllText(path, $"{DateTime.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }

        MessageBox.Show($"予期しないエラーが発生しました。\n{ex?.Message}\n\n詳細: {path}", "PLC Sim (SLMP)", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string? FindDefaultConfig()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(dir);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "config", "default.json");
                if (File.Exists(candidate))
                    return candidate;
                current = current.Parent;
            }
        }

        return null;
    }
}
