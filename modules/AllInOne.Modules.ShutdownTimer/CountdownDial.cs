using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AllInOne.Ui;

namespace AllInOne.Modules.ShutdownTimer;

/// <summary>Вид обратного отсчёта.</summary>
public enum CountdownFace
{
    /// <summary>Кольцо, которое убывает по часовой стрелке.</summary>
    Ring,

    /// <summary>Только цифры.</summary>
    Digits,

    /// <summary>Циферблат с сектором оставшегося времени, как у механического таймера.</summary>
    Analog,
}

/// <summary>
/// Обратный отсчёт до срабатывания таймера. Рисуется сам (OnRender) и сам обновляется
/// ~30 раз в секунду, пока виден на экране, — кольцо и сектор убывают плавно.
/// </summary>
public sealed class CountdownDial : FrameworkElement
{
    private static readonly Color Violet = Color.FromRgb(0xA7, 0x7C, 0xFF);

    private DateTime _target;
    private DateTime _start;
    private CountdownFace _face;

    public CountdownDial(DateTime target, DateTime start, CountdownFace face, double size = 220)
    {
        _target = target;
        _start = start < target ? start : target.AddMinutes(-1);
        _face = face;
        Width = size;
        Height = face == CountdownFace.Digits ? size * 0.42 : size;
        SnapsToDevicePixels = true;

        // Кадр по отрисовке экрана, пока элемент на экране (CompositionTarget не держит страницу после ухода).
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    public void Set(DateTime target, DateTime start, CountdownFace face)
    {
        _target = target;
        _start = start < target ? start : target.AddMinutes(-1);
        if (_face != face)
        {
            _face = face;
            Height = face == CountdownFace.Digits ? Width * 0.42 : Width;
        }
        InvalidateVisual();
    }

    private TimeSpan _lastDrawn = TimeSpan.MinValue;

    private void OnFrame(object? sender, EventArgs e)
    {
        // Цифрам хватает раза в секунду, кольцу и сектору — ~30 кадров.
        var left = Left;
        var step = _face == CountdownFace.Digits ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(33);
        if (_lastDrawn != TimeSpan.MinValue && Math.Abs((left - _lastDrawn).Ticks) < step.Ticks) return;
        _lastDrawn = left;
        InvalidateVisual();
    }

    private TimeSpan Left
    {
        get
        {
            var left = _target - DateTime.Now;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    /// <summary>Доля оставшегося времени от всего интервала (1 — только что взведён).</summary>
    private double Fraction
    {
        get
        {
            var total = (_target - _start).TotalSeconds;
            return total <= 0 ? 0 : Math.Clamp(Left.TotalSeconds / total, 0, 1);
        }
    }

    /// <summary>Последняя минута — тёплый цвет вместо синего.</summary>
    private bool Urgent => Left <= TimeSpan.FromMinutes(1);

    protected override void OnRender(DrawingContext dc)
    {
        switch (_face)
        {
            case CountdownFace.Ring: DrawRing(dc); break;
            case CountdownFace.Digits: DrawDigits(dc); break;
            case CountdownFace.Analog: DrawAnalog(dc); break;
        }
    }

    // ---------- кольцо ----------

    private void DrawRing(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var thickness = size * 0.075;
        var radius = size / 2 - thickness;

        // Дорожка.
        dc.DrawEllipse(null, new Pen(UiKit.Brush("CardStroke"), thickness), center, radius, radius);

        // Дуга оставшегося времени: от акцента к фиолетовому.
        var (from, to) = ArcColors();
        var fraction = Fraction;
        if (fraction > 0.0005)
        {
            var arc = Arc(center, radius, fraction);
            var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
            var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, arc);

            // Точка на конце дуги.
            var end = PointAt(center, radius, fraction);
            dc.DrawEllipse(Brushes.White, null, end, thickness * 0.22, thickness * 0.22);
        }

        DrawCenterText(dc, center, size, Format(Left), "осталось");
    }

    // ---------- цифры ----------

    private void DrawDigits(DrawingContext dc)
    {
        var text = Format(Left);
        var brush = Urgent ? UiKit.Brush("Warn") : UiKit.Brush("Text");
        var ft = Text(text, ActualHeight * 0.62, brush, FontWeights.Light, mono: true);
        // Ширина по всему элементу: длинное «12:34:56» ужимаем.
        if (ft.Width > ActualWidth) ft.SetFontSize(ActualHeight * 0.62 * ActualWidth / ft.Width);
        dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2 - ActualHeight * 0.06));

        // Тонкая полоса прогресса под цифрами.
        var barY = ActualHeight - 4;
        var bar = new Rect(ActualWidth * 0.1, barY, ActualWidth * 0.8, 3);
        dc.DrawRoundedRectangle(UiKit.Brush("CardStroke"), null, bar, 1.5, 1.5);
        var (from, to) = ArcColors();
        var filled = new Rect(bar.X, bar.Y, bar.Width * Fraction, bar.Height);
        if (filled.Width > 0)
            dc.DrawRoundedRectangle(new LinearGradientBrush(from, to, 0), null, filled, 1.5, 1.5);
    }

    // ---------- аналоговый ----------

