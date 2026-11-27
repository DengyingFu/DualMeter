using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;

namespace DualMeter
{
    internal sealed class WidgetMenu
    {
        private const int MaximumTransparency = 100;
        private readonly string file;
        private readonly Slider slider;
        private readonly TextBlock caption;
        private int savedPercent;
        private int Percent { get { return (int)Math.Round(slider.Value); } }
        public ContextMenu View { get; private set; }

        public WidgetMenu(string file, Action<double> apply, Action show, Action refresh, Action hide, Action exit)
        {
            this.file = file;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WidgetMenu.xaml"))
                View = (ContextMenu)XamlReader.Load(stream);
            if (SystemParameters.HighContrast) UseHighContrastColors();
            Bind("ShowItem", show);
            Bind("RefreshItem", refresh);
            Bind("HideItem", hide);
            Bind("ExitItem", exit);

            var row = (MenuItem)View.FindName("TransparencyRow");
            slider = (Slider)View.FindName("OpacitySlider");
            caption = (TextBlock)View.FindName("CurrentPercent");
            slider.Value = savedPercent = ReadPercent(file);
            Action update = delegate {
                caption.Text = Percent.ToString(CultureInfo.InvariantCulture) + "%";
                caption.ToolTip = null;
                apply((MaximumTransparency - Percent) / 100.0);
            };
            update();
            slider.ValueChanged += delegate { update(); };
            slider.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(delegate { SaveIfChanged(); }), true);
            row.AddHandler(Keyboard.KeyUpEvent, new KeyEventHandler(delegate { SaveIfChanged(); }), true);
            row.PreviewKeyDown += AdjustWithKeyboard;
            View.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.F5) { e.Handled = true; View.IsOpen = false; refresh(); }
                else if (e.Key == Key.Escape) { e.Handled = true; View.IsOpen = false; }
            };
            View.Closed += delegate { SaveIfChanged(); };
        }

        public void Open(UIElement target, PlacementMode placement)
        {
            if (View.IsOpen) return;
            View.PlacementTarget = target;
            View.Placement = placement;
            View.IsOpen = true;
        }

        public void SetWidgetVisible(bool visible)
        {
            ((MenuItem)View.FindName("HideItem")).Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            ((MenuItem)View.FindName("ShowItem")).Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Bind(string name, Action action)
        {
            ((MenuItem)View.FindName(name)).Click += delegate { View.IsOpen = false; action(); };
        }

        private void AdjustWithKeyboard(object sender, KeyEventArgs e)
        {
            // Menu navigation focuses the settings row; allow the same range keys as the slider.
            switch (e.Key)
            {
                case Key.Left: slider.Value -= slider.SmallChange; break;
                case Key.Right: slider.Value += slider.SmallChange; break;
                case Key.PageDown: slider.Value -= slider.LargeChange; break;
                case Key.PageUp: slider.Value += slider.LargeChange; break;
                case Key.Home: slider.Value = slider.Minimum; break;
                case Key.End: slider.Value = slider.Maximum; break;
                case Key.Enter: case Key.Space: slider.Focus(); break;
                default: return;
            }
            e.Handled = true;
        }

        private void UseHighContrastColors()
        {
            var colors = View.Resources;
            colors["Panel"] = colors["Card"] = SystemColors.MenuBrush;
            colors["Ink"] = colors["Muted"] = colors["Edge"] = colors["TrackInk"] = SystemColors.MenuTextBrush;
            colors["Accent"] = colors["Knob"] = SystemColors.MenuTextBrush;
            colors["Hover"] = colors["ExitHover"] = SystemColors.HighlightBrush;
            colors["HoverInk"] = colors["HoverMark"] = colors["ExitInk"] = SystemColors.HighlightTextBrush;
        }

        private static int ReadPercent(string file)
        {
            try
            {
                int value;
                if (File.Exists(file) && new FileInfo(file).Length <= 32 &&
                    Int32.TryParse(File.ReadAllText(file), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
                    value >= 0 && value <= MaximumTransparency) return value;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return 0;
        }

        internal void SaveIfChanged()
        {
            int value = Percent;
            if (value == savedPercent) return;
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(temp, value.ToString(CultureInfo.InvariantCulture));
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
                savedPercent = value;
            }
            catch (IOException) { ShowSaveFailure(); }
            catch (UnauthorizedAccessException) { ShowSaveFailure(); }
            finally
            {
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private void ShowSaveFailure()
        {
            caption.Text = Percent.ToString(CultureInfo.InvariantCulture) + "%*";
            caption.ToolTip = "设置未能保存，仅本次生效";
        }
    }
}
