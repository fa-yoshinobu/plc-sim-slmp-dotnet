using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using PlcSim.Slmp.Gui.ViewModels;
using Wpf.Ui.Appearance;

namespace PlcSim.Slmp.Gui.Views;

public partial class ScriptsView : UserControl
{
    private bool _replacing;

    public ScriptsView()
    {
        InitializeComponent();
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        ApplyEditorTheme(ApplicationThemeManager.GetAppTheme());
        ApplicationThemeManager.Changed += (theme, _) => Dispatcher.Invoke(() => ApplyEditorTheme(theme));
        Editor.TextChanged += (_, _) =>
        {
            if (!_replacing && DataContext is ScriptsViewModel vm)
                vm.EditorChanged(Editor.Text);
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ScriptsViewModel oldVm)
                oldVm.CodeReplaced -= Replace;
            if (e.NewValue is ScriptsViewModel newVm)
            {
                newVm.CodeReplaced += Replace;
                Replace(newVm.Code);
            }
        };
    }

    /// <summary>
    /// Python の色分けをテーマに合わせる。既定の配色は明るい背景向けなので、
    /// ダークでは各色を明るくして背景とのコントラストを確保する。
    /// </summary>
    private void ApplyEditorTheme(ApplicationTheme theme)
    {
        var definition = HighlightingManager.Instance.GetDefinition("Python");
        var dark = theme == ApplicationTheme.Dark || theme == ApplicationTheme.HighContrast;
        var palette = dark
            ? new Dictionary<string, Color>
            {
                ["Comment"] = Color.FromRgb(0x8F, 0xBF, 0x8F),
                ["String"] = Color.FromRgb(0xE6, 0xB0, 0x86),
                ["MethodCall"] = Color.FromRgb(0xDC, 0xDC, 0xAA),
                ["NumberLiteral"] = Color.FromRgb(0xB5, 0xCE, 0xA8),
                ["Keywords"] = Color.FromRgb(0x7F, 0xB8, 0xF5),
            }
            : new Dictionary<string, Color>
            {
                ["Comment"] = Color.FromRgb(0x2E, 0x7D, 0x32),
                ["String"] = Color.FromRgb(0xA3, 0x15, 0x15),
                ["MethodCall"] = Color.FromRgb(0x79, 0x4E, 0x00),
                ["NumberLiteral"] = Color.FromRgb(0x09, 0x86, 0x58),
                ["Keywords"] = Color.FromRgb(0x00, 0x33, 0xB3),
            };
        foreach (var color in definition.NamedHighlightingColors)
        {
            var key = palette.Keys.FirstOrDefault(k => color.Name.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (key is not null)
                color.Foreground = new SimpleHighlightingBrush(palette[key]);
            else if (dark)
                color.Foreground = new SimpleHighlightingBrush(Color.FromRgb(0xC5, 0x86, 0xC0));
        }

        Editor.SyntaxHighlighting = null;
        Editor.SyntaxHighlighting = definition;
    }

    private void Replace(string code)
    {
        _replacing = true;
        Editor.Text = code;
        _replacing = false;
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
