using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Codex Quota Overlay")]
[assembly: System.Reflection.AssemblyDescription("Windows overlay for remaining Codex Plus quotas")]
[assembly: System.Reflection.AssemblyCompany("Local")]
[assembly: System.Reflection.AssemblyProduct("Codex Quota Overlay")]
[assembly: System.Reflection.AssemblyVersion("1.3.4.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.4.0")]

internal static class Program
{
    internal static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaOverlay");

    internal static readonly string LogPath = Path.Combine(DataDirectory, "overlay.log");

    [STAThread]
    private static int Main(string[] args)
    {
        bool createdNew;
        using (Mutex mutex = new Mutex(true, "Local\\CodexQuotaOverlay.Native", out createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show("Codex Plus 한도 오버레이가 이미 실행 중입니다.", "Codex Plus 한도");
                return 0;
            }

            try
            {
                Directory.CreateDirectory(DataDirectory);
                SetCurrentProcessExplicitAppUserModelID("Local.CodexQuotaOverlay");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool validateOnly = args.Any(a => string.Equals(a, "--validate", StringComparison.OrdinalIgnoreCase));
                bool liveCheck = args.Any(a => string.Equals(a, "--live-check", StringComparison.OrdinalIgnoreCase));

                using (OverlayForm form = new OverlayForm(liveCheck))
                {
                    if (validateOnly)
                    {
                        form.ValidateComponents();
                        return 0;
                    }

                    Application.Run(form);
                    return form.ExitCode;
                }
            }
            catch (Exception exception)
            {
                Log("Fatal", exception);
                MessageBox.Show(
                    "Codex Plus 한도 오버레이를 실행하지 못했습니다.\r\n\r\n" + exception.Message +
                    "\r\n\r\n로그: " + LogPath,
                    "Codex Plus 한도",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    internal static void Log(string context, Exception exception)
    {
        Log(context + ": " + exception);
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(
                LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

internal sealed class OverlayForm : Form
{
    private const int WidthPixels = 330;
    private const int HeightPixels = 290;
    private const int FiveHourSectionTop = 28;
    private const int WeeklySectionTop = 156;
    private const int SectionSeparatorY = 153;
    private const int MinimumPercentageTrackGap = 0;
    private const int WmNclButtonDown = 0x00A1;
    private const int HtCaption = 2;

    private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
    private readonly System.Windows.Forms.Timer _refreshTimer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer _displayTimer = new System.Windows.Forms.Timer();
    private readonly bool _liveCheck;
    private readonly Rectangle _refreshBounds = new Rectangle(276, 0, 25, 25);
    private readonly Rectangle _closeBounds = new Rectangle(301, 0, 25, 25);
    private readonly string _settingsPath = Path.Combine(Program.DataDirectory, "settings.txt");

    private Process _codexProcess;
    private bool _initialized;
    private bool _closing;
    private int _nextRequestId = 10;
    private int _pendingRequestId = -1;
    private DateTime _lastRequestAt = DateTime.MinValue;
    private DateTime _liveDeadline;
    private string _stderrTail = string.Empty;

    private double? _weeklyRemainingPercent;
    private DateTimeOffset? _weeklyResetsAt;
    private double? _fiveHourRemainingPercent;
    private DateTimeOffset? _fiveHourResetsAt;
    private string _statusMessage = "Codex에 연결하고 있습니다…";
    private Color _weeklyAccent = Color.FromArgb(245, 158, 11);
    private Color _fiveHourAccent = Color.FromArgb(245, 158, 11);

    internal int ExitCode { get; private set; }

    internal OverlayForm(bool liveCheck)
    {
        _liveCheck = liveCheck;
        ExitCode = 0;

        Text = "Codex Plus 한도";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(WidthPixels, HeightPixels);
        MinimumSize = new Size(WidthPixels, HeightPixels);
        MaximumSize = new Size(WidthPixels, HeightPixels);
        BackColor = Color.FromArgb(18, 24, 36);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        DoubleBuffered = true;
        KeyPreview = true;
        Opacity = 0.98;
        Font = new Font("Segoe UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);

        RestorePosition();
        UpdateRoundedRegion();
        ValidateLayout();

        _refreshTimer.Interval = 60000;
        _refreshTimer.Tick += delegate { RefreshQuota(); };
        _displayTimer.Interval = 1000;
        _displayTimer.Tick += DisplayTimerTick;

        MouseDown += OverlayMouseDown;
        MouseUp += OverlayMouseUp;
        MouseMove += OverlayMouseMove;
        FormClosing += OverlayFormClosing;
        KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000;
            return parameters;
        }
    }

    internal void ValidateComponents()
    {
        Dictionary<string, object> parsed = AsDictionary(_json.DeserializeObject(
            "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":6,\"windowDurationMins\":300,\"resetsAt\":2000000000},\"secondary\":{\"usedPercent\":25,\"windowDurationMins\":10080,\"resetsAt\":2000500000}}}"));
        QuotaSnapshot snapshot = ParseSnapshot(parsed);
        if (snapshot.FiveHour == null || !snapshot.IsWeekly || ClientSize.Width != WidthPixels || ClientSize.Height != HeightPixels)
        {
            throw new InvalidOperationException("오버레이 구성 요소 검증에 실패했습니다.");
        }
        ValidateLayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_liveCheck)
        {
            Hide();
            _liveDeadline = DateTime.Now.AddSeconds(30);
        }

        _refreshTimer.Start();
        _displayTimer.Start();
        BeginInvoke(new Action(StartCodexServer));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (LinearGradientBrush background = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(27, 36, 52),
            Color.FromArgb(16, 22, 33),
            LinearGradientMode.ForwardDiagonal))
        {
            graphics.FillRectangle(background, ClientRectangle);
        }

        using (Pen border = new Pen(Color.FromArgb(51, 68, 93), 1f))
        {
            graphics.DrawRectangle(border, 0, 0, WidthPixels - 1, HeightPixels - 1);
        }

        using (SolidBrush dotBrush = new SolidBrush(_weeklyAccent))
        {
            graphics.FillEllipse(dotBrush, 18, 10, 7, 7);
        }

        DrawText(graphics, "CODEX · Plus 한도", new Font("Segoe UI Semibold", 9f),
            Color.FromArgb(200, 210, 225), new Rectangle(33, 2, 220, 24), TextFormatFlags.VerticalCenter);
        DrawHeaderControls(graphics);

        DrawQuotaSection(graphics, "5시간 한도", _fiveHourRemainingPercent, _fiveHourResetsAt, _fiveHourAccent, FiveHourSectionTop);
        using (Pen separator = new Pen(Color.FromArgb(43, 58, 79), 1f))
        {
            graphics.DrawLine(separator, 18, SectionSeparatorY, 312, SectionSeparatorY);
        }
        DrawQuotaSection(graphics, "주간 한도", _weeklyRemainingPercent, _weeklyResetsAt, _weeklyAccent, WeeklySectionTop);
    }

    private void DrawQuotaSection(Graphics graphics, string label, double? remainingPercent, DateTimeOffset? resetsAt, Color accent, int top)
    {
        DrawText(graphics, label, new Font("Segoe UI Semibold", 8.5f), Color.FromArgb(176, 190, 209),
            SectionLabelBounds(top), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        DrawText(graphics, remainingPercent.HasValue ? "실시간" : "정보 없음", new Font("Segoe UI", 8f), Color.FromArgb(112, 129, 152),
            LiveStatusBounds(top), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        string percent = remainingPercent.HasValue ? remainingPercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "--";
        DrawText(graphics, percent, new Font("Segoe UI Semibold", 19f), Color.FromArgb(248, 250, 252),
            PercentageBounds(top), TextFormatFlags.Left | TextFormatFlags.Top);
        DrawText(graphics, "남음", new Font("Segoe UI", 9f), Color.FromArgb(143, 160, 183),
            RemainingLabelBounds(top), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        Rectangle track = ProgressTrackBounds(top);
        using (GraphicsPath trackPath = RoundedRectangle(track, 4))
        using (SolidBrush trackBrush = new SolidBrush(Color.FromArgb(38, 50, 71)))
        {
            graphics.FillPath(trackBrush, trackPath);
        }
        if (remainingPercent.HasValue)
        {
            int fillWidth = Math.Max(1, (int)Math.Round(track.Width * Math.Max(0, Math.Min(100, remainingPercent.Value)) / 100.0));
            Rectangle fill = new Rectangle(track.X, track.Y, fillWidth, track.Height);
            using (GraphicsPath fillPath = RoundedRectangle(fill, 4))
            using (SolidBrush fillBrush = new SolidBrush(accent))
            {
                graphics.FillPath(fillBrush, fillPath);
            }
        }
        DrawText(graphics, GetResetText(resetsAt), new Font("Segoe UI", 8f), Color.FromArgb(143, 160, 183),
            ResetTextBounds(top), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawHeaderControls(Graphics graphics)
    {
        Color controlColor = Color.FromArgb(154, 167, 187);
        using (Pen pen = new Pen(controlColor, 1.7f))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;

            RectangleF refreshArc = new RectangleF(282.5f, 6.0f, 12.0f, 12.0f);
            graphics.DrawArc(pen, refreshArc, 32f, 286f);
            graphics.DrawLine(pen, 292.8f, 5.6f, 295.5f, 5.8f);
            graphics.DrawLine(pen, 295.5f, 5.8f, 295.0f, 8.5f);

            graphics.DrawLine(pen, 309.0f, 8.0f, 318.0f, 17.0f);
            graphics.DrawLine(pen, 318.0f, 8.0f, 309.0f, 17.0f);
        }
    }

    private static Rectangle SectionLabelBounds(int top)
    {
        return new Rectangle(18, top, 110, 20);
    }

    private static Rectangle LiveStatusBounds(int top)
    {
        return new Rectangle(240, top, 70, 20);
    }

    private static Rectangle PercentageBounds(int top)
    {
        return new Rectangle(18, top + 35, 140, 52);
    }

    private static Rectangle RemainingLabelBounds(int top)
    {
        return new Rectangle(164, top + 43, 48, 20);
    }

    private static Rectangle ProgressTrackBounds(int top)
    {
        return new Rectangle(18, top + 87, 294, 7);
    }

    private static Rectangle ResetTextBounds(int top)
    {
        return new Rectangle(18, top + 100, 294, 24);
    }

    private void ValidateLayout()
    {
        List<KeyValuePair<string, Rectangle>> components = new List<KeyValuePair<string, Rectangle>>
        {
            new KeyValuePair<string, Rectangle>("상태 점", new Rectangle(18, 10, 7, 7)),
            new KeyValuePair<string, Rectangle>("제목", new Rectangle(33, 2, 220, 24)),
            new KeyValuePair<string, Rectangle>("새로고침", _refreshBounds),
            new KeyValuePair<string, Rectangle>("닫기", _closeBounds),
            new KeyValuePair<string, Rectangle>("5시간 제목", SectionLabelBounds(FiveHourSectionTop)),
            new KeyValuePair<string, Rectangle>("5시간 상태", LiveStatusBounds(FiveHourSectionTop)),
            new KeyValuePair<string, Rectangle>("5시간 비율", PercentageBounds(FiveHourSectionTop)),
            new KeyValuePair<string, Rectangle>("5시간 남음", RemainingLabelBounds(FiveHourSectionTop)),
            new KeyValuePair<string, Rectangle>("5시간 진행도", ProgressTrackBounds(FiveHourSectionTop)),
            new KeyValuePair<string, Rectangle>("5시간 초기화", ResetTextBounds(FiveHourSectionTop)),
            new KeyValuePair<string, Rectangle>("구분선", new Rectangle(18, SectionSeparatorY, 294, 1)),
            new KeyValuePair<string, Rectangle>("주간 제목", SectionLabelBounds(WeeklySectionTop)),
            new KeyValuePair<string, Rectangle>("주간 상태", LiveStatusBounds(WeeklySectionTop)),
            new KeyValuePair<string, Rectangle>("주간 비율", PercentageBounds(WeeklySectionTop)),
            new KeyValuePair<string, Rectangle>("주간 남음", RemainingLabelBounds(WeeklySectionTop)),
            new KeyValuePair<string, Rectangle>("주간 진행도", ProgressTrackBounds(WeeklySectionTop)),
            new KeyValuePair<string, Rectangle>("주간 초기화", ResetTextBounds(WeeklySectionTop))
        };

        Rectangle clientBounds = new Rectangle(0, 0, WidthPixels, HeightPixels);
        List<string> problems = new List<string>();
        for (int index = 0; index < components.Count; index++)
        {
            KeyValuePair<string, Rectangle> current = components[index];
            if (!clientBounds.Contains(current.Value))
            {
                problems.Add(current.Key + "이(가) 창 밖에 있습니다: " + current.Value);
            }

            for (int otherIndex = index + 1; otherIndex < components.Count; otherIndex++)
            {
                KeyValuePair<string, Rectangle> other = components[otherIndex];
                if (current.Value.IntersectsWith(other.Value))
                {
                    problems.Add(current.Key + " ↔ " + other.Key + ": " + Rectangle.Intersect(current.Value, other.Value));
                }
            }
        }

        ValidatePercentageTrackGap(problems, "5시간", FiveHourSectionTop);
        ValidatePercentageTrackGap(problems, "주간", WeeklySectionTop);
        ValidatePercentageTextFits(problems);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("오버레이 좌표 충돌:\r\n" + string.Join("\r\n", problems.ToArray()));
        }
    }

    private static void ValidatePercentageTrackGap(List<string> problems, string sectionName, int top)
    {
        int gap = ProgressTrackBounds(top).Top - PercentageBounds(top).Bottom;
        if (gap < MinimumPercentageTrackGap)
        {
            problems.Add(sectionName + " 비율과 진행도 사이 간격이 부족합니다: " + gap + "px");
        }
    }

    private static void ValidatePercentageTextFits(List<string> problems)
    {
        Rectangle bounds = PercentageBounds(FiveHourSectionTop);
        using (Font font = new Font("Segoe UI Semibold", 19f))
        {
            Size naturalSize = TextRenderer.MeasureText(
                "99.9%",
                font,
                new Size(1000, 1000),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (naturalSize.Width > bounds.Width || naturalSize.Height > bounds.Height)
            {
                problems.Add("비율 글자가 영역보다 큽니다: " + naturalSize + " > " + bounds.Size);
            }
        }
    }

    private static void DrawText(Graphics graphics, string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags)
    {
        using (font)
        {
            TextRenderer.DrawText(graphics, text ?? string.Empty, font, bounds, color, flags | TextFormatFlags.NoPadding);
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        int diameter = radius * 2;
        GraphicsPath path = new GraphicsPath();
        if (rectangle.Width <= diameter || rectangle.Height <= diameter)
        {
            path.AddRectangle(rectangle);
            return path;
        }

        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void OverlayMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !_refreshBounds.Contains(e.Location) && !_closeBounds.Contains(e.Location))
        {
            ReleaseCapture();
            SendMessage(Handle, WmNclButtonDown, HtCaption, 0);
        }
    }

    private void OverlayMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_closeBounds.Contains(e.Location))
        {
            Close();
        }
        else if (_refreshBounds.Contains(e.Location))
        {
            RefreshQuota();
        }
    }

    private void OverlayMouseMove(object sender, MouseEventArgs e)
    {
        Cursor = (_refreshBounds.Contains(e.Location) || _closeBounds.Contains(e.Location)) ? Cursors.Hand : Cursors.SizeAll;
    }

    private void DisplayTimerTick(object sender, EventArgs e)
    {
        if (_pendingRequestId >= 0 && (DateTime.Now - _lastRequestAt).TotalSeconds > 20)
        {
            _pendingRequestId = -1;
            SetError("한도 조회 시간이 초과되었습니다.");
        }

        if (_liveCheck && DateTime.Now > _liveDeadline)
        {
            ExitCode = 1;
            Close();
            return;
        }

        Invalidate();
    }

    private void RefreshQuota()
    {
        try
        {
            if (_codexProcess == null || _codexProcess.HasExited)
            {
                StartCodexServer();
            }
            else
            {
                RequestRateLimits();
            }
        }
        catch (Exception exception)
        {
            Program.Log("RefreshQuota", exception);
            SetError(exception.Message);
            if (_liveCheck)
            {
                ExitCode = 1;
                Close();
            }
        }
    }

    private void StartCodexServer()
    {
        try
        {
            StopCodexServer();
            _statusMessage = "Codex app-server에 연결하고 있습니다…";
            _weeklyRemainingPercent = null;
            _fiveHourRemainingPercent = null;
            _weeklyResetsAt = null;
            _fiveHourResetsAt = null;
            _weeklyAccent = Color.FromArgb(245, 158, 11);
            _fiveHourAccent = _weeklyAccent;
            Invalidate();

            string executable = FindCodexExecutable();
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "app-server",
                WorkingDirectory = Program.DataDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (string.IsNullOrWhiteSpace(startInfo.EnvironmentVariables["CODEX_HOME"]))
            {
                startInfo.EnvironmentVariables["CODEX_HOME"] = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            }

            _codexProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _codexProcess.OutputDataReceived += CodexOutputReceived;
            _codexProcess.ErrorDataReceived += CodexErrorReceived;
            _codexProcess.Exited += CodexProcessExited;

            if (!_codexProcess.Start())
            {
                throw new InvalidOperationException("Codex app-server를 시작하지 못했습니다.");
            }

            _codexProcess.BeginOutputReadLine();
            _codexProcess.BeginErrorReadLine();
            _initialized = false;
            _pendingRequestId = -1;
            _nextRequestId = 10;

            SendMessage(new Dictionary<string, object>
            {
                { "method", "initialize" },
                { "id", 0 },
                { "params", new Dictionary<string, object>
                    {
                        { "clientInfo", new Dictionary<string, object>
                            {
                                { "name", "codex_quota_overlay" },
                                { "title", "Codex Quota Overlay" },
                                { "version", "1.1.0" }
                            }
                        }
                    }
                }
            });
        }
        catch (Exception exception)
        {
            Program.Log("StartCodexServer", exception);
            SetError(exception.Message);
            if (_liveCheck)
            {
                ExitCode = 1;
                Close();
            }
        }
    }

    private void CodexOutputReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data) || _closing || IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action<string>(HandleCodexMessage), e.Data);
        }
        catch
        {
        }
    }

    private void CodexErrorReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            _stderrTail = e.Data.Length > 500 ? e.Data.Substring(e.Data.Length - 500) : e.Data;
        }
    }

    private void CodexProcessExited(object sender, EventArgs e)
    {
        if (_closing || IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(delegate
            {
                if (!_closing)
                {
                    SetError(string.IsNullOrWhiteSpace(_stderrTail) ? "Codex app-server가 종료되었습니다." : _stderrTail);
                }
            }));
        }
        catch
        {
        }
    }

