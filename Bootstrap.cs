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
[assembly: System.Reflection.AssemblyDescription("Windows overlay for remaining Codex quotas")]
[assembly: System.Reflection.AssemblyCompany("Local")]
[assembly: System.Reflection.AssemblyProduct("Codex Quota Overlay")]
[assembly: System.Reflection.AssemblyVersion("1.4.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.4.0.0")]

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
                MessageBox.Show("Codex 한도 오버레이가 이미 실행 중입니다.", "Codex 한도");
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
                    "Codex 한도 오버레이를 실행하지 못했습니다.\r\n\r\n" + exception.Message +
                    "\r\n\r\n로그: " + LogPath,
                    "Codex 한도",
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
    private const int DualQuotaHeightPixels = 260;
    private const int SingleQuotaHeightPixels = 149;
    private const int FiveHourSectionTop = 30;
    private const int WeeklySectionTop = 141;
    private const int SectionSeparatorY = 138;
    private const int MinimumPercentageTrackGap = 0;
    private const int RemainingAdvanceAdjustment = 2;
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
    private int _pendingRateLimitsRequestId = -1;
    private int _pendingAccountRequestId = -1;
    private DateTime _lastRequestAt = DateTime.MinValue;
    private DateTime _liveDeadline;
    private string _stderrTail = string.Empty;
    private bool _accountCheckCompleted;
    private bool _quotaCheckCompleted;
    private bool _isProPlan;
    private string _detectedPlanType;

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

        Text = "Codex 한도";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(WidthPixels, DualQuotaHeightPixels);
        MinimumSize = new Size(WidthPixels, DualQuotaHeightPixels);
        MaximumSize = new Size(WidthPixels, DualQuotaHeightPixels);
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
        if (snapshot.FiveHour == null || !snapshot.IsWeekly || ClientSize.Width != WidthPixels || ClientSize.Height != DualQuotaHeightPixels)
        {
            throw new InvalidOperationException("오버레이 구성 요소 검증에 실패했습니다.");
        }

        ValidateLayout(false);

        Dictionary<string, object> proAccount = AsDictionary(_json.DeserializeObject(
            "{\"account\":{\"type\":\"chatgpt\",\"email\":null,\"planType\":\"pro\"},\"requiresOpenaiAuth\":true}"));
        string planType = ParsePlanType(proAccount);
        if (!IsProPlan(planType) || !IsProPlan("prolite") || IsProPlan("plus"))
        {
            throw new InvalidOperationException("Pro 요금제 판별 검증에 실패했습니다.");
        }

        ApplyPlanType(planType);
        if (!_isProPlan || ClientSize.Height != SingleQuotaHeightPixels)
        {
            throw new InvalidOperationException("Pro 단일 게이지 창 크기 검증에 실패했습니다.");
        }
        ValidateLayout(true);

        ApplyPlanType("plus");
        if (_isProPlan || ClientSize.Height != DualQuotaHeightPixels)
        {
            throw new InvalidOperationException("비 Pro 요금제 레이아웃 복원 검증에 실패했습니다.");
        }
        ValidateLayout(false);
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
            graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        using (SolidBrush dotBrush = new SolidBrush(_weeklyAccent))
        {
            graphics.FillEllipse(dotBrush, 18, 10, 7, 7);
        }

        DrawText(graphics, "CODEX 한도", new Font("Segoe UI Semibold", 9f),
            Color.FromArgb(200, 210, 225), new Rectangle(33, 2, 220, 24), TextFormatFlags.VerticalCenter);
        DrawHeaderControls(graphics);

        if (_isProPlan)
        {
            DrawQuotaSection(graphics, "주간 한도", _weeklyRemainingPercent, _weeklyResetsAt, _weeklyAccent, FiveHourSectionTop);
        }
        else
        {
            DrawQuotaSection(graphics, "5시간 한도", _fiveHourRemainingPercent, _fiveHourResetsAt, _fiveHourAccent, FiveHourSectionTop);
            using (Pen separator = new Pen(Color.FromArgb(43, 58, 79), 1f))
            {
                graphics.DrawLine(separator, 18, SectionSeparatorY, 312, SectionSeparatorY);
            }
            DrawQuotaSection(graphics, "주간 한도", _weeklyRemainingPercent, _weeklyResetsAt, _weeklyAccent, WeeklySectionTop);
        }
    }

    private void DrawQuotaSection(Graphics graphics, string label, double? remainingPercent, DateTimeOffset? resetsAt, Color accent, int top)
    {
        DrawText(graphics, label, new Font("Segoe UI Semibold", 8.5f), Color.FromArgb(176, 190, 209),
            SectionLabelBounds(top), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        string percent = remainingPercent.HasValue ? remainingPercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "--";
        Size percentageNaturalSize;
        using (Font percentageFont = new Font("Segoe UI Semibold", 19f))
        {
            percentageNaturalSize = TextRenderer.MeasureText(
                graphics,
                percent,
                percentageFont,
                new Size(1000, 1000),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
        DrawText(graphics, percent, new Font("Segoe UI Semibold", 19f), Color.FromArgb(248, 250, 252),
            PercentageBounds(top), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
        DrawText(graphics, "남음", new Font("Segoe UI", 9f), Color.FromArgb(143, 160, 183),
            RemainingLabelBounds(top, PercentageBounds(top).Left + percentageNaturalSize.Width + RemainingAdvanceAdjustment),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

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

    private static Rectangle PercentageBounds(int top)
    {
        return new Rectangle(18, top + 18, 140, 52);
    }

    private static Rectangle RemainingLabelBounds(int top, int left)
    {
        return new Rectangle(left, top + 35, 48, 30);
    }

    private static Rectangle PercentageAndRemainingBounds(int top)
    {
        return new Rectangle(18, top + 20, 192, 50);
    }

    private static Rectangle ProgressTrackBounds(int top)
    {
        return new Rectangle(18, top + 70, 294, 7);
    }

    private static Rectangle ResetTextBounds(int top)
    {
        return new Rectangle(18, top + 83, 294, 24);
    }

    private void ValidateLayout()
    {
        ValidateLayout(_isProPlan);
    }

    private void ValidateLayout(bool isProPlan)
    {
        List<KeyValuePair<string, Rectangle>> components = new List<KeyValuePair<string, Rectangle>>
        {
            new KeyValuePair<string, Rectangle>("상태 점", new Rectangle(18, 10, 7, 7)),
            new KeyValuePair<string, Rectangle>("제목", new Rectangle(33, 2, 220, 24)),
            new KeyValuePair<string, Rectangle>("새로고침", _refreshBounds),
            new KeyValuePair<string, Rectangle>("닫기", _closeBounds)
        };

        if (isProPlan)
        {
            AddQuotaSectionComponents(components, "주간", FiveHourSectionTop);
        }
        else
        {
            AddQuotaSectionComponents(components, "5시간", FiveHourSectionTop);
            components.Add(new KeyValuePair<string, Rectangle>("구분선", new Rectangle(18, SectionSeparatorY, 294, 1)));
            AddQuotaSectionComponents(components, "주간", WeeklySectionTop);
        }

        int expectedHeight = isProPlan ? SingleQuotaHeightPixels : DualQuotaHeightPixels;
        Rectangle clientBounds = new Rectangle(0, 0, WidthPixels, expectedHeight);
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

        if (isProPlan)
        {
            ValidatePercentageTrackGap(problems, "주간", FiveHourSectionTop);
        }
        else
        {
            ValidatePercentageTrackGap(problems, "5시간", FiveHourSectionTop);
            ValidatePercentageTrackGap(problems, "주간", WeeklySectionTop);
        }
        ValidateQuotaTextFits(problems);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("오버레이 좌표 충돌:\r\n" + string.Join("\r\n", problems.ToArray()));
        }
    }

    private static void AddQuotaSectionComponents(
        List<KeyValuePair<string, Rectangle>> components,
        string sectionName,
        int top)
    {
        components.Add(new KeyValuePair<string, Rectangle>(sectionName + " 제목", SectionLabelBounds(top)));
        components.Add(new KeyValuePair<string, Rectangle>(sectionName + " 비율 및 남음", PercentageAndRemainingBounds(top)));
        components.Add(new KeyValuePair<string, Rectangle>(sectionName + " 진행도", ProgressTrackBounds(top)));
        components.Add(new KeyValuePair<string, Rectangle>(sectionName + " 초기화", ResetTextBounds(top)));
    }

    private static void ValidatePercentageTrackGap(List<string> problems, string sectionName, int top)
    {
        int gap = ProgressTrackBounds(top).Top - PercentageBounds(top).Bottom;
        if (gap < MinimumPercentageTrackGap)
        {
            problems.Add(sectionName + " 비율과 진행도 사이 간격이 부족합니다: " + gap + "px");
        }
    }

    private static void ValidateQuotaTextFits(List<string> problems)
    {
        Rectangle percentageBounds = PercentageBounds(FiveHourSectionTop);
        Size percentageNaturalSize;
        using (Font percentageFont = new Font("Segoe UI Semibold", 19f))
        {
            percentageNaturalSize = TextRenderer.MeasureText(
                "99.9%",
                percentageFont,
                new Size(1000, 1000),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (percentageNaturalSize.Width > percentageBounds.Width || percentageNaturalSize.Height > percentageBounds.Height)
            {
                problems.Add("비율 글자가 영역보다 큽니다: " + percentageNaturalSize + " > " + percentageBounds.Size);
            }
        }

        Rectangle remainingBounds = RemainingLabelBounds(
            FiveHourSectionTop,
            percentageBounds.Left + percentageNaturalSize.Width + RemainingAdvanceAdjustment);
        using (Font remainingFont = new Font("Segoe UI", 9f))
        {
            Size remainingNaturalSize = TextRenderer.MeasureText(
                "남음",
                remainingFont,
                new Size(1000, 1000),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (remainingNaturalSize.Width > remainingBounds.Width || remainingNaturalSize.Height > remainingBounds.Height)
            {
                problems.Add("남음 글자가 영역보다 큽니다: " + remainingNaturalSize + " > " + remainingBounds.Size);
            }
        }
        if (remainingBounds.Right > WidthPixels)
        {
            problems.Add("비율 뒤의 남음 글자가 창 밖에 있습니다: " + remainingBounds);
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
        if ((_pendingRateLimitsRequestId >= 0 || _pendingAccountRequestId >= 0) &&
            (DateTime.Now - _lastRequestAt).TotalSeconds > 20)
        {
            bool quotaTimedOut = _pendingRateLimitsRequestId >= 0;
            bool accountTimedOut = _pendingAccountRequestId >= 0;
            _pendingRateLimitsRequestId = -1;
            _pendingAccountRequestId = -1;

            if (accountTimedOut)
            {
                _accountCheckCompleted = true;
                Program.Log("ChatGPT 요금제 조회 시간이 초과되어 기존 레이아웃을 유지합니다.");
            }

            if (quotaTimedOut)
            {
                SetError("한도 조회 시간이 초과되었습니다.");
                if (_liveCheck)
                {
                    ExitCode = 1;
                    Close();
                    return;
                }
            }
            else
            {
                TryCompleteLiveCheck();
            }
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
                RequestAccountAndRateLimits();
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
            _pendingRateLimitsRequestId = -1;
            _pendingAccountRequestId = -1;
            _accountCheckCompleted = false;
            _quotaCheckCompleted = false;
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
                                { "version", "1.4.0" }
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
                RequestAccountAndRateLimits();
                return;
            }

            if (hasId && id == _pendingAccountRequestId)
            {
                _pendingAccountRequestId = -1;
                _accountCheckCompleted = true;
                try
                {
                    ThrowIfError(message, "요금제 조회 실패");
                    ApplyPlanType(ParsePlanType(GetDictionary(message, "result")));
                }
                catch (Exception exception)
                {
                    Program.Log("Account read", exception);
                }
                TryCompleteLiveCheck();
                return;
            }

            if (hasId && id == _pendingRateLimitsRequestId)
            {
                _pendingRateLimitsRequestId = -1;
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
            else if (string.Equals(method, "account/updated", StringComparison.Ordinal))
            {
                Dictionary<string, object> parameters = GetDictionary(message, "params");
                ApplyPlanType(GetString(parameters, "planType"));
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

    private void RequestAccountAndRateLimits()
    {
        if (!_initialized || _codexProcess == null || _codexProcess.HasExited)
        {
            return;
        }

        bool sent = false;

        if (_pendingAccountRequestId < 0)
        {
            _pendingAccountRequestId = _nextRequestId++;
            SendMessage(new Dictionary<string, object>
            {
                { "method", "account/read" },
                { "id", _pendingAccountRequestId },
                { "params", new Dictionary<string, object>() }
            });
            sent = true;
        }

        if (_pendingRateLimitsRequestId < 0)
        {
            _pendingRateLimitsRequestId = _nextRequestId++;
            SendMessage(new Dictionary<string, object>
            {
                { "method", "account/rateLimits/read" },
                { "id", _pendingRateLimitsRequestId }
            });
            sent = true;
        }

        if (!sent)
        {
            return;
        }

        _lastRequestAt = DateTime.Now;
    }

    private static string ParsePlanType(Dictionary<string, object> result)
    {
        Dictionary<string, object> account = GetDictionary(result, "account");
        return GetString(account, "planType");
    }

    private static bool IsProPlan(string planType)
    {
        return string.Equals(planType, "pro", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(planType, "prolite", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyPlanType(string planType)
    {
        string normalized = string.IsNullOrWhiteSpace(planType) ? "unknown" : planType.Trim().ToLowerInvariant();
        bool isProPlan = IsProPlan(normalized);
        bool layoutChanged = _isProPlan != isProPlan;
        bool planChanged = !string.Equals(_detectedPlanType, normalized, StringComparison.Ordinal);

        _detectedPlanType = normalized;
        _isProPlan = isProPlan;

        if (layoutChanged)
        {
            ApplyPlanLayout();
        }

        if (planChanged)
        {
            int height = isProPlan ? SingleQuotaHeightPixels : DualQuotaHeightPixels;
            Program.Log("ChatGPT 요금제 감지: " + normalized + "; 5시간 한도 표시: " + (!isProPlan) + "; 창 높이: " + height + "px");
        }

        Invalidate();
    }

    private void ApplyPlanLayout()
    {
        int desiredHeight = _isProPlan ? SingleQuotaHeightPixels : DualQuotaHeightPixels;
        if (ClientSize.Width != WidthPixels || ClientSize.Height != desiredHeight)
        {
            MinimumSize = Size.Empty;
            MaximumSize = Size.Empty;
            ClientSize = new Size(WidthPixels, desiredHeight);
            MinimumSize = new Size(WidthPixels, desiredHeight);
            MaximumSize = new Size(WidthPixels, desiredHeight);
            UpdateRoundedRegion();
        }

        ValidateLayout(_isProPlan);
    }

    private void TryCompleteLiveCheck()
    {
        if (_liveCheck && _accountCheckCompleted && _quotaCheckCompleted && !_closing)
        {
            ExitCode = 0;
            Close();
        }
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
        _quotaCheckCompleted = true;

        Invalidate();
        TryCompleteLiveCheck();
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
        _pendingRateLimitsRequestId = -1;
        _pendingAccountRequestId = -1;
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
        Location = new Point(work.Right - WidthPixels - 20, work.Bottom - DualQuotaHeightPixels - 20);

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

            Rectangle candidate = new Rectangle(x, y, WidthPixels, DualQuotaHeightPixels);
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
        IntPtr regionHandle = CreateRoundRectRgn(0, 0, ClientSize.Width + 1, ClientSize.Height + 1, 20, 20);
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
