using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodexQuotaTaskbar
{
    internal static class Program
    {
        internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        internal const double WidgetWidth = 456;
        private const string InstanceName = "Local\\CodexQuotaTaskbar-v1";
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                if (args.Contains("--stop") || args.Contains("--refresh"))
                {
                    using (var signal = EventWaitHandle.OpenExisting(InstanceName + (args.Contains("--stop") ? "-stop" : "-refresh"))) signal.Set();
                    return;
                }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                if (args.Contains("--verify")) { VerifyVisuals(app); return; }
                bool created;
                using (var mutex = new Mutex(true, InstanceName, out created))
                {
                    if (!created) return;
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    { Log(e.Exception); e.Handled = true; };
                    WidgetWindow widget = null;
                    try { widget = new WidgetWindow(app, args.Contains("--inspect")); app.Run(); }
                    finally { if (widget != null) widget.Dispose(); mutex.ReleaseMutex(); }
                }
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (Exception e) { Log(e); MessageBox.Show("控件未能启动：" + e.Message, "Codex 额度控件"); }
        }
        internal static void Log(Exception e)
        {
            try { File.AppendAllText(Path.Combine(Root, "widget.log"), DateTimeOffset.Now.ToString("o") + " " + e.GetType().Name + ": " + e.Message + Environment.NewLine); } catch { }
        }
        internal static string FindCodex()
        {
            string configured = Path.Combine(Root, "codex.path");
            if (File.Exists(configured)) { string p = File.ReadAllText(configured).Trim(); if (File.Exists(p)) return p; }
            string bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(bin))
            {
                string latest = Directory.GetFiles(bin, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (latest != null) return latest;
            }
            foreach (string part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            { try { string path = Path.Combine(part, "codex.exe"); if (File.Exists(path)) return path; } catch { } }
            throw new FileNotFoundException("找不到 Codex，请先安装并登录 Codex 桌面端或 CLI。");
        }
        private static void VerifyVisuals(Application app)
        {
            string dir = Path.Combine(Root, "verification"); Directory.CreateDirectory(dir);
            foreach (double percent in new double[] { 0, 25, 67, 100, double.NaN })
            {
                foreach (double seconds in new double[] { 0, 1.2 })
                {
                    var bar = new QuotaBarControl { Width = WidgetWidth, Height = 38, RemainingPercent = percent };
                    var panel = new Grid { Width = WidgetWidth + 20, Height = 58, Background = new SolidColorBrush(Color.FromRgb(56, 56, 56)) };
                    panel.Children.Add(bar); panel.Measure(new Size(WidgetWidth + 20,58)); panel.Arrange(new Rect(0,0,WidgetWidth + 20,58)); panel.UpdateLayout();
                    bar.RenderAt(seconds); panel.UpdateLayout();
                    string name = (double.IsNaN(percent) ? "unknown" : percent.ToString("0", CultureInfo.InvariantCulture)) + "-" + seconds.ToString("0.0", CultureInfo.InvariantCulture) + ".png";
                    SaveVisual(panel, (int)WidgetWidth + 20, 58, Path.Combine(dir, name));
                    bar.SetAnimationActive(false);
                }
            }
            var placement = TaskbarAnchor.GetPlacement(WidgetWidth, 38);
            File.WriteAllText(Path.Combine(dir, "placement.json"), new JavaScriptSerializer().Serialize(placement));
            app.Shutdown();
        }
        internal static void SaveVisual(Visual visual, int width, int height, string path)
        {
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path)) encoder.Save(file);
        }
    }

    internal sealed class WidgetWindow : Window, IDisposable
    {
        private readonly Application app;
        private readonly QuotaBarControl bar;
        private readonly QuotaClient client;
        private readonly Forms.NotifyIcon tray;
        private readonly DispatcherTimer placementTimer, pollTimer, signalTimer;
        private readonly EventWaitHandle stopSignal, refreshSignal;
        private readonly ToolTip hover;
        private QuotaReading reading;
        private TaskbarPlacement placement;
        private Window details;
        private TextBlock detailsBody;
        private bool disposed;
        private int updates;
        private DateTimeOffset requestedAt;
        private bool refreshPending;
        private bool placementPending;
        private readonly bool inspectWindows;
        private const string RunName = "CodexQuotaTaskbar";

        internal WidgetWindow(Application application, bool inspect)
        {
            app = application;
            inspectWindows = inspect;
            string codexExe = Program.FindCodex();
            Title = "Codex 周额度控件"; Width = Program.WidgetWidth; Height = 38;
            AllowsTransparency = true; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            Background = Brushes.Transparent; ShowInTaskbar = inspect; ShowActivated = false; Topmost = true;
            bar = new QuotaBarControl { RemainingPercent = double.NaN, Cursor = Cursors.Hand };
            Content = bar;
            hover = new ToolTip { Placement = System.Windows.Controls.Primitives.PlacementMode.Top, Content = "正在读取 Codex 周额度…" };
            bar.ToolTip = hover; ToolTipService.SetInitialShowDelay(bar, 300); ToolTipService.SetShowDuration(bar, 15000);
            SourceInitialized += delegate { if (!inspectWindows) TaskbarAnchor.ConfigureWindow(new WindowInteropHelper(this).Handle); };
            bar.MouseLeftButtonUp += delegate { ToggleDetails(); };
            var menu = new ContextMenu();
            var refreshItem = new MenuItem { Header = "立即刷新" }; refreshItem.Click += async delegate { await Refresh(); };
            var startupItem = new MenuItem { Header = "开机启动", IsCheckable = true, IsChecked = IsStartupEnabled() };
            startupItem.Click += delegate { SetStartup(startupItem.IsChecked); WriteState(); };
            var exitItem = new MenuItem { Header = "退出控件" }; exitItem.Click += delegate { app.Shutdown(); };
            menu.Items.Add(refreshItem); menu.Items.Add(startupItem); menu.Items.Add(new Separator()); menu.Items.Add(exitItem);
            menu.Opened += delegate { startupItem.IsChecked = IsStartupEnabled(); };
            bar.ContextMenu = menu;
            tray = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "Codex 周额度：正在读取", Visible = true };
            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("查看额度", null, delegate { Dispatcher.BeginInvoke(new Action(ToggleDetails)); });
            trayMenu.Items.Add("立即刷新", null, delegate { Dispatcher.BeginInvoke(new Action(async delegate { await Refresh(); })); });
            var trayStartup = new Forms.ToolStripMenuItem("开机启动") { Checked = IsStartupEnabled(), CheckOnClick = true };
            trayStartup.Click += delegate { SetStartup(trayStartup.Checked); WriteState(); };
            trayMenu.Opening += delegate { trayStartup.Checked = IsStartupEnabled(); };
            trayMenu.Items.Add(trayStartup); trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("退出", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { app.Shutdown(); })); });
            tray.ContextMenuStrip = trayMenu;
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(new Action(ToggleDetails)); };
            client = new QuotaClient(codexExe);
            client.Updated += delegate(QuotaReading next) { Dispatcher.BeginInvoke(new Action(delegate { ApplyReading(next); })); };
            stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexQuotaTaskbar-v1-stop");
            refreshSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexQuotaTaskbar-v1-refresh");
            placementTimer = Timer(TimeSpan.FromSeconds(2), UpdatePlacement);
            pollTimer = Timer(TimeSpan.FromSeconds(60), async delegate { await Refresh(); });
            signalTimer = Timer(TimeSpan.FromMilliseconds(500), delegate
            {
                if (stopSignal.WaitOne(0)) { app.Shutdown(); return; }
                if (refreshSignal.WaitOne(0)) { var ignored = Refresh(); }
            });
            UpdatePlacement();
            var initial = Refresh();
        }
        private DispatcherTimer Timer(TimeSpan interval, Action action)
        { var timer = new DispatcherTimer { Interval = interval }; timer.Tick += delegate { action(); }; timer.Start(); return timer; }
        private async Task Refresh()
        {
            if (refreshPending || disposed) return;
            refreshPending = true; requestedAt = DateTimeOffset.Now;
            try { await client.RefreshAsync(); }
            catch (Exception e) { Program.Log(e); }
            finally { refreshPending = false; }
        }
        private void ApplyReading(QuotaReading next)
        {
            if (disposed) return;
            reading = next; updates++;
            bar.RemainingPercent = next.RemainingPercent; bar.IsStale = next.IsStale || !string.IsNullOrEmpty(next.Error);
            hover.Content = GetDescription();
            tray.Text = double.IsNaN(next.RemainingPercent) ? "Codex 周额度：暂未读取" : "Codex 周额度：剩余 " + next.RemainingPercent.ToString("0") + "%" + (bar.IsStale ? "（待更新）" : "");
            if (detailsBody != null) detailsBody.Text = GetDescription();
            WriteState();
        }
        private string GetDescription()
        {
            if (reading == null) return "正在读取 Codex 周额度…";
            string text = double.IsNaN(reading.RemainingPercent) ? "暂未读到周额度" : "本周剩余 " + reading.RemainingPercent.ToString("0") + "%\n已使用 " + reading.UsedPercent.ToString("0") + "%";
            if (reading.ResetsAt.HasValue) text += "\n重置：" + reading.ResetsAt.Value.ToLocalTime().ToString("M月d日 HH:mm");
            if (reading.FetchedAt != default(DateTimeOffset)) text += "\n更新：" + reading.FetchedAt.ToLocalTime().ToString("HH:mm:ss");
            if (!string.IsNullOrEmpty(reading.Error)) text += "\n待更新：" + reading.Error;
            return text;
        }
        private async void UpdatePlacement()
        {
            if (disposed || placementPending) return;
            placementPending = true;
            TaskbarPlacement next;
            try { next = await Task.Run(delegate { return TaskbarAnchor.GetPlacement(Program.WidgetWidth, 38); }); }
            finally { placementPending = false; }
            if (disposed) return;
            bool changed = placement == null || next.Visible != placement.Visible || Math.Abs(next.Left-placement.Left) > .1 || Math.Abs(next.Top-placement.Top) > .1 || Math.Abs(next.Width-placement.Width) > .1 || next.Reason != placement.Reason;
            placement = next;
            if (next.Visible)
            {
                Left = next.Left; Top = next.Top; Width = next.Width; Height = next.Height;
                if (!IsVisible) Show();
                var handle = new WindowInteropHelper(this).Handle;
                if (!NativeIsWindowVisible(handle)) NativeShowWindow(handle, 8);
                NativeSetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                bar.SetAnimationActive(true);
            }
            else { if (IsVisible) Hide(); bar.SetAnimationActive(false); }
            if (changed) WriteState();
        }
        private void ToggleDetails()
        {
            if (details != null) { details.Close(); return; }
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "Codex 周额度", Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,14) });
            detailsBody = new TextBlock { Text = GetDescription(), Foreground = new SolidColorBrush(Color.FromRgb(221,218,230)), FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 24 };
            panel.Children.Add(detailsBody);
            panel.Children.Add(new TextBlock { Text = "每分钟自动更新 · 与 Codex 账号共享", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(160,156,172)), Margin = new Thickness(0,14,0,12) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var refresh = new Button { Content = "立即刷新", Padding = new Thickness(12,5,12,5), Margin = new Thickness(0,0,8,0) }; refresh.Click += async delegate { await Refresh(); };
            var exit = new Button { Content = "退出控件", Padding = new Thickness(12,5,12,5) }; exit.Click += delegate { app.Shutdown(); };
            buttons.Children.Add(refresh); buttons.Children.Add(exit); panel.Children.Add(buttons);
            var startup = new CheckBox { Content = "开机启动", IsChecked = IsStartupEnabled(), Foreground = Brushes.White, Margin = new Thickness(0,12,0,0) };
            startup.Click += delegate { SetStartup(startup.IsChecked == true); WriteState(); }; panel.Children.Add(startup);
            details = new Window { Title = "Codex 额度详情", Owner = this, Width = 320, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = inspectWindows, Topmost = true, AllowsTransparency = true, Background = Brushes.Transparent,
                Content = new Border { CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Color.FromRgb(36,34,42)), BorderBrush = new SolidColorBrush(Color.FromRgb(77,70,91)), BorderThickness = new Thickness(1), Child = panel } };
            var popup = details;
            popup.Loaded += delegate { popup.Left = Math.Max(8, Left); popup.Top = Math.Max(8, Top-popup.ActualHeight-12); };
            popup.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) popup.Close(); };
            popup.Deactivated += delegate { popup.Close(); };
            popup.Closed += delegate { details = null; detailsBody = null; };
            popup.Show(); popup.Activate();
        }
        private static Drawing.Icon MakeIcon()
        {
            using (var bitmap = new Drawing.Bitmap(32,32))
            using (var g = Drawing.Graphics.FromImage(bitmap))
            using (var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(154,70,236)))
            using (var font = new Drawing.Font("Segoe UI", 14, Drawing.FontStyle.Bold))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias; g.FillEllipse(fill,1,1,30,30); g.DrawString("Q",font,Drawing.Brushes.White,6,3);
                var handle = bitmap.GetHicon(); var icon = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); DestroyIcon(handle); return icon;
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint="IsWindowVisible")] private static extern bool NativeIsWindowVisible(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint="ShowWindow")] private static extern bool NativeShowWindow(IntPtr handle, int command);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint="SetWindowPos")] private static extern bool NativeSetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint="GetWindowRect")] private static extern bool NativeGetWindowRect(IntPtr handle, out NativeRect rect);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        private static bool IsStartupEnabled()
        { using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) return key != null && key.GetValue(RunName) != null; }
        private static void SetStartup(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
            { if (enabled) key.SetValue(RunName, "\"" + Path.Combine(Program.Root,"CodexQuotaTaskbar.exe") + "\""); else key.DeleteValue(RunName,false); }
        }
        private void WriteState()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                NativeRect nativeRect; NativeGetWindowRect(handle, out nativeRect);
                var data = new { pid = Process.GetCurrentProcess().Id, visible = IsVisible, nativeVisible = NativeIsWindowVisible(handle), nativeRect = nativeRect, placement = placement,
                    remainingPercent = reading == null || double.IsNaN(reading.RemainingPercent) ? (double?)null : reading.RemainingPercent,
                    usedPercent = reading == null || double.IsNaN(reading.UsedPercent) ? (double?)null : reading.UsedPercent,
                    fetchedAt = reading == null ? null : reading.FetchedAt.ToString("o"), resetsAt = reading == null || !reading.ResetsAt.HasValue ? null : reading.ResetsAt.Value.ToString("o"),
                    stale = reading != null && (reading.IsStale || !string.IsNullOrEmpty(reading.Error)), error = reading == null ? null : reading.Error,
                    updates = updates, pollSeconds = 60, requestedAt = requestedAt.ToString("o"), autoStart = IsStartupEnabled(), writtenAt = DateTimeOffset.Now.ToString("o") };
                File.WriteAllText(Path.Combine(Program.Root,"runtime.json"), new JavaScriptSerializer().Serialize(data));
            }
            catch (Exception e) { Program.Log(e); }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            placementTimer.Stop(); pollTimer.Stop(); signalTimer.Stop(); bar.SetAnimationActive(false);
            client.Dispose(); stopSignal.Dispose(); refreshSignal.Dispose();
            if (details != null) details.Close(); tray.Visible = false; tray.Icon.Dispose(); tray.Dispose();
        }
    }
}