    private void HandleCodexMessage(string line)
    {
        try
        {
            Dictionary<string, object> message = AsDictionary(_json.DeserializeObject(line));
            if (message == null)
            {
                return;
            }

            int id;
            bool hasId = TryGetInt(message, "id", out id);
            if (hasId && id == 0)
            {
                ThrowIfError(message, "Codex 초기화 실패");
                SendMessage(new Dictionary<string, object>
                {
                    { "method", "initialized" },
                    { "params", new Dictionary<string, object>() }
                });
                _initialized = true;
                RequestRateLimits();
                return;
            }

            if (hasId && id == _pendingRequestId)
            {
                _pendingRequestId = -1;
                ThrowIfError(message, "한도 조회 실패");
                Dictionary<string, object> result = GetDictionary(message, "result");
                ApplySnapshot(ParseSnapshot(result));
                return;
            }

            string method = GetString(message, "method");
            if (string.Equals(method, "account/rateLimits/updated", StringComparison.Ordinal))
            {
                Dictionary<string, object> parameters = GetDictionary(message, "params");
                ApplySnapshot(ParseSnapshot(parameters));
            }
        }
        catch (Exception exception)
        {
            Program.Log("HandleCodexMessage", exception);
            SetError(exception.Message);
            if (_liveCheck)
            {
                ExitCode = 1;
                Close();
            }
        }
    }

