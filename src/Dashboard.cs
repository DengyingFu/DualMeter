using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DualMeter
{
    internal sealed class Reading
    {
        public readonly string Label;
        public readonly int? Remaining;
        public readonly string Reset;
        public Reading(string label, int? remaining, string reset)
        {
            Label = label;
            Remaining = remaining;
            Reset = reset;
        }
    }

    internal sealed class ServiceState
    {
        public bool Busy;
        public List<Reading> Rows = new List<Reading>();
        public DateTime? Updated;
        public string Error;
    }

    internal static class Ink
    {
        public static readonly Brush Green = ColorBrush("#39E79E");
        public static readonly Brush Track = ColorBrush("#333C37");
        public static readonly Brush White = ColorBrush("#F1F5F2");
        public static readonly Brush Muted = ColorBrush("#A9B6AE");
        public static readonly Brush Amber = ColorBrush("#F2C27B");
        public static readonly Brush Selected = ColorBrush("#1C2721");
        private static Brush ColorBrush(string hex)
        {
            Brush brush = (Brush)new BrushConverter().ConvertFromString(hex);
            brush.Freeze();
            return brush;
        }
    }

    internal sealed class Gauge : FrameworkElement
    {
        private readonly bool circular;
        private int? value;
        public int? Value { set { this.value = value; InvalidateVisual(); } }
        public Gauge(bool circular)
        {
            this.circular = circular;
            if (circular) Width = Height = 32;
            else Height = 7;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (!circular)
            {
                const int count = 40;
                int filled = value.HasValue ? (int)Math.Round(value.Value * count / 100.0, MidpointRounding.AwayFromZero) : 0;
                if (value > 0 && value < 100) filled = Math.Max(1, Math.Min(count - 1, filled));
                double step = ActualWidth / count;
                for (int i = 0; i < count; i++)
                    dc.DrawRoundedRectangle(i < filled ? Ink.Green : Ink.Track, null,
                        new Rect(i * step, 0, Math.Max(0, step - 2.1), ActualHeight), 2, 2);
                return;
            }
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            const double thickness = 3;
            double radius = Math.Min(center.X, center.Y) - thickness / 2 - 0.5;
            dc.DrawEllipse(null, new Pen(Ink.Track, thickness), center, radius, radius);
            if (!value.HasValue || value <= 0) return;
            var pen = new Pen(Ink.Green,thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (value >= 100) { dc.DrawEllipse(null, pen, center, radius, radius); return; }
            double angle = value.Value * Math.PI / 50;
            var path = new StreamGeometry();
            using (var ctx = path.Open())
            {
                ctx.BeginFigure(new Point(center.X,center.Y-radius), false, false);
                ctx.ArcTo(new Point(center.X+Math.Sin(angle)*radius,center.Y-Math.Cos(angle)*radius), new Size(radius,radius),
                    0,value > 50,SweepDirection.Clockwise,true,false);
            }
            dc.DrawGeometry(null,pen,path);
        }
    }

    internal sealed class Dashboard
    {
        private static readonly string[] Names = { "Codex", "Antigravity" };
        private static readonly ImageBrush[] Marks = { LoadMark("CodexMark.png"), LoadMark("AntigravityMark.png") };
        private readonly Button[] buttons;
        private readonly Gauge[] rings = new Gauge[2];
        private readonly TextBlock[] values = new TextBlock[2];
        private readonly Border[] warnings = new Border[2];
        private readonly FrameworkElement root;
        private int current = -1;
        public FrameworkElement View { get { return root; } }
        public event Action<int> Selected;
        public event Action RefreshRequested;
        public event Action CollapseRequested;

        public Dashboard()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dashboard.xaml"))
                root = (FrameworkElement)XamlReader.Load(stream);
            buttons = new[] { Find<Button>("CodexButton"), Find<Button>("GravityButton") };
            for (int i = 0; i < buttons.Length; i++) BuildTile(i);
            Find<Button>("Refresh").Click += delegate { if (RefreshRequested != null) RefreshRequested(); };
            Find<Button>("Fold").Click += delegate { if (CollapseRequested != null) CollapseRequested(); };
        }

        private T Find<T>(string name) where T : FrameworkElement { return (T)root.FindName(name); }
        private static TextBlock Text(string value, double size, Brush brush, FontWeight weight)
        {
            return new TextBlock { Text = value, FontSize = size, Foreground = brush, FontWeight = weight };
        }

        private static ImageBrush LoadMark(string name)
        {
            var image = new BitmapImage();
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit();
            }
            image.Freeze();
            var brush = new ImageBrush(image) { Stretch = Stretch.Uniform };
            brush.Freeze();
            return brush;
        }

        private static Border Symbol(int index, double size, Brush color)
        {
            var shape = new Border { Width=size, Height=size, OpacityMask=Marks[index], Background=color,
                HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(shape, BitmapScalingMode.HighQuality);
            return shape;
        }

        private void BuildTile(int index)
        {
            var content = new StackPanel { VerticalAlignment=VerticalAlignment.Center };
            var icon = new Grid { HorizontalAlignment=HorizontalAlignment.Center };
            rings[index] = new Gauge(true);
            icon.Children.Add(rings[index]);
            icon.Children.Add(Symbol(index, 19, Ink.White));
            warnings[index] = new Border { Width=6,Height=6,CornerRadius=new CornerRadius(3),
                BorderBrush=Ink.Track,BorderThickness=new Thickness(1),Background=Ink.Amber,
                HorizontalAlignment=HorizontalAlignment.Right, VerticalAlignment=VerticalAlignment.Top,
                Visibility=Visibility.Collapsed };
            icon.Children.Add(warnings[index]);
            content.Children.Add(icon);
            values[index] = Text("—", 14, Ink.White, FontWeights.SemiBold);
            values[index].HorizontalAlignment = HorizontalAlignment.Center;
            values[index].Margin = new Thickness(0,1,0,0);
            content.Children.Add(values[index]);
            buttons[index].Content = content;
            buttons[index].Padding = new Thickness(0);
            var button = buttons[index];
            var hint = new ToolTip { HasDropShadow = false };
            button.ToolTip = hint;
            ToolTipService.SetBetweenShowDelay(button, 0);
            button.PreviewMouseDown += delegate {
                ToolTipService.SetIsEnabled(button, false);
                if (hint.IsOpen) hint.IsOpen = false;
            };
            button.MouseLeave += delegate { ToolTipService.SetIsEnabled(button, true); };
            buttons[index].Click += delegate { if (Selected != null) Selected(index); };
        }

        private void UpdateTile(int i, bool selected, ServiceState state)
        {
            Reading first = state.Rows.Count == 0 ? null : state.Rows[0];
            int? amount = first == null ? null : first.Remaining;
            string display = amount.HasValue ? amount.Value.ToString(CultureInfo.InvariantCulture) + "%" : "—";
            bool stale = !String.IsNullOrEmpty(state.Error);
            rings[i].Value = amount;
            values[i].Text = display;
            values[i].Foreground = stale ? Ink.Amber : Ink.White;
            warnings[i].Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
            buttons[i].Background = selected ? Ink.Selected : Brushes.Transparent;
            string hint = Names[i] + (first == null ? "" : " · " + first.Label + "　剩余 " + display);
            if (stale) hint += state.Updated.HasValue ? "（上次结果）" : "（读取失败）";
            else if (first == null) hint += " · 正在读取";
            ((ToolTip)buttons[i].ToolTip).Content = hint + "\n单击查看额度，拖动可移动悬浮窗";
            AutomationProperties.SetName(buttons[i], hint);
        }

        public void Render(int selected, ServiceState[] states)
        {
            root.Width = selected < 0 ? Find<Border>("Summary").Width : 246;
            for (int i = 0; i < 2; i++) UpdateTile(i, selected == i, states[i]);
            Find<StackPanel>("Drawer").Visibility = selected < 0 ? Visibility.Collapsed : Visibility.Visible;
            if (selected < 0) { current = -1; return; }
            if (current != selected)
            {
                Find<ScrollViewer>("InfoScroll").ScrollToTop();
                Find<Border>("HeaderIcon").Child = Symbol(selected, 17, Ink.Green);
                Find<TextBlock>("Heading").Text = "剩余额度";
                Grid.SetColumn(Find<System.Windows.Shapes.Path>("Pointer"), selected);
                current = selected;
            }
            ServiceState state = states[selected];
            string notice = state.Error;
            if (!String.IsNullOrWhiteSpace(notice) && state.Updated.HasValue)
                notice = "当前显示上次读数。" + notice;
            var banner = Find<TextBlock>("Notice");
            banner.Text = notice;
            banner.Visibility = String.IsNullOrWhiteSpace(notice) ? Visibility.Collapsed : Visibility.Visible;
            var rows = Find<StackPanel>("Rows");
            rows.Children.Clear();
            foreach (var entry in state.Rows) rows.Children.Add(Row(entry));
            if (state.Rows.Count == 0 && state.Busy)
                rows.Children.Add(Text("正在读取额度…", 13, Ink.Muted, FontWeights.Normal));
            string status = state.Busy ? "正在刷新…" : state.Updated.HasValue ?
                state.Updated.Value.ToLocalTime().ToString(state.Updated.Value.ToLocalTime().Date == DateTime.Today ? "HH:mm:ss 更新" : "MM/dd HH:mm 更新") :
                String.IsNullOrEmpty(state.Error) ? "等待读数" : "等待重试";
            Find<TextBlock>("Status").Text = status + "   ·   每分钟刷新";
        }

        private FrameworkElement Row(Reading entry)
        {
            var stack = new StackPanel { Margin=new Thickness(1,7,1,0) };
            var title = new DockPanel();
            string percent = entry.Remaining.HasValue ? entry.Remaining.Value + "%" : "—";
            var amount = Text(percent,14,Ink.White,FontWeights.SemiBold);
            DockPanel.SetDock(amount,Dock.Right); title.Children.Add(amount);
            var label = Text(entry.Label,13,Ink.White,FontWeights.Medium);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextWrapping = TextWrapping.Wrap;
            label.Margin = new Thickness(0,0,6,0); title.Children.Add(label);
            stack.Children.Add(title);
            var bar = new Gauge(false) { Value=entry.Remaining, Margin=new Thickness(0,2,0,2) };
            stack.Children.Add(bar);
            if (!String.IsNullOrWhiteSpace(entry.Reset))
            {
                var reset = Text(entry.Reset,11.5,Ink.Muted,FontWeights.Normal);
                reset.TextWrapping = TextWrapping.Wrap;
                stack.Children.Add(reset);
            }
            AutomationProperties.SetName(stack, entry.Label + "　剩余 " + percent + "　" + entry.Reset);
            return stack;
        }
    }
}
