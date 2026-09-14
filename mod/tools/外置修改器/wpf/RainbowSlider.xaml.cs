using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PvzheRemote
{
    /// <summary>
    /// 彩色律动滑条：彩虹渐变轨道（GradientStop 循环流动动画）+ 每段一个小白点刻度 + 数值显示。
    /// </summary>
    public partial class RainbowSlider : UserControl
    {
        readonly DispatcherTimer _anim;
        readonly DispatcherTimer _commit;
        LinearGradientBrush? _brush;
        Border? _rainbow;
        Canvas? _dots;
        bool _ready;

        public RainbowSlider()
        {
            InitializeComponent();
            _commit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            _commit.Tick += (s, e) => { _commit.Stop(); ValueCommitted?.Invoke(this, EventArgs.Empty); };
            Bar.ValueChanged += (s, e) => { UpdateValue(); _commit.Stop(); _commit.Start(); };
            SizeChanged += (s, e) => UpdateValue();
            Loaded += (s, e) => { _ready = true; SetupAnim(); UpdateValue(); };

            _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _anim.Tick += (s, e) => StepRainbow();
            _anim.Start();
        }

        public event EventHandler? ValueCommitted;

        /// <summary>数值后缀（如 "x"、"×"），默认为空。</summary>
        public string Suffix { get; set; } = "";

        public double Value
        {
            get => Bar.Value;
            set => Bar.Value = value;
        }

        public double Minimum
        {
            get => Bar.Minimum;
            set => Bar.Minimum = value;
        }

        public double Maximum
        {
            get => Bar.Maximum;
            set => Bar.Maximum = value;
        }

        public void SetRange(double min, double max, double val, double tick)
        {
            Bar.Minimum = min;
            Bar.Maximum = max;
            Bar.TickFrequency = tick;
            Bar.Value = val;
            IsSnapToTickEnabled2(tick > 0);
            UpdateValue();
        }

        void IsSnapToTickEnabled2(bool v) => Bar.IsSnapToTickEnabled = v;

        void SetupAnim()
        {
            var tpl = Bar.Template;
            if (tpl == null) return;
            _brush = tpl.FindName("RainbowBrush", Bar) as LinearGradientBrush;
            _rainbow = tpl.FindName("Rainbow", Bar) as Border;
            _dots = tpl.FindName("DotLayer", Bar) as Canvas;
        }

        void StepRainbow()
        {
            if (_brush == null || !IsVisible) return;
            const double step = 0.008;
            foreach (var g in _brush.GradientStops)
            {
                g.Offset += step;
                if (g.Offset > 1) g.Offset -= 1;
            }
        }

        void UpdateValue()
        {
            if (!_ready) return;
            ValLabel.Text = Bar.Value.ToString(Bar.TickFrequency >= 1 ? "0" : "0.##") + Suffix;
            if (_rainbow != null && _dots != null && Bar.Template != null)
            {
                double trackW = _dots.ActualWidth;
                if (trackW > 0)
                {
                    double ratio = (Bar.Value - Bar.Minimum) / Math.Max(0.0001, Bar.Maximum - Bar.Minimum);
                    _rainbow.Width = trackW * ratio;
                    DrawDots(trackW);
                }
            }
        }

        void DrawDots(double trackW)
        {
            _dots!.Children.Clear();
            if (trackW <= 0) return;
            double span = Math.Max(0.0001, Bar.Maximum - Bar.Minimum);
            int n = (int)(span / Math.Max(0.0001, Bar.TickFrequency)) + 1;
            if (n < 2) n = 2;
            for (int i = 0; i < n; i++)
            {
                double x = trackW * i / (n - 1);
                var dot = new Ellipse { Width = 3, Height = 3, Fill = Brushes.White };
                Canvas.SetLeft(dot, x - 1.5);
                Canvas.SetTop(dot, 2.5);
                _dots.Children.Add(dot);
            }
        }
    }
}