    private void RequestRateLimits()
    {
        if (!_initialized || _pendingRequestId >= 0 || _codexProcess == null || _codexProcess.HasExited)
        {
            return;
        }

        _pendingRequestId = _nextRequestId++;
        _lastRequestAt = DateTime.Now;
        SendMessage(new Dictionary<string, object>
        {
            { "method", "account/rateLimits/read" },
            { "id", _pendingRequestId }
        });
    }

    private void SendMessage(Dictionary<string, object> message)
    {
        if (_codexProcess == null || _codexProcess.HasExited)
        {
            throw new InvalidOperationException("Codex app-server가 실행 중이 아닙니다.");
        }

        _codexProcess.StandardInput.WriteLine(_json.Serialize(message));
        _codexProcess.StandardInput.Flush();
    }

    private QuotaSnapshot ParseSnapshot(Dictionary<string, object> result)
    {
        if (result == null)
        {
            throw new InvalidOperationException("Codex가 빈 한도 응답을 반환했습니다.");
        }

        List<QuotaWindow> windows = new List<QuotaWindow>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLimitWindows(windows, seen, GetDictionary(result, "rateLimits"), true, "codex");

        Dictionary<string, object> byId = GetDictionary(result, "rateLimitsByLimitId");
        if (byId != null)
        {
            foreach (KeyValuePair<string, object> entry in byId)
            {
                AddLimitWindows(windows, seen, AsDictionary(entry.Value), false, entry.Key);
            }
        }

        if (windows.Count == 0)
        {
            throw new InvalidOperationException("표시 가능한 Codex 한도 창이 없습니다.");
        }

        List<QuotaWindow> weekly = windows.Where(w => w.IsWeekly).ToList();
        QuotaWindow selected = (weekly.Count > 0 ? weekly : windows)
            .OrderByDescending(w => w.IsMain)
            .ThenByDescending(w => w.DurationMins)
            .First();

        QuotaWindow fiveHour = windows
            .Where(w => string.Equals(w.LimitId, selected.LimitId, StringComparison.OrdinalIgnoreCase) && w.IsFiveHour)
            .OrderByDescending(w => w.IsMain)
            .FirstOrDefault();

        return new QuotaSnapshot
        {
            RemainingPercent = selected.RemainingPercent,
            DurationMins = selected.DurationMins,
            ResetsAt = selected.ResetsAt,
            IsWeekly = selected.IsWeekly,
            WindowLabel = WindowLabel(selected.DurationMins),
            FiveHour = fiveHour
        };
    }

