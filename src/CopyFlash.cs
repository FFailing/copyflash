using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("CopyFlash")]
[assembly: AssemblyDescription("Local clipboard feedback for Windows")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

namespace CopyFlash
{
    static class Program
    {
        internal static string StatusFile;
        internal static void Log(string message) { if (StatusFile != null) { try { File.AppendAllText(StatusFile, message + Environment.NewLine); } catch (IOException) { } } }
        [STAThread]
        static int Main(string[] args)
        {
            int logIndex = Array.IndexOf(args, "--status-file");
            if (logIndex >= 0 && logIndex + 1 < args.Length) StatusFile = args[logIndex + 1];
            Log("startup; tray-only=" + (Array.IndexOf(args, "--tray") >= 0));
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            if (args.Length > 0 && args[0] == "--probe") return SelectionProbe.Run(args);
            if (args.Length > 0 && args[0] == "--self-test") return SelfTest.Run();
            bool first;
            using (Mutex mutex = new Mutex(true, @"Local\CopyFlash-0.1", out first))
            {
                Log("single-instance=" + first);
                if (!first) { MessageBox.Show("CopyFlash 已在托盘运行。双击托盘图标可打开实验窗口。", "CopyFlash"); return 0; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (FlashApp app = new FlashApp(Array.IndexOf(args, "--tray") < 0))
                    Application.Run(app);
                mutex.ReleaseMutex();
            }
            return 0;
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll")] internal static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint color, byte alpha, uint flags);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [StructLayout(LayoutKind.Sequential)] internal struct WindowRect
        {
            public int Left, Top, Right, Bottom;
            internal Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }
        internal static uint Pid(IntPtr hwnd) { uint id; GetWindowThreadProcessId(hwnd, out id); return id; }
    }

    // No GetClipboardData, clipboard text retrieval, keyboard hooks, network or persistence.
    sealed class ClipboardHost : Form
    {
        internal event Action ClipboardChanged;
        internal bool Registered;
        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Registered = Native.AddClipboardFormatListener(Handle);
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (Registered) Native.RemoveClipboardFormatListener(Handle);
            Registered = false;
            base.OnHandleDestroyed(e);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x031D && ClipboardChanged != null) ClipboardChanged();
            base.WndProc(ref m);
        }
    }

    internal static class SelectionProbe
    {
        // Run UI Automation in a disposable helper process. A slow provider cannot hang the tray.
        internal static int Run(string[] args)
        {
            if (args.Length != 3) return 2;
            uint processId;
            long windowId;
            if (!uint.TryParse(args[1], out processId) || !long.TryParse(args[2], out windowId)) return 2;
            IntPtr foreground = new IntPtr(windowId);
            if (Native.GetForegroundWindow() != foreground || Native.Pid(foreground) != processId) return 0;
            string result = "";
            Thread worker = new Thread(delegate()
            {
                try { result = Find(processId, foreground); }
                catch (Exception) { result = ""; }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            if (!worker.Join(900)) return 3;
            Console.Write(result);
            return 0;
        }

        static string Find(uint pid, IntPtr foreground)
        {
            AutomationElement focused = AutomationElement.FocusedElement;
            if (focused != null && focused.Current.ProcessId == pid)
            {
                if (focused.Current.IsPassword) return "SKIP";
                AutomationElement current = focused;
                for (int depth = 0; current != null && depth < 8; depth++)
                {
                    if (current.Current.ProcessId != pid) break;
                    string rectangles = Read(current);
                    if (rectangles.Length != 0) return rectangles;
                    current = TreeWalker.ControlViewWalker.GetParent(current);
                }
            }
            // Some applications keep keyboard focus on a container instead of their document.
            AutomationElement root = AutomationElement.FromHandle(foreground);
            AutomationElement document = root.FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true),
                                 new PropertyCondition(AutomationElement.IsPasswordProperty, false)));
            return document == null ? "" : Read(document);
        }

        static string Read(AutomationElement element)
        {
            object value;
            if (element.Current.IsPassword || !element.TryGetCurrentPattern(TextPattern.Pattern, out value)) return "";
            TextPattern pattern = (TextPattern)value;
            if (pattern.SupportedTextSelection == SupportedTextSelection.None) return "";
            TextPatternRange[] selection = pattern.GetSelection();
            if (selection == null) return "";
            StringBuilder result = new StringBuilder();
            int count = 0;
            foreach (TextPatternRange range in selection)
            {
                if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0) continue;
                foreach (System.Windows.Rect rectangle in range.GetBoundingRectangles())
                {
                    if (rectangle.IsEmpty || double.IsNaN(rectangle.X) || double.IsInfinity(rectangle.X) ||
                        double.IsNaN(rectangle.Y) || double.IsInfinity(rectangle.Y) ||
                        rectangle.Width <= 0 || rectangle.Height <= 0 ||
                        rectangle.Width > 20000 || rectangle.Height > 20000) continue;
                    result.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3}\n",
                        Math.Floor(rectangle.Left), Math.Floor(rectangle.Top),
                        Math.Ceiling(rectangle.Right) - Math.Floor(rectangle.Left),
                        Math.Ceiling(rectangle.Bottom) - Math.Floor(rectangle.Top));
                    if (++count >= 48) return result.ToString();
                }
            }
            return result.ToString();
        }

        internal static string Query(uint pid, IntPtr window)
        {
            ProcessStartInfo start = new ProcessStartInfo(Application.ExecutablePath,
                "--probe " + pid.ToString(CultureInfo.InvariantCulture) + " " + window.ToInt64().ToString(CultureInfo.InvariantCulture));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using (Process helper = Process.Start(start))
            {
                if (!helper.WaitForExit(1100))
                {
                    try { helper.Kill(); helper.WaitForExit(300); } catch (InvalidOperationException) { }
                    return "";
                }
                return helper.ExitCode == 0 ? helper.StandardOutput.ReadToEnd() : "";
            }
        }

        internal static List<Rectangle> Parse(string text, Rectangle allowed)
        {
            List<Rectangle> rectangles = new List<Rectangle>();
            HashSet<string> seen = new HashSet<string>();
            long pixels = 0;
            foreach (string line in text.Split('\n'))
            {
                string[] fields = line.Trim().Split(',');
                int x, y, w, h;
                if (fields.Length != 4 || !int.TryParse(fields[0], out x) || !int.TryParse(fields[1], out y) ||
                    !int.TryParse(fields[2], out w) || !int.TryParse(fields[3], out h) ||
                    Math.Abs((long)x) > 100000 || Math.Abs((long)y) > 100000 || w <= 0 || h <= 0 || w > 20000 || h > 20000) continue;
                Rectangle clipped = Rectangle.Intersect(new Rectangle(x, y, w, h), allowed);
                if (clipped.Width < 1 || clipped.Height < 1 || !seen.Add(clipped.ToString())) continue;
                pixels += (long)clipped.Width * clipped.Height;
                if (pixels > 8000000 || rectangles.Count >= 48) break;
                rectangles.Add(clipped);
            }
            return rectangles;
        }
    }

    sealed class CopyRequest
    {
        internal IntPtr Window;
        internal uint Pid, Sequence;
        internal int Generation;
        internal Rectangle WindowBounds;
    }

    sealed class FlashApp : ApplicationContext
    {
        readonly ClipboardHost host = new ClipboardHost();
        readonly NotifyIcon tray;
        readonly List<Form> overlays = new List<Form>();
        readonly System.Windows.Forms.Timer lifetime = new System.Windows.Forms.Timer();
        readonly Stopwatch elapsed = new Stopwatch();
        readonly List<ToolStripMenuItem> durations = new List<ToolStripMenuItem>();
        readonly ToolStripMenuItem pause;
        DemoWindow demo;
        CopyRequest pending, displayed;
        bool busy, enabled = true;
        int generation, duration = 300;
        uint lastSequence;

        internal FlashApp(bool showDemo)
        {
            IntPtr handle = host.Handle;
            Program.Log("clipboard listener=" + host.Registered);
            if (!host.Registered) throw new InvalidOperationException("无法注册 Windows 剪贴板通知。");
            lastSequence = Native.GetClipboardSequenceNumber();
            host.ClipboardChanged += OnClipboardChanged;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开实验窗口", null, delegate { OpenDemo(); });
            pause = new ToolStripMenuItem("暂停闪色", null, delegate { SetEnabled(!enabled); });
            menu.Items.Add(pause);
            ToolStripMenuItem time = new ToolStripMenuItem("闪色时长");
            foreach (int milliseconds in new int[] { 300, 600, 900 })
            {
                int value = milliseconds;
                ToolStripMenuItem item = new ToolStripMenuItem(value + " ms");
                item.Tag = value;
                item.Checked = value == duration;
                item.Click += delegate { SetDuration(value); };
                durations.Add(item);
                time.DropDownItems.Add(item);
            }
            menu.Items.Add(time);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关于与兼容性", null, delegate {
                MessageBox.Show("CopyFlash 闪拷 0.1.0 实验版\n\n文字进入剪贴板后，在可取得选区位置的软件中短暂反色。\n无法定位时显示提示。兼容性因软件而异。\n\n不读取或保存剪贴板正文，不联网。\n反色时仅在内存中临时处理选区像素。\n不接管 Ctrl+C，不设置开机启动。\n\n双击托盘图标打开实验窗口；退出后停止监听。", "CopyFlash");
            });
            menu.Items.Add("退出 CopyFlash", null, delegate { ExitThread(); });
            tray = new NotifyIcon { Text = "CopyFlash 闪拷 · 300 ms", Icon = SystemIcons.Information, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { OpenDemo(); };
            lifetime.Interval = 30;
            lifetime.Tick += delegate {
                Native.WindowRect rect;
                if (displayed == null || elapsed.ElapsedMilliseconds >= duration ||
                    Native.GetForegroundWindow() != displayed.Window ||
                    !Native.GetWindowRect(displayed.Window, out rect) || rect.ToRectangle() != displayed.WindowBounds ||
                    Native.GetAsyncKeyState(0x1B) < 0 || Native.GetAsyncKeyState(0x01) < 0)
                    ClearOverlays();
            };
            if (showDemo) OpenDemo();
        }

        internal void SetDuration(int milliseconds)
        {
            if (milliseconds != 300 && milliseconds != 600 && milliseconds != 900) return;
            duration = milliseconds;
            foreach (ToolStripMenuItem item in durations) item.Checked = (int)item.Tag == duration;
            tray.Text = "CopyFlash 闪拷 · " + duration + " ms";
        }

        internal void SetEnabled(bool value)
        {
            enabled = value;
            pause.Checked = !enabled;
            generation++;
            pending = null;
            ClearOverlays();
            if (demo != null && !demo.IsDisposed) demo.SetStatus(enabled ? "已启用 · 等待复制" : "已暂停");
        }

        void OpenDemo()
        {
            Program.Log("opening demo");
            if (demo == null || demo.IsDisposed) demo = new DemoWindow(this);
            demo.Show();
            demo.WindowState = FormWindowState.Normal;
            demo.Activate();
            Program.Log("demo visible=" + demo.Visible);
        }

        void OnClipboardChanged()
        {
            uint sequence = Native.GetClipboardSequenceNumber();
            if (sequence == lastSequence) return;
            lastSequence = sequence;
            generation++;
            pending = null;
            ClearOverlays();
            if (!enabled || !Native.IsClipboardFormatAvailable(13)) return;
            IntPtr foreground = Native.GetForegroundWindow();
            uint pid = Native.Pid(foreground);
            uint owner = Native.Pid(Native.GetClipboardOwner());
            // Unknown/background sources are ignored rather than attributed to the wrong app.
            if (foreground == IntPtr.Zero || pid == 0 || owner != pid) return;
            Native.WindowRect bounds;
            if (!Native.GetWindowRect(foreground, out bounds)) return;
            pending = new CopyRequest { Window = foreground, Pid = pid, Sequence = sequence,
                Generation = generation, WindowBounds = bounds.ToRectangle() };
            StartPending();
        }

        void StartPending()
        {
            if (busy || pending == null || host.IsDisposed) return;
            CopyRequest request = pending;
            pending = null;
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate {
                string result = "";
                try { result = SelectionProbe.Query(request.Pid, request.Window); } catch (Exception) { }
                try {
                    host.BeginInvoke((Action)delegate {
                        busy = false;
                        if (enabled && request.Generation == generation && request.Sequence == Native.GetClipboardSequenceNumber() &&
                            request.Window == Native.GetForegroundWindow() && result != "SKIP")
                            ShowFeedback(request, result);
                        StartPending();
                    });
                } catch (InvalidOperationException) { }
            });
        }

        void ShowFeedback(CopyRequest request, string result)
        {
            ClearOverlays();
            Native.WindowRect currentBounds;
            if (!Native.GetWindowRect(request.Window, out currentBounds) || currentBounds.ToRectangle() != request.WindowBounds) return;
            Rectangle allowed = Rectangle.Intersect(request.WindowBounds, SystemInformation.VirtualScreen);
            List<Rectangle> rectangles = SelectionProbe.Parse(result, allowed);
            List<FlashWindow> prepared = new List<FlashWindow>();
            try {
                // Capture every rectangle before showing any overlay, so overlapping lines are never captured twice.
                foreach (Rectangle rectangle in rectangles)
                    prepared.Add(new FlashWindow(rectangle, PixelEffects.CaptureInverted(rectangle)));
            } catch (Exception) {
                foreach (FlashWindow window in prepared) window.Dispose();
                prepared.Clear();
                rectangles.Clear();
            }
            if (request.Window != Native.GetForegroundWindow()) {
                foreach (FlashWindow window in prepared) window.Dispose();
                return;
            }
            if (prepared.Count == 0) {
                ToastWindow toast = new ToastWindow();
                Point point = Cursor.Position;
                Rectangle area = Screen.FromPoint(point).WorkingArea;
                toast.Location = new Point(Math.Max(area.Left, Math.Min(point.X + 16, area.Right - toast.Width)),
                    Math.Max(area.Top, Math.Min(point.Y + 20, area.Bottom - toast.Height)));
                overlays.Add(toast);
                toast.Show();
            } else {
                foreach (FlashWindow window in prepared) { overlays.Add(window); window.Show(); }
            }
            displayed = request;
            Program.Log("feedback rectangles=" + prepared.Count);
            elapsed.Restart();
            lifetime.Start();
            if (demo != null && !demo.IsDisposed)
                demo.SetStatus(prepared.Count > 0 ? "原文反色 · " + rectangles.Count + " 个选区矩形 · " + duration + " ms" : "已提示 · 当前控件未提供选区位置");
        }

        void ClearOverlays()
        {
            lifetime.Stop();
            foreach (Form window in overlays) { window.Close(); window.Dispose(); }
            overlays.Clear();
            displayed = null;
        }

        protected override void ExitThreadCore()
        {
            generation++;
            pending = null;
            ClearOverlays();
            if (demo != null) demo.Dispose();
            tray.Visible = false;
            base.ExitThreadCore();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                lifetime.Dispose();
                tray.ContextMenuStrip.Dispose();
                tray.Dispose();
                host.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class PixelEffects
    {
        internal static Bitmap Invert(Bitmap source)
        {
            Bitmap result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(result))
            using (ImageAttributes attributes = new ImageAttributes()) {
                attributes.SetColorMatrix(new ColorMatrix(new float[][] {
                    new float[] {-1,0,0,0,0}, new float[] {0,-1,0,0,0}, new float[] {0,0,-1,0,0},
                    new float[] {0,0,0,1,0}, new float[] {1,1,1,0,1} }));
                graphics.DrawImage(source, new Rectangle(0,0,source.Width,source.Height), 0,0,source.Width,source.Height, GraphicsUnit.Pixel, attributes);
            }
            return result;
        }
        internal static Bitmap CaptureInverted(Rectangle rectangle)
        {
            // GDI screen copies do not guarantee a valid alpha channel. Capture RGB, then invert into opaque ARGB.
            using (Bitmap original = new Bitmap(rectangle.Width, rectangle.Height, PixelFormat.Format24bppRgb)) {
                using (Graphics graphics = Graphics.FromImage(original))
                    graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size, CopyPixelOperation.SourceCopy);
                return Invert(original);
            }
        }
    }

    class PassiveWindow : Form
    {
        internal PassiveWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 0x00080000; return p; }
        }
        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            Native.SetLayeredWindowAttributes(Handle, 0, 255, 2);
        }
        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; }
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
            base.WndProc(ref m);
        }
    }
    sealed class FlashWindow : PassiveWindow
    {
        readonly Bitmap image;
        internal FlashWindow(Rectangle rectangle, Bitmap pixels) {
            Bounds = rectangle;
            image = pixels;
            DoubleBuffered = true;
        }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.DrawImageUnscaled(image, Point.Empty); }
        protected override void Dispose(bool disposing) { if (disposing) image.Dispose(); base.Dispose(disposing); }
    }
    sealed class ToastWindow : PassiveWindow
    {
        internal ToastWindow() { Size = new Size(190,40); BackColor = Color.FromArgb(30,34,39); ForeColor = Color.White; }
        protected override void OnPaint(PaintEventArgs e) {
            TextRenderer.DrawText(e.Graphics, "文字已进入剪贴板", SystemFonts.MessageBoxFont, ClientRectangle,
                ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    sealed class DemoWindow : Form
    {
        readonly Label status = new Label();
        internal DemoWindow(FlashApp app)
        {
            Text = "CopyFlash 闪拷 · Windows 实验版";
            ClientSize = new Size(760,650);
            MinimumSize = new Size(520,690);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 10F);
            BackColor = Color.FromArgb(246,247,249);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9 };
            int[] heights = { 48, 38, 220, 52, 32, 30, 110, 28, 30 };
            foreach (int height in heights) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Label heading = new Label { Text = "复制，看得见。", Font = new Font(Font.FontFamily, 20F), Dock = DockStyle.Fill, AutoSize = false };
            Label hint = new Label { Text = "选中下面的文字，按 Ctrl+C；复制后选区会短暂反色。", Dock = DockStyle.Fill };
            RichTextBox source = new RichTextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Font = new Font(Font.FontFamily, 14F), HideSelection = false, DetectUrls = false, AccessibleName = "复制实验文字区" };
            source.Text = "有时候，按完复制，总想再确认一次。\n\n让刚复制的文字，短暂亮一下。\n\n可以选中几个字，也可以跨行选中整段。这里是原生 Windows 控件，复制仍由 Windows 和当前软件完成。";
            FlowLayoutPanel controls = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0,7,0,0) };
            Button copy = new Button { Text = "复制所选文字", AutoSize = true };
            copy.Click += delegate { source.Focus(); if (source.SelectionLength > 0) source.Copy(); };
            ComboBox duration = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105 };
            duration.Items.AddRange(new object[] { "300 ms", "600 ms", "900 ms" });
            duration.SelectedIndex = 0;
            duration.SelectedIndexChanged += delegate { app.SetDuration(new int[] {300,600,900}[duration.SelectedIndex]); };
            controls.Controls.Add(copy);
            controls.Controls.Add(duration);
            CheckBox pause = new CheckBox { Text = "暂停闪色", AutoSize = true, Margin = new Padding(12,4,0,0) };
            pause.CheckedChanged += delegate { app.SetEnabled(!pause.Checked); };
            controls.Controls.Add(pause);
            Button exit = new Button { Text = "退出(&X)", AutoSize = true };
            exit.Click += delegate { app.ExitThread(); };
            controls.Controls.Add(exit);
            status.Text = "已启用 · 等待复制";
            status.Dock = DockStyle.Fill;
            status.ForeColor = Color.FromArgb(36,111,80);
            Label pasteLabel = new Label { Text = "粘贴验证：点击下面的框，按 Ctrl+V", Dock = DockStyle.Fill };
            RichTextBox paste = new RichTextBox { Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 11F), AccessibleName = "粘贴验证区" };
            Label limitation = new Label { Text = "兼容性因软件而异；无法定位选区时，显示剪贴板提示。", Dock = DockStyle.Fill, ForeColor = Color.DimGray };
            Label footer = new Label { Text = "关闭窗口后仍在托盘运行。右键托盘 → 退出，停止监听。", Dock = DockStyle.Fill, ForeColor = Color.DimGray };
            layout.Controls.Add(heading,0,0); layout.Controls.Add(hint,0,1); layout.Controls.Add(source,0,2);
            layout.Controls.Add(controls,0,3); layout.Controls.Add(status,0,4); layout.Controls.Add(pasteLabel,0,5);
            layout.Controls.Add(paste,0,6); layout.Controls.Add(limitation,0,7); layout.Controls.Add(footer,0,8);
            Controls.Add(layout);
            Shown += delegate { source.Focus(); source.Select(0,0); };
        }
        internal void SetStatus(string value) { status.Text = value; }
    }

    internal static class SelfTest
    {
        internal static int Run()
        {
            try {
                using (Bitmap input = new Bitmap(2,2,PixelFormat.Format24bppRgb)) {
                    input.SetPixel(0,0,Color.FromArgb(10,20,30));
                    using (Bitmap inverted = PixelEffects.Invert(input)) {
                        Color pixel = inverted.GetPixel(0,0);
                        if (pixel.R != 245 || pixel.G != 235 || pixel.B != 225 || pixel.A != 255) throw new Exception("opaque RGB inversion");
                    }
                }
                List<Rectangle> rects = SelectionProbe.Parse("-5,-5,20,20\n-5,-5,20,20\n0,0,-1,5\n99999999,0,5,5\nbad\n", new Rectangle(0,0,100,100));
                if (rects.Count != 1 || rects[0] != new Rectangle(0,0,15,15)) throw new Exception("rectangle clipping/deduplication");
                if (SelectionProbe.Parse("0,0,20000,20000",new Rectangle(0,0,20000,20000)).Count != 0) throw new Exception("capture memory cap");
                using (ClipboardHost listener = new ClipboardHost()) {
                    IntPtr handle = listener.Handle;
                    if (!listener.Registered) throw new Exception("clipboard listener registration");
                }
                Console.WriteLine("PASS: RGB inversion; geometry clipping; duplicate/invalid bounds; capture memory cap; clipboard listener lifecycle.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
        }
    }
}
