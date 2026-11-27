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
                app.Resources[SystemParameters.ToolTipPopupAnimationKey] = PopupAnimation.None;
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
        private readonly WidgetMenu menu;
        private readonly Forms.ContextMenuStrip trayMenuTrigger;
        private readonly Icon icon;
        private readonly Anchor anchor;
        private int selected = -1;
        private bool loaded, closed, dragging, watchingOutside;
        private HwndSource source;
        private Point? press;
        private Point originalAnchor;
        private Vector grabOffsetDip;

        public Shell() : this(Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "DualMeter", "transparency.txt")) { }

        internal Shell(string transparencyFile)
        {
            anchor = new Anchor(Path.Combine(Path.GetDirectoryName(transparencyFile), "position.txt"));
            dashboard = new Dashboard();
            Window = new Window {
                Title = "DualMeter · 模型额度",
                Content = dashboard.View,
                // Keep the layered surface stable; only the drawer's pixels change on a toggle.
                SizeToContent = SizeToContent.Manual,
                Width = dashboard.SurfaceLimit.Width,
                Height = dashboard.SurfaceLimit.Height,
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
            menu = new WidgetMenu(transparencyFile, dashboard.SetBackgroundOpacity,
                Restore, Refresh, delegate { Select(-1); Window.Hide(); }, Window.Close);
            menu.SetWidgetVisible(Window.IsVisible);
            Window.IsVisibleChanged += delegate { menu.SetWidgetVisible(Window.IsVisible); };
            menu.View.Opened += delegate { UpdateOutsideClicks(); };
            menu.View.Closed += delegate { UpdateOutsideClicks(); };
            // NotifyIcon gives its native window foreground ownership for mouse and keyboard requests.
            // Hand the opening to our shared WPF menu, including when the widget is hidden.
            trayMenuTrigger = new Forms.ContextMenuStrip();
            trayMenuTrigger.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                e.Cancel = true;
                Window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate {
                    if (!closed) menu.Open(null, PlacementMode.MousePoint);
                }));
            };
            icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            tray = new Forms.NotifyIcon {
                Icon = icon, Text = "DualMeter · Codex / Antigravity",
                Visible = true, ContextMenuStrip = trayMenuTrigger
            };
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e)
            {
                if (e.Button == Forms.MouseButtons.Left) Restore();
            };
            Window.PreviewMouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                menu.Open(Window, PlacementMode.MousePoint);
            };
            dashboard.Selected += delegate(int index) { Select(selected == index ? -1 : index); };
            dashboard.CollapseRequested += delegate { Select(-1); };
            dashboard.RefreshRequested += Refresh;
            Window.Deactivated += delegate {
                if (selected < 0 || closed) return;
                // The menu lives in its own popup and may temporarily take focus.
                Window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate {
                    if (!Window.IsActive && !menu.View.IsOpen) Select(-1);
                }));
            };
            // Buttons take their own capture for normal clicks. Also support dragging blank parts of the card.
            Window.MouseLeftButtonDown += delegate {
                if (press.HasValue && Mouse.Captured == null) Window.CaptureMouse();
            };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (menu.View.IsOpen) return;
                if (e.Key == Key.Escape && dragging)
                {
                    e.Handled = true;
                    anchor.MovePill(Window, dashboard, press.Value, originalAnchor);
                    FinishDrag();
                }
                else if (e.Key == Key.Escape && selected >= 0) { e.Handled = true; Select(-1); }
                else if (e.Key == Key.F5) { e.Handled = true; Refresh(); }
                else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
                {
                    e.Handled = true;
                    menu.Open(Keyboard.FocusedElement as UIElement ?? Window, PlacementMode.Bottom);
                }
            };

            timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            timer.Tick += delegate { Refresh(); };
            anchor.Read();
            Window.SourceInitialized += delegate
            {
                source = HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
                source.AddHook(WindowMessage);
            };
            Window.Loaded += delegate
            {
                if (loaded) return;
                loaded = true;
                anchor.Apply(Window, dashboard);
                Window.Opacity = 1;
                timer.Start();
                Refresh();
            };
            Window.Closed += delegate
            {
                closed = true;
                FinishDrag();
                timer.Stop();
                StopOutsideClicks();
                if (source != null && !source.IsDisposed) source.RemoveHook(WindowMessage);
            };
            Render();
        }

        private void Select(int index)
        {
            if (closed || dragging || index == selected || index < -1 || index > 1) return;
            selected = index;
            if (index >= 0 && Window.IsVisible && !Window.IsActive) Window.Activate();
            UpdateOutsideClicks();
            Render();
        }

        private void UpdateOutsideClicks()
        {
            if (closed || source == null || (selected < 0 && !menu.View.IsOpen)) StopOutsideClicks();
            else if (!watchingOutside) watchingOutside = OutsideClick.SetReceiver(source.Handle);
        }

        private void StopOutsideClicks()
        {
            if (!watchingOutside) return;
            OutsideClick.SetReceiver(IntPtr.Zero);
            watchingOutside = false;
        }

        private void Render()
        {
            dashboard.Render(selected, states);
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int wmDpiChanged = 0x02E0, wmDisplayChange = 0x007E, wmSettingChange = 0x001A;
            const int setWorkArea = 0x002F;
            if (loaded && (message == wmDpiChanged || message == wmDisplayChange ||
                (message == wmSettingChange && wParam.ToInt64() == setWorkArea)))
            {
                // Let WPF accept the monitor's new pixel scale before laying out the surface.
                Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                    if (!closed && !dragging) anchor.Apply(Window, dashboard);
                }));
            }
            const int wmInput = 0x00FF;
            const int wmActivateApp = 0x001C;
            bool leftMenu = message == wmActivateApp && wParam == IntPtr.Zero && menu.View.IsOpen;
            if (leftMenu || (message == wmInput && watchingOutside && !dragging && OutsideClick.IsExternalPress(hwnd, lParam)))
            {
                if (menu.View.IsOpen) menu.View.IsOpen = false;
                Select(-1);
            }
            const int mouseMove = 0x0200, leftDown = 0x0201, leftUp = 0x0202, doubleClick = 0x0203;
            const int cancelMode = 0x001F, captureChanged = 0x0215;
            if (loaded && !closed && !dragging && (message == leftDown || message == doubleClick))
            {
                press = null;
                Point pointer = MouseMessagePoint(hwnd, lParam);
                var hit = Window.InputHitTest(Window.PointFromScreen(pointer)) as DependencyObject;
                if (hit != null && !menu.View.IsOpen && !InsideScrollBar(hit))
                {
                    press = pointer;
                    originalAnchor = dashboard.ScreenAnchor;
                    grabOffsetDip = source.CompositionTarget.TransformFromDevice.Transform(originalAnchor - pointer);
                }
            }
            else if (press.HasValue && message == mouseMove && (wParam.ToInt64() & 1) != 0)
                DragTo(MouseMessagePoint(hwnd, lParam));
            else if (press.HasValue && (message == leftUp || message == cancelMode ||
                (message == captureChanged && dragging && lParam != hwnd) || (message == mouseMove && !dragging)))
            {
                if (dragging && message == leftUp) DragTo(MouseMessagePoint(hwnd, lParam));
                FinishDrag();
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hwnd, ref System.Drawing.Point point);
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();

        private static Point MouseMessagePoint(IntPtr hwnd, IntPtr lParam)
        {
            int packed = unchecked((int)lParam.ToInt64());
            var point = new System.Drawing.Point(unchecked((short)packed), unchecked((short)(packed >> 16)));
            ClientToScreen(hwnd, ref point);
            return new Point(point.X, point.Y);
        }

        private void DragTo(Point pointer)
        {
            var target = source.CompositionTarget;
            if (!dragging)
            {
                Vector moved = target.TransformFromDevice.Transform(pointer - press.Value);
                if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                // Clear the pressed WPF button first, then track the drag across transparent pixels and other windows.
                if (Mouse.Captured != null) Mouse.Capture(null);
                SetCapture(source.Handle);
                if (GetCapture() != source.Handle) { press = null; return; }
                dragging = true;
            }
            anchor.MovePill(Window, dashboard, pointer, pointer + target.TransformToDevice.Transform(grabOffsetDip));
        }

        private void FinishDrag()
        {
            bool moved = dragging;
            dragging = false;
            press = null;
            if (Mouse.Captured == Window) Window.ReleaseMouseCapture();
            if (!moved) return;
            if (source != null && GetCapture() == source.Handle) ReleaseCapture();
            if (!closed) anchor.Apply(Window, dashboard);
            anchor.Save();
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
            anchor.Apply(Window, dashboard);
            Window.Activate();
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
            StopOutsideClicks();
            menu.View.IsOpen = false;
            menu.SaveIfChanged();
            tray.Visible = false;
            tray.Dispose();
            trayMenuTrigger.Dispose();
            if (icon != null) icon.Dispose();
        }
    }

    // Persist the pill's center and top in monitor-relative pixels, independently of the drawer.
    internal sealed class Anchor
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RectPx { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr handle, out RectPx rect);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y,
            int width, int height, uint flags);

        private readonly string file;
        private string display;
        private int centerOffset, topOffset;

        public Anchor(string file) { this.file = file; }

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

        public void Save()
        {
            if (display == null) return;
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllLines(temp, new[] {display, centerOffset.ToString(CultureInfo.InvariantCulture),
                                          topOffset.ToString(CultureInfo.InvariantCulture)});
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static int Clamp(long value, int low, int high)
        {
            return (int)Math.Max(low, Math.Min(value, high));
        }

        private static System.Drawing.Rectangle SurfaceBounds(System.Drawing.Rectangle workArea, Dashboard dashboard, Matrix scale)
        {
            // Only the root's transparent margin may extend past the working area.
            Thickness margin = dashboard.View.Margin;
            return System.Drawing.Rectangle.FromLTRB(
                workArea.Left - (int)Math.Round(margin.Left * scale.M11),
                workArea.Top - (int)Math.Round(margin.Top * scale.M22),
                workArea.Right + (int)Math.Round(margin.Right * scale.M11),
                workArea.Bottom + (int)Math.Round(margin.Bottom * scale.M22));
        }

        private static System.Drawing.Rectangle PillAt(System.Drawing.Rectangle bounds, Size compact, Matrix scale, long center, long top)
        {
            int width = Math.Min(bounds.Width, (int)Math.Round(compact.Width * scale.M11));
            int height = Math.Min(bounds.Height, (int)Math.Round(compact.Height * scale.M22));
            int x = Clamp(center - width / 2, bounds.Left, bounds.Right - width);
            int y = Clamp(top, bounds.Top, bounds.Bottom - height);
            return new System.Drawing.Rectangle(x, y, width, height);
        }

        public void MovePill(Window window, Dashboard dashboard, Point pointer, Point requestedAnchor)
        {
            // During a drag, move the existing surface without resizing it. Windows' caption
            // drag would constrain the entire (mostly invisible) surface to the desktop.
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)Math.Round(pointer.X), (int)Math.Round(pointer.Y)));
            var area = screen.WorkingArea;
            var scale = PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice;
            var bounds = SurfaceBounds(area, dashboard, scale);
            var pill = PillAt(bounds, dashboard.CompactSize, scale, (long)Math.Round(requestedAnchor.X), (long)Math.Round(requestedAnchor.Y));
            int center = pill.Left + pill.Width / 2;
            Point current = dashboard.ScreenAnchor;
            int dx = (int)Math.Round(center - current.X), dy = (int)Math.Round(pill.Top - current.Y);
            if (dx != 0 || dy != 0)
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                RectPx rect;
                if (!GetWindowRect(hwnd, out rect) || !SetWindowPos(hwnd, IntPtr.Zero, rect.Left + dx, rect.Top + dy,
                    0, 0, 0x0001 | 0x0004 | 0x0010)) return;
            }
            display = screen.DeviceName;
            centerOffset = center - area.Left;
            topOffset = pill.Top - area.Top;
        }

        public void Apply(Window window, Dashboard dashboard)
        {
            var source = PresentationSource.FromVisual(window);
            if (source == null || source.CompositionTarget == null) return;
            Forms.Screen screen = null;
            foreach (var device in Forms.Screen.AllScreens)
                if (device.DeviceName == display) { screen = device; break; }
            bool saved = screen != null;
            if (!saved) screen = Forms.Screen.PrimaryScreen;
            if (screen == null) return;
            var area = screen.WorkingArea;
            if (area.Width <= 0 || area.Height <= 0) return;
            Matrix scale = source.CompositionTarget.TransformToDevice;
            var bounds = SurfaceBounds(area, dashboard, scale);
            long center = (long)area.Left + (saved ? centerOffset : area.Width / 2);
            long top = (long)area.Top + (saved ? topOffset : 14);
            var pill = PillAt(bounds, dashboard.CompactSize, scale, center, top);
            Size limit = dashboard.SurfaceLimit;
            int width = Math.Min(bounds.Width, (int)Math.Round(limit.Width * scale.M11));
            int reserve = Math.Max(0, (int)Math.Round(limit.Height * scale.M22) - pill.Height);
            int above = pill.Top - bounds.Top, below = bounds.Bottom - pill.Bottom;
            bool openAbove = below < reserve && above > below;
            int room = Math.Min(reserve, openAbove ? above : below);
            int height = pill.Height + room;
            int x = Clamp((long)pill.Left + pill.Width / 2 - width / 2, bounds.Left, bounds.Right - width);
            int y = openAbove ? pill.Top - room : pill.Top;
            dashboard.Place((pill.Left - x) / scale.M11, openAbove, width / scale.M11);

            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            RectPx rect;
            if (GetWindowRect(hwnd, out rect) && (rect.Left != x || rect.Top != y ||
                rect.Right - rect.Left != width || rect.Bottom - rect.Top != height))
                SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010);
        }
    }
}