    private static void AddLimitWindows(
        List<QuotaWindow> windows,
        HashSet<string> seen,
        Dictionary<string, object> limit,
        bool isMain,
        string fallbackId)
    {
        if (limit == null)
        {
            return;
        }

        string limitId = GetString(limit, "limitId");
        if (string.IsNullOrWhiteSpace(limitId))
        {
            limitId = fallbackId;
        }

        foreach (string slotName in new[] { "primary", "secondary" })
        {
            Dictionary<string, object> slot = GetDictionary(limit, slotName);
            double used;
            double duration;
            if (slot == null || !TryGetDouble(slot, "usedPercent", out used) || !TryGetDouble(slot, "windowDurationMins", out duration))
            {
                continue;
            }

            long resetSeconds;
            DateTimeOffset? reset = null;
            if (TryGetLong(slot, "resetsAt", out resetSeconds))
            {
                reset = UnixSecondsToLocal(resetSeconds);
            }

            string key = limitId + "|" + slotName + "|" + duration.ToString(CultureInfo.InvariantCulture) + "|" + resetSeconds;
            if (!seen.Add(key))
            {
                continue;
            }

            used = Math.Max(0, Math.Min(100, used));
            windows.Add(new QuotaWindow
            {
                LimitId = limitId,
                IsMain = isMain,
                IsWeekly = duration >= 9360 && duration <= 10800,
                IsFiveHour = duration >= 240 && duration <= 360,
                DurationMins = duration,
                RemainingPercent = Math.Max(0, 100 - used),
                ResetsAt = reset
            });
        }
    }