    private void DrawAnalog(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - 2;

        // Шкала: до часа — минуты, до 12 часов — часы, дальше — сутки.
        var totalMinutes = (_target - _start).TotalMinutes;
        var (scaleMinutes, labels, labelStep) = totalMinutes <= 60 ? (60.0, 12, 5)
            : totalMinutes <= 12 * 60 ? (12 * 60.0, 12, 1)
            : (24 * 60.0, 12, 2);

        // Корпус.
        dc.DrawEllipse(UiKit.Brush("Control"), new Pen(UiKit.Brush("ControlStroke"), 1.5), center, radius, radius);

        // Сектор оставшегося времени.
        var (from, to) = ArcColors();
        var fraction = Math.Clamp(Left.TotalMinutes / scaleMinutes, 0, 1);
        var sectorRadius = radius * 0.78;
        if (fraction > 0.0005)
        {
            var sector = Sector(center, sectorRadius, fraction);
            var fill = new RadialGradientBrush(Color.FromArgb(200, to.R, to.G, to.B), Color.FromArgb(230, from.R, from.G, from.B));
            dc.DrawGeometry(fill, null, sector);
        }

        // Деления: 60 мелких, каждое пятое — длинное.
        var minor = new Pen(UiKit.Brush("SubText"), Math.Max(1, size / 220));
        var major = new Pen(UiKit.Brush("Text"), Math.Max(2, size / 90)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (var i = 0; i < 60; i++)
        {
            var isMajor = i % 5 == 0;
            var outer = PointAt(center, radius * 0.95, i / 60.0);
            var inner = PointAt(center, radius * (isMajor ? 0.84 : 0.89), i / 60.0);
            dc.DrawLine(isMajor ? major : minor, inner, outer);
        }

        // Подписи шкалы.
        for (var i = 0; i < labels; i++)
        {
            var value = i * labelStep;
            var label = Text(value.ToString(CultureInfo.InvariantCulture), size * 0.065, UiKit.Brush("SubText"), FontWeights.Normal);
            var p = PointAt(center, radius * 0.7, i / (double)labels);
            if (fraction > i / (double)labels || i == 0 && fraction > 0) label.SetForegroundBrush(Brushes.White);
            dc.DrawText(label, new Point(p.X - label.Width / 2, p.Y - label.Height / 2));
        }

        // Стрелка на границе сектора и ось.
        var hand = new Pen(Brushes.White, Math.Max(2, size / 70)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(hand, center, PointAt(center, radius * 0.82, fraction));
        dc.DrawEllipse(UiKit.Brush("Card"), new Pen(Brushes.White, Math.Max(2, size / 90)), center, size * 0.045, size * 0.045);

        // Точное время — под осью.
        var digits = Text(Format(Left), size * 0.075, UiKit.Brush("Text"), FontWeights.SemiBold, mono: true);
        var y = center.Y + radius * 0.28;
        var pad = new Rect(center.X - digits.Width / 2 - 8, y - 3, digits.Width + 16, digits.Height + 6);
        dc.DrawRoundedRectangle(UiKit.Brush("Card"), null, pad, pad.Height / 2, pad.Height / 2);
        dc.DrawText(digits, new Point(center.X - digits.Width / 2, y));
    }

    // ---------- общее ----------

    private (Color From, Color To) ArcColors()
    {
        if (Urgent)
        {
            var warn = ((SolidColorBrush)UiKit.Brush("Warn")).Color;
            var bad = ((SolidColorBrush)UiKit.Brush("Bad")).Color;
            return (warn, bad);
        }
        return (((SolidColorBrush)UiKit.Brush("Accent")).Color, Violet);
    }

    private void DrawCenterText(DrawingContext dc, Point center, double size, string main, string caption)
    {
        var big = Text(main, size * (main.Length > 5 ? 0.15 : 0.19), UiKit.Brush("Text"), FontWeights.SemiBold, mono: true);
        var small = Text(caption, size * 0.065, UiKit.Brush("SubText"), FontWeights.Normal);
        var total = big.Height + small.Height;
        dc.DrawText(big, new Point(center.X - big.Width / 2, center.Y - total / 2));
        dc.DrawText(small, new Point(center.X - small.Width / 2, center.Y - total / 2 + big.Height));
    }

    /// <summary>«1:05:09» или «12:30» (минуты:секунды).</summary>
    internal static string Format(TimeSpan left)
    {
        var h = (int)left.TotalHours;
        return h > 0
            ? $"{h}:{left.Minutes:00}:{left.Seconds:00}"
            : $"{left.Minutes}:{left.Seconds:00}";
    }

    private FormattedText Text(string text, double size, Brush brush, FontWeight weight, bool mono = false)
    {
        // Цифры у Segoe UI одинаковой ширины — отсчёт не «прыгает».
        var family = new FontFamily(mono ? "Segoe UI Variable Display, Segoe UI" : "Segoe UI Variable Text, Segoe UI");
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), Math.Max(1, size), brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft;
    }

    /// <summary>Точка окружности: доля 0 — верх, дальше по часовой стрелке.</summary>
    private static Point PointAt(Point center, double radius, double fraction)
    {
        var angle = fraction * 2 * Math.PI - Math.PI / 2;
        return new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
    }

    private static Geometry Arc(Point center, double radius, double fraction)
    {
        if (fraction >= 0.9999) return new EllipseGeometry(center, radius, radius);
        var start = PointAt(center, radius, 0);
        var end = PointAt(center, radius, fraction);
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

    private static Geometry Sector(Point center, double radius, double fraction)
    {
        if (fraction >= 0.9999) return new EllipseGeometry(center, radius, radius);
        var figure = new PathFigure { StartPoint = center, IsClosed = true };
        figure.Segments.Add(new LineSegment(PointAt(center, radius, 0), true));
        figure.Segments.Add(new ArcSegment(PointAt(center, radius, fraction), new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }
}
