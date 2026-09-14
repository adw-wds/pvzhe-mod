using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;

namespace PvzheRemote
{
    /// <summary>精致的圆角开关（带滑动动画）。</summary>
    public partial class ToggleSwitch : UserControl
    {
        public static readonly DependencyProperty IsOnProperty =
            DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(ToggleSwitch),
                new PropertyMetadata(false, OnIsOnChanged));

        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register(nameof(Command), typeof(Action<bool>), typeof(ToggleSwitch));

        public bool IsOn
        {
            get => (bool)GetValue(IsOnProperty);
            set => SetValue(IsOnProperty, value);
        }

        /// <summary>切换时回调（参数 = 新状态）。</summary>
        public Action<bool> Command
        {
            get => (Action<bool>)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        // 轨道弧面渐变：开=顶部受光亮绿（凸起）；关=顶部暗、底反光（内凹槽）
        // Win10 平面药丸：开=强调蓝，关=白底灰边（不再是弧面渐变）
        static readonly Brush TrackOn = Solid(0xFF0078D4);
        static readonly Brush TrackOff = Solid(0xFFFFFFFF);
        static readonly Brush BorderOn = Solid(0xFF0078D4);
        static readonly Brush BorderOff = Solid(0xFF8A8A8A);
        static readonly Brush KnobOn = Solid(0xFFFFFFFF);
        static readonly Brush KnobOff = Solid(0xFF8A8A8A);

        static Brush Solid(uint rgb)
        {
            var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            b.Freeze();
            return b;
        }

        public ToggleSwitch()
        {
            InitializeComponent();
            ApplyTrack();
        }

        void ApplyTrack()
        {
            Track.Background = IsOn ? TrackOn : TrackOff;
            Track.BorderBrush = IsOn ? BorderOn : BorderOff;
            if (Knob != null) Knob.Fill = IsOn ? KnobOn : KnobOff;
        }

        static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ToggleSwitch)d).UpdateVisual();
        }

        void UpdateVisual()
        {
            ApplyTrack();
            // 40 宽轨道、12 旋钮：关=左 4，开=右 40-12-4=24
            var to = new Thickness(IsOn ? 24 : 4, 0, 0, 0);
            Knob.BeginAnimation(MarginProperty,
                new ThicknessAnimation(to, TimeSpan.FromMilliseconds(150)));
        }

        DateTime _lastToggle = DateTime.MinValue;

        void OnToggle(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;   // 阻止事件冒泡到方块，避免方块再切一次（否则无法稳定开关）
            if ((DateTime.Now - _lastToggle).TotalMilliseconds < 260) return;   // 双击防抖：快速连点不再把开关翻回
            _lastToggle = DateTime.Now;
            IsOn = !IsOn;
            Command?.Invoke(IsOn);
        }

        /// <summary>不触发命令地设置状态（用于 /get 同步）。</summary>
        public void SetSilently(bool on)
        {
            IsOn = on;
        }
    }
}
