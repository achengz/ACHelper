using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace LolMatchAssistant;

public sealed class ReviewTagStrip : Panel
{
    public static readonly DependencyProperty TagsProperty = DependencyProperty.Register(nameof(Tags), typeof(IEnumerable),
        typeof(ReviewTagStrip), new FrameworkPropertyMetadata(null, Rebuild));
    public static readonly DependencyProperty BadgeTemplateProperty = DependencyProperty.Register(nameof(BadgeTemplate),
        typeof(DataTemplate), typeof(ReviewTagStrip), new FrameworkPropertyMetadata(null, Rebuild));

    public IEnumerable? Tags { get => (IEnumerable?)GetValue(TagsProperty); set => SetValue(TagsProperty, value); }
    public DataTemplate? BadgeTemplate { get => (DataTemplate?)GetValue(BadgeTemplateProperty); set => SetValue(BadgeTemplateProperty, value); }

    private ReviewTag[] _tags = [];
    private ContentPresenter? _overflow;
    private int _visible;
    private int _overflowStart = -1;

    private static void Rebuild(DependencyObject owner, DependencyPropertyChangedEventArgs _) =>
        ((ReviewTagStrip)owner).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        _tags = Tags?.OfType<ReviewTag>().ToArray() ?? [];
        _overflow = null;
        _overflowStart = -1;
        if (_tags.Length == 0 || BadgeTemplate is null) return;
        foreach (var tag in _tags) Children.Add(new ContentPresenter { Content = tag, ContentTemplate = BadgeTemplate });
        _overflow = new ContentPresenter { ContentTemplate = BadgeTemplate };
        Children.Add(_overflow);
    }

    protected override Size MeasureOverride(Size area)
    {
        if (_overflow is null) return new Size();
        var unconstrained = new Size(double.PositiveInfinity, area.Height);
        var widths = new double[_tags.Length];
        var total = 0d;
        var height = 0d;
        for (var index = 0; index < _tags.Length; index++)
        {
            var child = Children[index];
            child.Measure(unconstrained);
            widths[index] = child.DesiredSize.Width;
            total += widths[index];
            height = Math.Max(height, child.DesiredSize.Height);
        }
        _visible = _tags.Length;
        if (total <= area.Width) return new Size(total, height);

        const double overflowSpace = 38;
        while (_visible > 0 && total + overflowSpace > area.Width) total -= widths[--_visible];
        if (_overflowStart != _visible)
        {
            _overflowStart = _visible;
            var details = string.Join("\n", _tags.Skip(_visible).Select(tag => $"{tag.Label} · {tag.Reason.ReplaceLineEndings(" ")}"));
            _overflow.Content = new ReviewTag($"+{_tags.Length - _visible}", details, "overflow");
        }
        _overflow.Measure(unconstrained);
        return new Size(Math.Min(area.Width, total + _overflow.DesiredSize.Width), Math.Max(height, _overflow.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size area)
    {
        if (_overflow is null) return area;
        var x = 0d;
        for (var index = 0; index < _tags.Length; index++)
        {
            var child = Children[index];
            if (index >= _visible)
            {
                child.Arrange(new Rect(-child.DesiredSize.Width - 1, 0, child.DesiredSize.Width, area.Height));
                continue;
            }
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, area.Height));
            x += child.DesiredSize.Width;
        }
        _overflow.Arrange(_visible < _tags.Length
            ? new Rect(x, 0, _overflow.DesiredSize.Width, area.Height)
            : new Rect(-_overflow.DesiredSize.Width - 1, 0, _overflow.DesiredSize.Width, area.Height));
        return area;
    }
}