    private void ApplySnapshot(QuotaSnapshot snapshot)
    {
        _weeklyRemainingPercent = Math.Round(snapshot.RemainingPercent, 1);
        _weeklyResetsAt = snapshot.ResetsAt;
        _fiveHourRemainingPercent = snapshot.FiveHour == null ? (double?)null : Math.Round(snapshot.FiveHour.RemainingPercent, 1);
        _fiveHourResetsAt = snapshot.FiveHour == null ? (DateTimeOffset?)null : snapshot.FiveHour.ResetsAt;
        _statusMessage = string.Empty;
        _weeklyAccent = AccentFor(_weeklyRemainingPercent);
        _fiveHourAccent = AccentFor(_fiveHourRemainingPercent);

        Invalidate();
        if (_liveCheck)
        {
            ExitCode = 0;
            Close();
        }
    }

    private void SetError(string message)
    {
        _statusMessage = string.IsNullOrWhiteSpace(message) ? "알 수 없는 오류" : message.Replace(Environment.NewLine, " ");
        _weeklyRemainingPercent = null;
        _fiveHourRemainingPercent = null;
        _weeklyResetsAt = null;
        _fiveHourResetsAt = null;
        _weeklyAccent = Color.FromArgb(248, 113, 113);
        _fiveHourAccent = _weeklyAccent;
        Invalidate();
    }

