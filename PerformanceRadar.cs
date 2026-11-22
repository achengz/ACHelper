using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LolMatchAssistant;

internal record RadarAxis(string Name, string Value, string TeamAverage, double PlayerRatio, double TeamRatio);

internal record RadarSnapshot(RadarAxis[] Axes)
{
    public static RadarSnapshot? Build(Match match, Participant player)
    {
        if (player.Team <= 0 || player.Combat is null) return null;
        var team = match.Players.Where(member => member.Team == player.Team && member.Combat is not null).ToArray();
        if (team.Length < 2) return null;
        var totalKills = team.All(member => member.Combat!.Kills is >= 0)
            ? team.Sum(member => member.Combat!.Kills!.Value) : 0;
        var axes = new List<RadarAxis>();
        Add("伤害", p => p.Combat!.ChampionDamage);
        Add("承伤", p => p.Combat!.DamageTaken);
        Add("金币", p => p.Combat!.Gold);
        Add("补刀", p => p.Combat!.Minions.HasValue ? p.Cs : null);
        Add("KDA", p => p.Combat is { Kills: { } k, Assists: { } a, Deaths: { } d } ? (k + a) / (double)Math.Max(1, d) : null, "F2");
        Add("参团率", p => totalKills > 0 && p.Combat is { Kills: { } k, Assists: { } a } ? (k + a) / (double)totalKills : null, "P0");
        Add("治疗", p => p.Combat!.TotalHealing);
        return axes.Count >= 3 ? new([.. axes]) : null;

        void Add(string name, Func<Participant, double?> measure, string format = "N0")
        {
            if (measure(player) is not >= 0) return;
            var value = measure(player)!.Value;
            var samples = team.Select(measure).OfType<double>().Where(sample => sample >= 0).ToArray();
            if (samples.Length < 2) return;
            var scale = Math.Max(1e-9, samples.Max());
            var average = samples.Average();
            axes.Add(new(name, value.ToString(format), average.ToString(format), value / scale, average / scale));
        }
    }
}

public sealed class PerformanceRadar : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(object),
        typeof(PerformanceRadar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public object? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (Data is not RadarSnapshot { Axes.Length: >= 3 } snapshot) return;
        var axes = snapshot.Axes;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var ink = (Brush)FindResource("Ink");
        var grid = new Pen((Brush)FindResource("Stroke"), 1);
        var team = (Brush)FindResource("BlueInk");
        var personal = (Brush)FindResource("RadarPersonal");
        var directions = Enumerable.Range(0, axes.Length)
            .Select(index => Math.Tau * index / axes.Length - Math.PI / 2)
            .Select(angle => new Vector(Math.Cos(angle), Math.Sin(angle))).ToArray();
        const double radius = 79;

        for (var ring = 1; ring <= 4; ring++)
            DrawPolygon(directions.Select(direction => center + direction * (radius * ring / 4)).ToArray(), null, grid);
        foreach (var direction in directions) drawing.DrawLine(grid, center, center + direction * radius);
        DrawSeries(axes.Select(axis => axis.TeamRatio).ToArray(), team, null);
        DrawSeries(axes.Select(axis => axis.PlayerRatio).ToArray(), personal, WithOpacity(personal, .20));
        for (var index = 0; index < axes.Length; index++)
        {
            var axis = axes[index];
            var position = center + directions[index] * 105;
            var text = new FormattedText($"{axis.Name} {axis.Value}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 10, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point(
                Math.Clamp(position.X - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width)),
                Math.Clamp(position.Y - text.Height / 2, 0, Math.Max(0, ActualHeight - text.Height))));
        }

        void DrawSeries(double[] ratios, Brush stroke, Brush? fill)
        {
            var points = ratios.Select((ratio, index) => center + directions[index] * (radius * Math.Clamp(ratio, 0, 1))).ToArray();
            DrawPolygon(points, fill, new Pen(stroke, 1.7));
            foreach (var point in points) drawing.DrawEllipse(stroke, null, point, 2.4, 2.4);
        }

        void DrawPolygon(Point[] points, Brush? fill, Pen pen)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(points[0], fill is not null, true);
                path.PolyLineTo(points.Skip(1).ToArray(), true, false);
            }
            drawing.DrawGeometry(fill, pen, geometry);
        }
    }

    private static Brush WithOpacity(Brush source, double opacity)
    {
        var brush = source.CloneCurrentValue();
        brush.Opacity = opacity;
        return brush;
    }
}

internal record IconHint(string Name, string Description);
