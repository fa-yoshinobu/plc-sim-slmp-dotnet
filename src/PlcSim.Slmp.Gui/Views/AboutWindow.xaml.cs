using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using Wpf.Ui.Controls;

namespace PlcSim.Slmp.Gui.Views;

/// <summary>バージョン情報（製品情報・使用ライブラリ・アプリのライセンス）。</summary>
public partial class AboutWindow : FluentWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        var app = typeof(AboutWindow).Assembly;
        ProductName = app.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "PLC Sim (SLMP)";
        Version = VersionOf(app);
        Company = app.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "FA Labo";
        Copyright = app.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright (c) 2026 FA Labo";
        Libraries =
        [
            new(ProductName, Version, "MIT", "アプリ本体・内部モジュール"),
            new("WPF-UI", VersionOf("Wpf.Ui"), "MIT", "画面の部品・ウィンドウ"),
            new("CommunityToolkit.Mvvm", VersionOf("CommunityToolkit.Mvvm"), "MIT", "画面とデータの連携"),
            new("AvalonEdit", VersionOf("ICSharpCode.AvalonEdit"), "MIT", "スクリプトエディタ"),
            new("DADS デザイントークン", "2.0.1", "MIT", "画面の配色（© デジタル庁）"),
            new("ASP.NET Core", Environment.Version.ToString(), "MIT", "HTTP 制御 API"),
            new(".NET Runtime", Environment.Version.ToString(), "MIT", "アプリの実行環境"),
        ];
        using var stream = app.GetManifestResourceStream("PlcSim.Slmp.Gui.LICENSE");
        using var reader = stream is null ? null : new StreamReader(stream);
        LicenseText = reader?.ReadToEnd() ?? "ライセンス本文を読み込めませんでした。";
        DataContext = this;
    }

    public string ProductName { get; }

    public string Version { get; }

    public string Company { get; }

    public string Copyright { get; }

    public string LicenseText { get; }

    public IReadOnlyList<LibraryInfo> Libraries { get; }

    private static string VersionOf(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? assembly.GetName().Version?.ToString() ?? "不明" : info.Split('+')[0];
    }

    private static string VersionOf(string name)
    {
        try
        {
            return VersionOf(Assembly.Load(new AssemblyName(name)));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return "不明";
        }
    }

    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            System.Windows.MessageBox.Show(this, "ブラウザーを起動できませんでした。", "リンクを開けません", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    public sealed record LibraryInfo(string Name, string Version, string License, string Purpose);
}
