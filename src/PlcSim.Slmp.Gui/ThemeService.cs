using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;

namespace PlcSim.Slmp.Gui;

/// <summary>
/// 表示テーマ（システム / ライト / ダーク）の切替。
/// WPF-UI のテーマを切り替えたあと、DADS 準拠の色辞書（Themes/Dads.Light.xaml / Dads.Dark.xaml）を
/// 最後に重ねて、WPF-UI のコントロールも含めて DADS の色にそろえる。
/// </summary>
public static class ThemeService
{
    private static readonly Uri LightUri = new("pack://application:,,,/Themes/Dads.Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/Themes/Dads.Dark.xaml");
    private static Window? _watched;

    /// <summary>"system" / "light" / "dark" を適用する。</summary>
    public static void Apply(string theme)
    {
        switch (theme)
        {
            case "light":
                Unwatch();
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case "dark":
                Unwatch();
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                if (Application.Current.MainWindow is { } window && _watched is null)
                {
                    SystemThemeWatcher.Watch(window);
                    _watched = window;
                }

                break;
        }

        ApplyDadsPalette(ApplicationThemeManager.GetAppTheme());
    }

    /// <summary>DADS の色辞書を現在のテーマに合わせて差し替える（WPF-UI の辞書より後ろに置く）。</summary>
    public static void ApplyDadsPalette(ApplicationTheme theme)
    {
        var dark = theme is ApplicationTheme.Dark or ApplicationTheme.HighContrast;
        var merged = Application.Current.Resources.MergedDictionaries;
        foreach (var existing in merged.Where(static d => d.Source == LightUri || d.Source == DarkUri).ToList())
            merged.Remove(existing);
        merged.Add(new ResourceDictionary { Source = dark ? DarkUri : LightUri });
        RestoreWindowBackground();
    }

    /// <summary>
    /// テーマ切替で WPF-UI が窓の背景を透明（背景効果用）に変えるため、不透明な背景に戻す。
    /// </summary>
    public static void RestoreWindowBackground()
    {
        if (Application.Current.MainWindow is not { } window)
            return;
        void Restore()
        {
            if (window is Wpf.Ui.Controls.FluentWindow fluent)
                fluent.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
            window.SetResourceReference(Control.BackgroundProperty, "Dads.Background");
        }

        Restore();
        // WPF-UI 側の背景更新が後から走る場合に備えて、描画後にもう一度設定する。
        window.Dispatcher.BeginInvoke(Restore, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static void Unwatch()
    {
        if (_watched is null)
            return;
        SystemThemeWatcher.UnWatch(_watched);
        _watched = null;
    }
}
