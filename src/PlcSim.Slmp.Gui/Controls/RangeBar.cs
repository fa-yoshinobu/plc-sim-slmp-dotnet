using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace PlcSim.Slmp.Gui.Controls;

/// <summary>領域全体のうち表示中の位置（つまみ）と書込み位置（目盛り）を示す細い帯。</summary>
public sealed class RangeBar : FrameworkElement
{
    public static readonly DependencyProperty ThumbStartProperty = DependencyProperty.Register(
        nameof(ThumbStart), typeof(double), typeof(RangeBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThumbSizeProperty = DependencyProperty.Register(
        nameof(ThumbSize), typeof(double), typeof(RangeBar), new FrameworkPropertyMetadata(0.01, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(
        nameof(Marks), typeof(IEnumerable), typeof(RangeBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnMarksChanged));

    public double ThumbStart
    {
        get => (double)GetValue(ThumbStartProperty);
        set => SetValue(ThumbStartProperty, value);
    }

    public double ThumbSize
    {
        get => (double)GetValue(ThumbSizeProperty);
        set => SetValue(ThumbSizeProperty, value);
    }

    public IEnumerable? Marks
    {
        get => (IEnumerable?)GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 12);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var track = Brush("Dads.SurfaceSubtle", Brushes.LightGray);
        var stroke = Brush("Dads.BorderStrong", Brushes.Gray);
        var accent = Brush("Dads.Accent", Brushes.SteelBlue);
        var mark = Brush("Dads.Mark", Brushes.Orange);
        dc.DrawRoundedRectangle(track, new Pen(stroke, 1), new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)), 5, 5);
        var thumbX = Math.Clamp(ThumbStart, 0, 1) * w;
        var thumbW = Math.Max(4, Math.Clamp(ThumbSize, 0, 1) * w);
        var thumbBrush = accent.Clone();
        thumbBrush.Opacity = 0.35;
        dc.DrawRectangle(thumbBrush, null, new Rect(thumbX, 1, Math.Min(thumbW, w - thumbX), Math.Max(0, h - 2)));
        dc.DrawRectangle(accent, null, new Rect(thumbX, 1, 2, Math.Max(0, h - 2)));
        if (Marks is null)
            return;
        foreach (var item in Marks)
        {
            if (item is double f)
                dc.DrawRectangle(mark, null, new Rect(Math.Clamp(f * w, 0, w - 2), 2, 2, Math.Max(0, h - 4)));
        }
    }

    private Brush Brush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static void OnMarksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (RangeBar)d;
        if (e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= bar.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += bar.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
}
