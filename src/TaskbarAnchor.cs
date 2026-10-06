using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace CodexQuotaTaskbar
{
    public sealed class TaskbarPlacement
    {
        public double Left, Top, Width, Height;
        public bool Visible;
        public string Reason;
    }

    // Read-only placement beside the primary Windows taskbar. Nothing is injected
    // into Explorer and no taskbar settings are changed.
    public static class TaskbarAnchor
    {
        private static IntPtr overlayHandle;
        private static IntPtr cachedTrayHandle;
        private static RECT cachedTrayRect;
        private static DateTime nextAutomationRead = DateTime.MinValue;
        private static double cachedStartLeft = Double.NaN;
        private static double cachedOccupiedRight;
        private static readonly object automationLock = new object();

        public static TaskbarPlacement GetPlacement(double desiredWidth, double desiredHeight)
        {
            IntPtr oldDpi = IntPtr.Zero;
            bool changedDpi = false;
            try
            {
                // UI Automation gives physical pixels even to DPI-unaware callers.
                // Use the same coordinate space for the native geometry reads.
                try
                {
                    oldDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
                    changedDpi = oldDpi != IntPtr.Zero;
                }
                catch (EntryPointNotFoundException) { }

                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero || !IsWindowVisible(tray))
                    return Hidden("任务栏未显示");

                RECT trayRect;
                if (!GetWindowRect(tray, out trayRect))
                    return Hidden("无法读取任务栏位置");

                MONITORINFO monitor = new MONITORINFO();
                monitor.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                IntPtr monitorHandle = MonitorFromWindow(tray, 2);
                if (!GetMonitorInfo(monitorHandle, ref monitor))
                    return Hidden("无法读取任务栏屏幕");

                // This widget is deliberately restricted to a bottom taskbar.
                // A collapsed autohide taskbar is not a stable surface to overlay.
                APPBARDATA appbar = new APPBARDATA();
                appbar.cbSize = (uint)Marshal.SizeOf(typeof(APPBARDATA));
                appbar.hWnd = tray;
                uint state = SHAppBarMessage(4, ref appbar).ToUInt32();
                if ((state & 1) != 0)
                    return Hidden("任务栏自动隐藏已启用");
                if (Math.Abs(trayRect.Bottom - monitor.rcMonitor.Bottom) > 3 ||
                    trayRect.Top < monitor.rcMonitor.Top ||
                    trayRect.Right - trayRect.Left < (monitor.rcMonitor.Right - monitor.rcMonitor.Left) / 2 ||
                    trayRect.Bottom - trayRect.Top > (monitor.rcMonitor.Bottom - monitor.rcMonitor.Top) / 4)
                    return Hidden("仅在屏幕底部任务栏显示");
                if (HasFullscreenForeground(monitor.rcMonitor, tray))
                    return Hidden("全屏应用运行中");

                double dpi = 96.0;
                try { uint nativeDpi = GetDpiForWindow(tray); if (nativeDpi > 0) dpi = nativeDpi; }
                catch (EntryPointNotFoundException) { }
                double scale = dpi / 96.0;

                ReadTaskbarButtons(tray, trayRect);
                double startLeft;
                double occupiedRight;
                lock (automationLock)
                {
                    startLeft = cachedStartLeft;
                    occupiedRight = cachedOccupiedRight;
                }
                if (Double.IsNaN(startLeft))
                    return Hidden("等待任务栏开始按钮位置");

                double preferredLeft = trayRect.Left / scale + 86.0;
                double left = Math.Max(preferredLeft, occupiedRight / scale + 12.0);
                double right = startLeft / scale - 12.0;
                double width = Math.Max(1.0, desiredWidth);
                width = Math.Min(width, right - left);
                if (width < 100.0)
                    return Hidden("开始按钮前的空白不足");

                double height = Math.Min(32.0, Math.Max(1.0, desiredHeight));
                height = Math.Min(height, (trayRect.Bottom - trayRect.Top) / scale - 6.0);
                if (height < 16.0)
                    return Hidden("任务栏高度不足");

                return new TaskbarPlacement
                {
                    Left = Math.Round(left, 2),
                    Top = Math.Round(trayRect.Top / scale + ((trayRect.Bottom - trayRect.Top) / scale - height) / 2.0, 2),
                    Width = Math.Round(width, 2),
                    Height = Math.Round(height, 2),
                    Visible = true,
                    Reason = width < desiredWidth ? "空间受限，已缩窄" : "任务栏空白区域"
                };
            }
            catch (Exception)
            {
                // Explorer can disappear or invalidate UIA elements during a restart.
                return Hidden("任务栏正在更新");
            }
            finally
            {
                if (changedDpi)
                {
                    try { SetThreadDpiAwarenessContext(oldDpi); }
                    catch (EntryPointNotFoundException) { }
                }
            }
        }

        public static void ConfigureWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            overlayHandle = hwnd;
            long style = GetWindowLongPtr(hwnd, -20).ToInt64();
            style |= 0x00000080L | 0x08000000L; // TOOLWINDOW | NOACTIVATE
            style &= ~0x00040000L; // Remove APPWINDOW (no taskbar button).
            SetWindowLongPtr(hwnd, -20, new IntPtr(style));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        }

        private static TaskbarPlacement Hidden(string reason)
        {
            return new TaskbarPlacement { Visible = false, Reason = reason };
        }

        private static void ReadTaskbarButtons(IntPtr tray, RECT trayRect)
        {
            lock (automationLock)
            {
                DateTime now = DateTime.UtcNow;
                bool changed = tray != cachedTrayHandle || !SameRect(trayRect, cachedTrayRect);
                if (!changed && now < nextAutomationRead) return;
                cachedTrayHandle = tray;
                cachedTrayRect = trayRect;
                nextAutomationRead = now.AddSeconds(3);
                // Fail closed instead of using old button geometry after Explorer changes.
                cachedStartLeft = Double.NaN;
                cachedOccupiedRight = trayRect.Left;
                try
                {
                    AutomationElement root = AutomationElement.FromHandle(tray);
                    if (root == null) return;
                    AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                    double startLeft = Double.NaN;
                    for (int i = 0; i < elements.Count; i++)
                    {
                        AutomationElement.AutomationElementInformation element = elements[i].Current;
                        if (element.IsOffscreen) continue;
                        string id = element.AutomationId ?? String.Empty;
                        if (String.Equals(id, "StartButton", StringComparison.OrdinalIgnoreCase) ||
                            (element.ControlType == ControlType.Button &&
                            (element.Name == "开始" || element.Name == "Start")))
                        {
                            Rect bounds = element.BoundingRectangle;
                            if (ValidTaskbarBounds(bounds, trayRect)) { startLeft = bounds.Left; break; }
                        }
                    }
                    if (Double.IsNaN(startLeft)) return;

                    double occupiedRight = trayRect.Left;
                    for (int i = 0; i < elements.Count; i++)
                    {
                        AutomationElement.AutomationElementInformation element = elements[i].Current;
                        if (element.IsOffscreen) continue;
                        string id = element.AutomationId ?? String.Empty;
                        string name = element.Name ?? String.Empty;
                        bool isWidget = id.IndexOf("Widget", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("小组件", StringComparison.Ordinal) >= 0 ||
                            name.IndexOf("Widgets", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (element.ControlType != ControlType.Button && !isWidget) continue;
                        Rect bounds = element.BoundingRectangle;
                        if (ValidTaskbarBounds(bounds, trayRect) && bounds.Left < startLeft - 1.0)
                            occupiedRight = Math.Max(occupiedRight, bounds.Right);
                    }
                    cachedStartLeft = startLeft;
                    cachedOccupiedRight = occupiedRight;
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (COMException) { }
            }
        }

        private static bool ValidTaskbarBounds(Rect bounds, RECT tray)
        {
            return !bounds.IsEmpty && bounds.Width >= 8 && bounds.Height >= 8 &&
                bounds.Left >= tray.Left - 2 && bounds.Right <= tray.Right + 2 &&
                bounds.Bottom > tray.Top && bounds.Top < tray.Bottom;
        }

        private static bool SameRect(RECT a, RECT b)
        {
            return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
        }

        private static bool HasFullscreenForeground(RECT monitor, IntPtr tray)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == tray || foreground == overlayHandle ||
                !IsWindowVisible(foreground) || IsIconic(foreground)) return false;
            StringBuilder name = new StringBuilder(128);
            GetClassName(foreground, name, name.Capacity);
            string windowClass = name.ToString();
            if (windowClass == "Progman" || windowClass == "WorkerW" || windowClass == "Shell_TrayWnd") return false;
            RECT rect;
            if (!GetWindowRect(foreground, out rect)) return false;
            return rect.Left <= monitor.Left + 2 && rect.Top <= monitor.Top + 2 &&
                rect.Right >= monitor.Right - 2 && rect.Bottom >= monitor.Bottom - 2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage, uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
        }
        private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
