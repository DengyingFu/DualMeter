using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Icon = System.Drawing.Icon;
using Point = System.Windows.Point;

namespace DualMeter
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool first;
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset,
                       @"Local\DualMeter.ShowWindow.v1", out first))
            {
                if (!first) { show.Set(); return; }
                var app = new Application();
                using (var shell = new Shell())
                {
                    var listener = ThreadPool.RegisterWaitForSingleObject(show, delegate
                    {
                        if (!app.Dispatcher.HasShutdownStarted)
                            app.Dispatcher.BeginInvoke(new Action(shell.Restore));
                    }, null, Timeout.Infinite, false);
                    try { app.Run(shell.Window); }
                    finally { listener.Unregister(null); }
                }
            }
        }
    }

    internal sealed class Shell : IDisposable
    {
        public readonly Window Window;
        private readonly Dashboard dashboard;
        private readonly ServiceState[] states = { new ServiceState(), new ServiceState() };
        private readonly DispatcherTimer timer;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ContextMenuStrip menu;
        private readonly Icon icon;
        private readonly Anchor anchor = new Anchor();
        private int selected = -1;
        private bool loaded, closed, dragging;
        private HwndSource source;
        private Point? press;

        public Shell()
        {
            dashboard = new Dashboard();
            Window = new Window {
                Title = "DualMeter · 模型额度",
                Content = dashboard.View,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Opacity = 0,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示额度悬浮窗", null, delegate { Restore(); });
            menu.Items.Add("立即刷新", null, delegate { Refresh(); });
            menu.Items.Add("隐藏到托盘", null, delegate { Window.Hide(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Window.Close(); });
            icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            tray = new Forms.NotifyIcon {
                Icon = icon, Text = "DualMeter · Codex / Antigravity",
                Visible = true, ContextMenuStrip = menu
            };
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e)
            {
                if (e.Button == Forms.MouseButtons.Left) Restore();
            };
            Window.PreviewMouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                menu.Show(Forms.Cursor.Position);
            };
            dashboard.Selected += delegate(int index) { Select(selected == index ? -1 : index); };
            dashboard.CollapseRequested += delegate { Select(-1); };
            dashboard.RefreshRequested += Refresh;
            Window.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                press = InsideScrollBar(e.OriginalSource as DependencyObject) ? (Point?)null : e.GetPosition(Window);
            };
            Window.PreviewMouseLeftButtonUp += delegate { press = null; };
            Window.PreviewMouseMove += Drag;
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape && selected >= 0) { e.Handled = true; Select(-1); }
                else if (e.Key == Key.F5) { e.Handled = true; Refresh(); }
                else if (e.Key == Key.Apps) { e.Handled = true; menu.Show(Forms.Cursor.Position); }
            };

            timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            timer.Tick += delegate { Refresh(); };
            anchor.Read();
            Window.SourceInitialized += delegate
            {
                source = HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
                source.AddHook(AdjustResize);
            };
            Window.Loaded += delegate
            {
                if (loaded) return;
                loaded = true;
                anchor.Apply(Window);
                Window.Opacity = 1;
                timer.Start();
                Refresh();
            };
            Window.Closed += delegate
            {
                closed = true;
                timer.Stop();
                if (source != null && !source.IsDisposed) source.RemoveHook(AdjustResize);
            };
            Render();
        }

        private void Select(int index)
        {
            if (closed || dragging || index < -1 || index > 1) return;
            selected = index;
            Render();
        }

        private void Render()
        {
            dashboard.Render(selected, states);
        }

        private IntPtr AdjustResize(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WPF sizes this layered HWND to its content. Apply the anchor to that same
            // native resize before Windows commits it, rather than moving an already drawn frame.
            const int wmWindowPosChanging = 0x0046;
            if (message == wmWindowPosChanging && !dragging && !closed)
                anchor.AdjustResize(lParam);
            return IntPtr.Zero;
        }

        private void Drag(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { press = null; return; }
            if (!press.HasValue || dragging) return;
            Vector moved = e.GetPosition(Window) - press.Value;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            press = null;
            dragging = true;
            e.Handled = true;
            // Cancel ButtonBase's captured press before the native move loop consumes mouse up.
            Mouse.Capture(null);
            try { Window.DragMove(); anchor.Remember(Window); }
            catch (InvalidOperationException) { }
            finally { dragging = false; anchor.Apply(Window); }
        }

        private static bool InsideScrollBar(DependencyObject element)
        {
            while (element != null)
            {
                if (element is ScrollBar || element is Thumb) return true;
                var content = element as FrameworkContentElement;
                element = content != null ? content.Parent : VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        public void Restore()
        {
            if (closed) return;
            if (!Window.IsVisible) Window.Show();
            Window.Activate();
            anchor.Apply(Window);
        }

        private void Refresh()
        {
            if (closed) return;
            Read(0, CodexSource.Fetch);
            Read(1, GeminiSource.Fetch);
        }

        private async void Read(int index, Func<List<Reading>> fetch)
        {
            ServiceState state = states[index];
            if (state.Busy) return;
            state.Busy = true;
            Render();
            try
            {
                List<Reading> rows = await Task.Run(fetch);
                if (closed) return;
                if (rows == null || rows.Count == 0)
                    throw new InvalidOperationException("当前没有可显示的额度。");
                state.Rows = rows;
                state.Updated = DateTime.Now;
                state.Error = null;
            }
            catch (Exception ex)
            {
                state.Error = String.IsNullOrWhiteSpace(ex.Message) ? "获取额度失败" : ex.Message;
            }
            finally
            {
                state.Busy = false;
                if (!closed) Render();
            }
        }

        public void Dispose()
        {
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            if (icon != null) icon.Dispose();
        }
    }

    // Save the user's preferred compact center and top. Expanding near an edge may move the
    // window temporarily, but the next collapse returns to the original compact position.
    internal sealed class Anchor
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RectPx { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPos
        {
            public IntPtr Hwnd, After;
            public int X, Y, Width, Height;
            public uint Flags;
        }
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr handle, out RectPx rect);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y,
            int width, int height, uint flags);

        private readonly string file = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "DualMeter", "position.txt");
        private string display;
        private int centerOffset, topOffset;

        public void Read()
        {
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > 1024) return;
                var lines = File.ReadAllLines(file);
                if (lines.Length == 3 && Int32.TryParse(lines[1], out centerOffset) &&
                    Int32.TryParse(lines[2], out topOffset)) display = lines[0];
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Remember(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            RectPx bounds;
            if (!GetWindowRect(hwnd, out bounds)) return;
            var screen = Forms.Screen.FromHandle(hwnd);
            display = screen.DeviceName;
            centerOffset = bounds.Left + (bounds.Right - bounds.Left) / 2 - screen.WorkingArea.Left;
            topOffset = bounds.Top - screen.WorkingArea.Top;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllLines(file, new[] {display, centerOffset.ToString(CultureInfo.InvariantCulture),
                                          topOffset.ToString(CultureInfo.InvariantCulture)});
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private bool TargetPosition(int width, int height, out int x, out int y)
        {
            x = y = 0;
            Forms.Screen screen = null;
            foreach (var device in Forms.Screen.AllScreens)
                if (device.DeviceName == display) { screen = device; break; }
            bool saved = screen != null;
            if (!saved) screen = Forms.Screen.PrimaryScreen;
            if (screen == null) return false;
            var area = screen.WorkingArea;
            long center = (long)area.Left + (saved ? centerOffset : area.Width / 2);
            long top = (long)area.Top + (saved ? topOffset : 14);
            x = (int)Math.Max(area.Left, Math.Min(center - width / 2, (long)area.Right - width));
            y = (int)Math.Max(area.Top, Math.Min(top, (long)area.Bottom - height));
            return true;
        }

        public void AdjustResize(IntPtr windowPos)
        {
            const uint noSize = 0x0001, noMove = 0x0002, noCopyBits = 0x0100;
            var pos = (WindowPos)Marshal.PtrToStructure(windowPos, typeof(WindowPos));
            if ((pos.Flags & noSize) != 0 || pos.Width <= 0 || pos.Height <= 0) return;
            if (!TargetPosition(pos.Width, pos.Height, out pos.X, out pos.Y)) return;
            pos.Flags = (pos.Flags & ~noMove) | noCopyBits;
            Marshal.StructureToPtr(pos, windowPos, false);
        }

        public void Apply(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            RectPx rect;
            int x, y;
            if (!GetWindowRect(hwnd, out rect)) return;
            if (TargetPosition(rect.Right - rect.Left, rect.Bottom - rect.Top, out x, out y) &&
                (x != rect.Left || y != rect.Top))
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        }
    }
}
