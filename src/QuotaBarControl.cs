using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CodexQuotaTaskbar
{
    /// <summary>
    /// A small, self-drawn quota indicator. Set RemainingPercent on the UI thread.
    /// RenderAt freezes the clock so previews can reproduce a frame exactly.
    /// </summary>
    public sealed class QuotaBarControl : FrameworkElement
    {
        private const int ParticleCount = 40;
        private const int AlphaSteps = 24;
        private const double FramesPerSecond = 30.0;
        private const double ParticleSpeedMultiplier = 4.0;
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Particle[] _particles = new Particle[ParticleCount];
        private readonly Brush[] _whiteDots = new Brush[AlphaSteps];
        private readonly Brush[] _pinkDots = new Brush[AlphaSteps];
        private readonly Brush[] _glows = new Brush[AlphaSteps];
        private readonly Brush[] _shadowRings = new Brush[4];
        private readonly LinearGradientBrush _fillBrush;
        private readonly Brush _trackBrush;
        private readonly Brush _thumbBrush;
        private readonly Brush _labelBrush;
        private readonly Brush _staleLabelBrush;
        private readonly Pen _trackOutline;
        private readonly Pen _thumbOutline;
        private readonly Typeface _typeface;
        private readonly Color[] _referenceStops;
        private double _remainingPercent = double.NaN;
        private bool _isStale;
        private bool _animationRequested = true;
        private bool _manualClock;
        private bool _subscribed;
        private double _clockAnchor;
        private double _seconds;
        private long _lastFrame = -1;
        private Rect _track;
        private Point _thumbCenter;
        private double _thumbRadius;
        private double _geometryWidth = -1;
        private double _geometryHeight = -1;
        private double _geometryPercent = double.NaN;
        private RectangleGeometry _trackGeometry;
        private RectangleGeometry _fillGeometry;
        private FormattedText _label;
        private string _labelText;
        private double _labelSize;
        private double _labelPixelsPerDip;
        private bool _labelStale;

        private struct Particle
        {
            public double X;
            public double Y;
            public double Phase;
            public double Speed;
            public double Radius;
            public bool Pink;
        }

        public QuotaBarControl()
        {
            SnapsToDevicePixels = false;
            UseLayoutRounding = true;
            Focusable = false;
            ClipToBounds = true;

            _trackBrush = Solid(59, 59, 65, 255);
            _thumbBrush = Solid(253, 253, 255, 255);
            _labelBrush = Solid(93, 51, 123, 255);
            _staleLabelBrush = Solid(123, 113, 132, 255);
            _trackOutline = FrozenPen(Solid(103, 98, 115, 90), 0.65);
            _thumbOutline = FrozenPen(Solid(214, 210, 220, 230), 0.8);
            _typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                FontWeights.SemiBold, FontStretches.Normal);

            _referenceStops = new Color[]
            {
                Color.FromRgb(144, 31, 139),
                Color.FromRgb(161, 63, 202),
                Color.FromRgb(179, 83, 245),
                Color.FromRgb(180, 91, 253),
                Color.FromRgb(158, 64, 229)
            };
            _fillBrush = new LinearGradientBrush();
            _fillBrush.StartPoint = new Point(0, 0.3);
            _fillBrush.EndPoint = new Point(1, 0.7);
            _fillBrush.ColorInterpolationMode = ColorInterpolationMode.SRgbLinearInterpolation;
            for (int i = 0; i < _referenceStops.Length; i++)
                _fillBrush.GradientStops.Add(new GradientStop(_referenceStops[i],
                    i / (double)(_referenceStops.Length - 1)));

            for (int i = 0; i < AlphaSteps; i++)
            {
                byte alpha = (byte)Math.Round(i * 255.0 / (AlphaSteps - 1));
                _whiteDots[i] = Solid(255, 249, 255, alpha);
                _pinkDots[i] = Solid(255, 197, 247, alpha);
                _glows[i] = Solid(247, 190, 255, (byte)(alpha * 0.12));
            }
            for (int i = 0; i < _shadowRings.Length; i++)
                _shadowRings[i] = Solid(8, 5, 18, (byte)(5 + i * 6));

            Random random = new Random(140119);
            for (int i = 0; i < ParticleCount; i++)
            {
                // Stratified positions keep small taskbar-sized bars evenly populated.
                _particles[i] = new Particle
                {
                    X = (i + random.NextDouble()) / ParticleCount,
                    Y = 0.10 + random.NextDouble() * 0.80,
                    Phase = random.NextDouble() * Math.PI * 2.0,
                    Speed = 0.45 + random.NextDouble() * 0.75,
                    Radius = 0.42 + random.NextDouble() * 0.62,
                    Pink = i % 5 == 2
                };
            }

            Loaded += delegate { UpdateAnimationSubscription(); };
            Unloaded += delegate { StopRendering(); };
            IsVisibleChanged += delegate { UpdateAnimationSubscription(); };
        }

        /// <summary>Remaining quota from 0 through 100; NaN means unknown.</summary>
        public double RemainingPercent
        {
            get { return _remainingPercent; }
            set
            {
                VerifyAccess();
                double next = double.IsNaN(value) ? double.NaN : Math.Max(0, Math.Min(100, value));
                if (SameNumber(next, _remainingPercent)) return;
                _remainingPercent = next;
                InvalidateVisual();
                UpdateAnimationSubscription();
            }
        }

        /// <summary>Mute the fill when the displayed value is an old successful reading.</summary>
        public bool IsStale
        {
            get { return _isStale; }
            set
            {
                VerifyAccess();
                if (_isStale == value) return;
                _isStale = value;
                InvalidateVisual();
            }
        }

        /// <summary>Pause/resume animation without losing the current animation phase.</summary>
        public void SetAnimationActive(bool active)
        {
            VerifyAccess();
            _animationRequested = active;
            if (active) _manualClock = false;
            UpdateAnimationSubscription();
        }

        /// <summary>
        /// Stop real-time rendering and request a deterministic frame at the supplied time.
        /// Call SetAnimationActive(true) to resume live animation after preview capture.
        /// </summary>
        public void RenderAt(double seconds)
        {
            VerifyAccess();
            StopRendering();
            _manualClock = true;
            _seconds = double.IsNaN(seconds) || double.IsInfinity(seconds) ? 0 : Math.Max(0, seconds);
            _clockAnchor = _seconds;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(248, 30);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (ActualWidth < 30 || ActualHeight < 12) return;
            UpdateGeometry();

            DrawingContext dc = drawingContext;
            dc.DrawGeometry(_trackBrush, _trackOutline, _trackGeometry);
            if (_fillGeometry != null)
            {
                UpdateFieldColors(_seconds);
                dc.PushClip(_trackGeometry);
                dc.PushClip(_fillGeometry);
                if (_isStale) dc.PushOpacity(0.58);
                // Drawing the whole track under a fill clip keeps the gradient's scale stable
                // as quota changes; the moving white knob alone determines the exposed fill.
                dc.DrawRectangle(_fillBrush, null, _track);
                DrawParticles(dc, _seconds);
                if (_isStale) dc.Pop();
                dc.Pop();
                dc.Pop();
            }

            // Four inexpensive concentric rings produce a soft shadow without a live Effect.
            for (int i = 0; i < _shadowRings.Length; i++)
            {
                double expansion = 1.7 - i * 0.40;
                dc.DrawEllipse(_shadowRings[i], null,
                    new Point(_thumbCenter.X, _thumbCenter.Y + 0.75),
                    _thumbRadius + expansion, _thumbRadius + expansion);
            }
            dc.DrawEllipse(_thumbBrush, _thumbOutline, _thumbCenter, _thumbRadius, _thumbRadius);
            UpdateLabel();
            if (_label != null)
            {
                dc.DrawText(_label, new Point(_thumbCenter.X - _label.Width / 2,
                    _thumbCenter.Y - _label.Height / 2 - 0.1));
            }
        }

        private void UpdateGeometry()
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width == _geometryWidth && height == _geometryHeight &&
                SameNumber(_remainingPercent, _geometryPercent)) return;
            _geometryWidth = width;
            _geometryHeight = height;
            _geometryPercent = _remainingPercent;
            double thumbDiameter = Math.Min(26, height - 4);
            _thumbRadius = thumbDiameter / 2;
            double trackHeight = Math.Min(22, height - 8);
            double inset = 2;
            _track = new Rect(inset, (height - trackHeight) / 2,
                Math.Max(0, width - 2 * inset), trackHeight);
            double radius = trackHeight / 2;
            _trackGeometry = new RectangleGeometry(_track, radius, radius);
            _trackGeometry.Freeze();
            double fraction = double.IsNaN(_remainingPercent) ? 0 : _remainingPercent / 100;
            double left = inset + _thumbRadius;
            double right = width - inset - _thumbRadius;
            _thumbCenter = new Point(left + (right - left) * fraction, height / 2);
            _fillGeometry = null;
            if (fraction > 0)
            {
                double fillWidth = Math.Min(_track.Width, _thumbCenter.X - _track.X + radius);
                _fillGeometry = new RectangleGeometry(new Rect(_track.X, _track.Y,
                    fillWidth, _track.Height), radius, radius);
                _fillGeometry.Freeze();
            }
        }

        private void UpdateLabel()
        {
            string text = double.IsNaN(_remainingPercent) ? "—" :
                Math.Round(_remainingPercent, MidpointRounding.AwayFromZero)
                    .ToString(CultureInfo.InvariantCulture) + "%";
            double fontSize = (text.Length >= 4 ? 8.5 : 10.0) * Math.Min(1, _thumbRadius / 13);
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_label != null && text == _labelText && _labelSize == fontSize &&
                _labelStale == _isStale && _labelPixelsPerDip == pixelsPerDip)
                return;
            _labelText = text;
            _labelSize = fontSize;
            _labelPixelsPerDip = pixelsPerDip;
            _labelStale = _isStale;
            _label = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _typeface, fontSize,
                _isStale ? _staleLabelBrush : _labelBrush, pixelsPerDip);
            _label.TextAlignment = TextAlignment.Left;
        }

        private void UpdateFieldColors(double time)
        {
            // Subtle independent color movement suggests the native slider's purple field.
            // Cached brush/stops avoid allocating a bitmap or a full visual tree per frame.
            for (int i = 0; i < _referenceStops.Length; i++)
            {
                Color reference = _referenceStops[i];
                double pulse = Math.Sin(time * (0.34 + i * 0.065) + i * 1.45);
                Color next = Color.FromRgb(
                    Channel(reference.R + pulse * 5),
                    Channel(reference.G + Math.Cos(time * 0.42 + i) * 5),
                    Channel(reference.B + pulse * 3));
                if (_fillBrush.GradientStops[i].Color != next)
                    _fillBrush.GradientStops[i].Color = next;
            }
        }

        private void DrawParticles(DrawingContext dc, double time)
        {
            // Speed up particle drift and twinkle together without changing field colors.
            time *= ParticleSpeedMultiplier;
            double scale = Math.Min(1.3, _track.Height / 22);
            for (int i = 0; i < _particles.Length; i++)
            {
                Particle p = _particles[i];
                double u = Fraction(p.X + time * 0.012 * p.Speed);
                double x = _track.X + u * _track.Width + Math.Sin(time * 0.73 + p.Phase) * 1.5;
                double v = p.Y + Math.Sin(time * p.Speed + p.Phase) * 0.09;
                double y = _track.Y + v * _track.Height;
                double twinkle = 0.5 + 0.5 * Math.Sin(time * (1.5 + p.Speed) + p.Phase);
                double alpha = 0.22 + 0.67 * twinkle * twinkle;
                int index = Math.Max(1, Math.Min(AlphaSteps - 1,
                    (int)Math.Round(alpha * (AlphaSteps - 1))));
                double radius = p.Radius * scale;
                Point center = new Point(x, y);
                dc.DrawEllipse(_glows[index], null, center, radius * 2.8, radius * 2.8);
                dc.DrawEllipse(p.Pink ? _pinkDots[index] : _whiteDots[index], null,
                    center, radius, radius);
            }
        }

        private void UpdateAnimationSubscription()
        {
            bool shouldRun = _animationRequested && !_manualClock && IsLoaded && IsVisible &&
                !double.IsNaN(_remainingPercent) && _remainingPercent > 0;
            if (!shouldRun)
            {
                StopRendering();
                return;
            }
            if (_subscribed) return;
            _clockAnchor = _seconds;
            _clock.Restart();
            _lastFrame = -1;
            CompositionTarget.Rendering += OnCompositionRendering;
            _subscribed = true;
        }

        private void StopRendering()
        {
            if (_subscribed)
            {
                _seconds = _clockAnchor + _clock.Elapsed.TotalSeconds;
                CompositionTarget.Rendering -= OnCompositionRendering;
                _subscribed = false;
            }
            _clock.Stop();
        }

        private void OnCompositionRendering(object sender, EventArgs e)
        {
            double seconds = _clockAnchor + _clock.Elapsed.TotalSeconds;
            long frame = (long)Math.Floor(seconds * FramesPerSecond);
            if (frame == _lastFrame) return;
            _lastFrame = frame;
            _seconds = seconds;
            InvalidateVisual();
        }

        private static double Fraction(double value)
        {
            return value - Math.Floor(value);
        }

        private static byte Channel(double value)
        {
            return (byte)Math.Max(0, Math.Min(255, Math.Round(value)));
        }

        private static bool SameNumber(double a, double b)
        {
            return a == b || (double.IsNaN(a) && double.IsNaN(b));
        }

        private static Brush Solid(byte red, byte green, byte blue, byte alpha)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(Brush brush, double thickness)
        {
            Pen pen = new Pen(brush, thickness);
            pen.Freeze();
            return pen;
        }
    }
}