    private static Color AccentFor(double? remainingPercent)
    {
        if (!remainingPercent.HasValue || remainingPercent.Value <= 10)
        {
            return Color.FromArgb(248, 113, 113);
        }
        if (remainingPercent.Value <= 30)
        {
            return Color.FromArgb(251, 191, 36);
        }
        return Color.FromArgb(33, 212, 155);
    }

    private string GetResetText(DateTimeOffset? resetsAt)
    {
        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            return _statusMessage;
        }
        if (!resetsAt.HasValue)
        {
            return "초기화 시각 정보 없음";
        }

        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan remaining = resetsAt.Value - now;
        if (remaining.TotalSeconds <= 0)
        {
            return "곧 초기화";
        }

        string relative;
        if (remaining.TotalDays >= 1)
        {
            relative = ((int)Math.Floor(remaining.TotalDays)) + "일 " + remaining.Hours + "시간 후";
        }
        else if (remaining.TotalHours >= 1)
        {
            relative = ((int)Math.Floor(remaining.TotalHours)) + "시간 " + remaining.Minutes + "분 후";
        }
        else
        {
            relative = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)) + "분 후";
        }

        return "초기화 " + resetsAt.Value.LocalDateTime.ToString("M월 d일 (ddd) tt h:mm", CultureInfo.GetCultureInfo("ko-KR")) + " · " + relative;
    }

    private string FindCodexExecutable()
    {
        string overridePath = Environment.GetEnvironmentVariable("CODEX_QUOTA_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return MakeExecutableUsable(overridePath);
        }

        string found = FindOnPath("codex.exe") ?? FindOnPath("codex");
        if (!string.IsNullOrWhiteSpace(found) && File.Exists(found))
        {
            return MakeExecutableUsable(found);
        }

        string packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        try
        {
            string packageExecutable = Directory.GetDirectories(packageRoot, "OpenAI.Codex_*")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.Combine(path, "app", "resources", "codex.exe"))
                .FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(packageExecutable))
            {
                return MakeExecutableUsable(packageExecutable);
            }
        }
        catch (Exception exception)
        {
            Program.Log("Package discovery", exception);
        }

        string runtimePath = Path.Combine(Program.DataDirectory, "runtime", "codex.exe");
        if (File.Exists(runtimePath))
        {
            return runtimePath;
        }

        throw new FileNotFoundException("Codex 실행 파일을 찾지 못했습니다. Codex 데스크톱 앱 또는 CLI를 설치하세요.");
    }

    private static string FindOnPath(string fileName)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string entry in path.Split(Path.PathSeparator))
        {
            try
            {
                string directory = entry.Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private string MakeExecutableUsable(string sourcePath)
    {
        string resolved = Path.GetFullPath(sourcePath);
        if (resolved.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return resolved;
        }

        string runtimeDirectory = Path.Combine(Program.DataDirectory, "runtime");
        string runtimePath = Path.Combine(runtimeDirectory, "codex.exe");
        string stampPath = Path.Combine(runtimeDirectory, "source-stamp.txt");
        Directory.CreateDirectory(runtimeDirectory);

        FileInfo source = new FileInfo(resolved);
        string stamp = resolved + "|" + source.Length + "|" + source.LastWriteTimeUtc.Ticks;
        string existingStamp = File.Exists(stampPath) ? File.ReadAllText(stampPath) : null;
        if (!File.Exists(runtimePath) || !string.Equals(stamp, existingStamp, StringComparison.Ordinal))
        {
            _statusMessage = "Microsoft Store용 Codex 런타임을 준비하고 있습니다…";
            Invalidate();
            string staging = Path.Combine(runtimeDirectory, "codex.new.exe");
            File.Copy(resolved, staging, true);
            if (File.Exists(runtimePath))
            {
                File.Delete(runtimePath);
            }
            File.Move(staging, runtimePath);
            File.WriteAllText(stampPath, stamp, new UTF8Encoding(false));
        }

        return runtimePath;
    }

    private void StopCodexServer()
    {
        Process process = _codexProcess;
        _codexProcess = null;
        _initialized = false;
        _pendingRequestId = -1;
        if (process == null)
        {
            return;
        }

        try
        {
            process.OutputDataReceived -= CodexOutputReceived;
            process.ErrorDataReceived -= CodexErrorReceived;
            process.Exited -= CodexProcessExited;
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch { }
                if (!process.WaitForExit(500))
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void OverlayFormClosing(object sender, FormClosingEventArgs e)
    {
        _closing = true;
        _refreshTimer.Stop();
        _displayTimer.Stop();
        if (!_liveCheck)
        {
            SavePosition();
        }
        StopCodexServer();
    }

    private void RestorePosition()
    {
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(work.Right - WidthPixels - 20, work.Bottom - HeightPixels - 20);

        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }
            string[] pieces = File.ReadAllText(_settingsPath).Split(',');
            int x;
            int y;
            if (pieces.Length != 2 || !int.TryParse(pieces[0], out x) || !int.TryParse(pieces[1], out y))
            {
                return;
            }

            Rectangle candidate = new Rectangle(x, y, WidthPixels, HeightPixels);
            if (Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(candidate)))
            {
                Location = new Point(x, y);
            }
        }
        catch (Exception exception)
        {
            Program.Log("RestorePosition", exception);
        }
    }

    private void SavePosition()
    {
        try
        {
            Directory.CreateDirectory(Program.DataDirectory);
            File.WriteAllText(_settingsPath, Left + "," + Top, Encoding.ASCII);
        }
        catch (Exception exception)
        {
            Program.Log("SavePosition", exception);
        }
    }

    private void UpdateRoundedRegion()
    {
        IntPtr regionHandle = CreateRoundRectRgn(0, 0, WidthPixels + 1, HeightPixels + 1, 20, 20);
        Region newRegion = Region.FromHrgn(regionHandle);
        DeleteObject(regionHandle);
        Region oldRegion = Region;
        Region = newRegion;
        if (oldRegion != null)
        {
            oldRegion.Dispose();
        }
    }

    private static void ThrowIfError(Dictionary<string, object> message, string prefix)
    {
        Dictionary<string, object> error = GetDictionary(message, "error");
        if (error != null)
        {
            throw new InvalidOperationException(prefix + ": " + GetString(error, "message"));
        }
    }

    private static Dictionary<string, object> AsDictionary(object value)
    {
        return value as Dictionary<string, object>;
    }

    private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
    {
        object value;
        return source != null && source.TryGetValue(key, out value) ? AsDictionary(value) : null;
    }

    private static string GetString(Dictionary<string, object> source, string key)
    {
        object value;
        return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    private static bool TryGetInt(Dictionary<string, object> source, string key, out int value)
    {
        object raw;
        if (source != null && source.TryGetValue(key, out raw) && raw != null)
        {
            try { value = Convert.ToInt32(raw, CultureInfo.InvariantCulture); return true; } catch { }
        }
        value = 0;
        return false;
    }

    private static bool TryGetLong(Dictionary<string, object> source, string key, out long value)
    {
        object raw;
        if (source != null && source.TryGetValue(key, out raw) && raw != null)
        {
            try { value = Convert.ToInt64(raw, CultureInfo.InvariantCulture); return true; } catch { }
        }
        value = 0;
        return false;
    }

    private static bool TryGetDouble(Dictionary<string, object> source, string key, out double value)
    {
        object raw;
        if (source != null && source.TryGetValue(key, out raw) && raw != null)
        {
            try { value = Convert.ToDouble(raw, CultureInfo.InvariantCulture); return true; } catch { }
        }
        value = 0;
        return false;
    }

    private static DateTimeOffset UnixSecondsToLocal(long seconds)
    {
        DateTime utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        return new DateTimeOffset(utc).ToLocalTime();
    }

    private static string WindowLabel(double durationMins)
    {
        if (durationMins >= 9360 && durationMins <= 10800)
        {
            return "주간 한도";
        }
        if (durationMins >= 1440)
        {
            return Math.Round(durationMins / 1440.0, 1).ToString("0.#") + "일 한도";
        }
        if (durationMins >= 60)
        {
            return Math.Round(durationMins / 60.0, 1).ToString("0.#") + "시간 한도";
        }
        return Math.Round(durationMins, 1).ToString("0.#") + "분 한도";
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}

internal sealed class QuotaWindow
{
    internal string LimitId;
    internal bool IsMain;
    internal bool IsWeekly;
    internal bool IsFiveHour;
    internal double DurationMins;
    internal double RemainingPercent;
    internal DateTimeOffset? ResetsAt;
}

internal sealed class QuotaSnapshot
{
    internal double RemainingPercent;
    internal double DurationMins;
    internal DateTimeOffset? ResetsAt;
    internal bool IsWeekly;
    internal string WindowLabel;
    internal QuotaWindow FiveHour;
}
