using Aging.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aging.WinForms;

/// <summary>Original full-screen video layout and test-attempt counting, hosted by the supported WMP OCX.</summary>
public sealed class VideoForm : Form, IMessageFilter
{
    private sealed class MediaHost() : AxHost("6BF52A52-394A-11d3-B153-00C04F79FAA6")
    { public object Player => GetOcx() ?? throw new InvalidOperationException("Windows Media Player ActiveX 未就绪"); }
    private readonly MediaHost host = new();
    private readonly string path, iconDirectory;
    private readonly Panel telemetry = new() { BackColor = Color.Black };
    private readonly Label elapsed, battery, playbackError;
    private readonly PictureBox charge = new() { SizeMode = PictureBoxSizeMode.StretchImage };
    private readonly Button pass, fail;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly Stopwatch clock = new();
    private readonly Dictionary<string, Image> batteryImages = new(StringComparer.OrdinalIgnoreCase);
    private TaskResult result = new(Outcome.Aborted, "视频窗口被关闭");
    private bool mediaPlayed, mediaFailed, messageFilterInstalled;
    private int plays, oldBattery, moveStep = 20;
    private long lastMinute = -1;
    private DateTimeOffset hideButtonsAt;

    public VideoForm(string path, string? iconDirectory = null)
    {
        this.path = path;
        this.iconDirectory = iconDirectory ?? Path.Combine(AppContext.BaseDirectory, "Assets");
        Text = "Video Play"; Font = Ui.DefaultFont; AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black; TopMost = true; KeyPreview = true;
        Controls.Add(host);
        elapsed = Ui.Label(telemetry, "00:00:00", new(0, 0, 150, 36)); elapsed.Font = new Font(Font.FontFamily, 20); elapsed.ForeColor = Color.White;
        charge.Bounds = new(150, 0, 58, 36); telemetry.Controls.Add(charge);
        battery = Ui.Label(telemetry, "", new(208, 0, 90, 36)); battery.Font = new Font(Font.FontFamily, 22); battery.ForeColor = Color.White;
        Controls.Add(telemetry);
        pass = Ui.Button(this, "通过 (&P)", Rectangle.Empty, (_, _) => Finish(true));
        pass.BackColor = Color.FromArgb(154, 205, 50); pass.ForeColor = Color.White; pass.Font = new Font(Font.FontFamily, 32); pass.Enabled = false;
        fail = Ui.Button(this, "失败 (&F)", Rectangle.Empty, (_, _) => Finish(false));
        fail.BackColor = Color.FromArgb(220, 20, 60); fail.ForeColor = Color.White; fail.Font = pass.Font;
        playbackError = Ui.Label(this, "", Rectangle.Empty); playbackError.ForeColor = Color.White; playbackError.BackColor = Color.DarkRed;
        pass.Hide(); fail.Hide(); playbackError.Hide();
        Resize += (_, _) => Arrange();
        Shown += (_, _) => { Application.AddMessageFilter(this); messageFilterInstalled = true; Arrange(); StartPlayback(); };
        timer.Tick += (_, _) => UpdatePlayback();
        FormClosed += (_, _) =>
        {
            timer.Stop(); RemoveInputFilter();
            // Original PlayNum counts entries into _VIDEOPLAY, not repetitions inside WMP's loop mode.
            result = result with { VideoPlays = plays, VideoRunSeconds = plays > 0 ? [Math.Max(0, clock.Elapsed.TotalSeconds)] : [] };
            try { dynamic player = host.Player; player.controls.stop(); } catch (Exception e) when (e is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        };
    }
    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96F);
    private void Arrange()
    {
        int height = (int)Math.Round(ClientSize.Width * 0.5625);
        host.Bounds = new(0, (ClientSize.Height - height) / 2, ClientSize.Width, height);
        int telemetryX = Math.Clamp(telemetry.Left == 0 ? Scale(10) : telemetry.Left, 0, Math.Max(0, ClientSize.Width - Scale(308)));
        telemetry.Bounds = new(telemetryX, Scale(6), Scale(298), Scale(36));
        elapsed.Bounds = new(0, 0, Scale(150), Scale(36)); charge.Bounds = new(Scale(150), 0, Scale(58), Scale(36)); battery.Bounds = new(Scale(208), 0, Scale(90), Scale(36));
        pass.Bounds = new(Scale(10), ClientSize.Height - Scale(140), Scale(220), Scale(90));
        fail.Bounds = new(Scale(250), ClientSize.Height - Scale(140), Scale(220), Scale(90));
        playbackError.Bounds = new(Scale(10), ClientSize.Height - Scale(42), Math.Max(Scale(100), ClientSize.Width - Scale(20)), Scale(32));
        telemetry.BringToFront(); pass.BringToFront(); fail.BringToFront(); playbackError.BringToFront();
    }
    private void StartPlayback()
    {
        try
        {
            // One test attempt starts here. Decoder failure remains a failed attempt in its log.
            plays = 1; clock.Start();
            dynamic player = host.Player; player.uiMode = "none"; player.stretchToFit = true; player.enableContextMenu = false;
            player.settings.autoStart = true; player.settings.setMode("loop", true); player.URL = path;
            player.controls.play(); timer.Start(); UpdateBattery();
        }
        catch (Exception e) { result = new(Outcome.Fail, "视频组件无法启动：" + e.Message); Close(); }
    }
    private void UpdatePlayback()
    {
        try
        {
            dynamic player = host.Player;
            if ((int)player.playState == 3) { mediaPlayed = true; pass.Enabled = !mediaFailed; }
            if ((int)player.error.errorCount > 0)
            {
                mediaFailed = true; pass.Enabled = false; result = new(Outcome.Fail, "视频解码或播放错误", plays);
                playbackError.Text = "视频播放失败，请检查视频文件和解码器；按 F 结束"; playbackError.Show(); RevealButtons();
            }
            elapsed.Text = FormatElapsed(clock.Elapsed);
            UpdateBattery();
            if (pass.Visible && DateTimeOffset.Now >= hideButtonsAt) { pass.Hide(); fail.Hide(); }
            if (!mediaPlayed && clock.Elapsed > TimeSpan.FromSeconds(30))
            { result = new(Outcome.Fail, "30 秒内未开始播放，可能缺少解码器"); Close(); }
        }
        catch (Exception e) { result = new(Outcome.Fail, "视频状态读取失败：" + e.Message, plays); Close(); }
    }
    private static string FormatElapsed(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    private void UpdateBattery()
    {
        PowerStatus info = SystemInformation.PowerStatus;
        bool present = info.BatteryChargeStatus != BatteryChargeStatus.Unknown && !info.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery);
        bool online = info.PowerLineStatus == PowerLineStatus.Online;
        bool charging = present && info.BatteryChargeStatus.HasFlag(BatteryChargeStatus.Charging);
        int percent = info.BatteryLifePercent is >= 0 and <= 1 ? (int)Math.Round(info.BatteryLifePercent * 100) : 0;
        battery.Visible = present; battery.Text = $" {percent}%";
        string level = percent > 95 ? "A" : Math.Clamp((int)Math.Round((percent - 1) / 10D), 0, 9).ToString();
        string icon = $"{(online ? 1 : 0)}{(charging ? 1 : 0)}{(present ? 1 : 0)}{level}.bmp";
        string file = Path.Combine(iconDirectory, "Battery", icon);
        if (File.Exists(file))
        {
            if (!batteryImages.TryGetValue(file, out var loaded)) { using var source = Image.FromFile(file); loaded = new Bitmap(source); batteryImages[file] = loaded; }
            charge.Image = loaded;
        }
        else charge.Image = null;
        charge.AccessibleName = online ? (charging ? "充电中" : "交流电源") : "电池供电";
        var now = DateTimeOffset.Now;
        long minute = now.ToUnixTimeSeconds() / 60;
        if (now.Second == 1 && minute != lastMinute)
        {
            lastMinute = minute;
            BackColor = present && oldBattery > percent && percent < 90 ? Color.Red : Color.Black;
            telemetry.BackColor = BackColor; oldBattery = percent;
            int next = telemetry.Left + Scale(moveStep);
            if (next < Scale(10) || next + telemetry.Width > ClientSize.Width - Scale(10)) { moveStep = -moveStep; next = telemetry.Left + Scale(moveStep); }
            telemetry.Left = Math.Clamp(next, 0, Math.Max(0, ClientSize.Width - telemetry.Width));
        }
    }
    private void RevealButtons()
    {
        hideButtonsAt = DateTimeOffset.Now.AddSeconds(5);
        pass.Show(); fail.Show(); pass.BringToFront(); fail.BringToFront();
    }
    public bool PreFilterMessage(ref Message m)
    {
        if (!Visible || IsDisposed || !IsHandleCreated || m.HWnd != Handle && !IsChild(Handle, m.HWnd)) return false;
        if (m.Msg == 0x0202) RevealButtons(); // Left-button release, including the WMP child window.
        if (m.Msg == 0x0100)
        {
            switch ((Keys)m.WParam.ToInt32())
            {
                case Keys.P: if (pass.Enabled) Finish(true); return true;
                case Keys.F: Finish(false); return true;
                case Keys.Escape: Close(); return true;
            }
        }
        return false;
    }
    private void Finish(bool passed)
    {
        if (passed && (!mediaPlayed || mediaFailed)) return;
        result = new(passed ? Outcome.Pass : Outcome.Fail, passed ? "视频人工判定通过" : "视频人工判定失败", plays); Close();
    }
    public TaskResult Play(IWin32Window owner, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return new(Outcome.Aborted, "视频播放已取消");
        Bounds = Screen.FromHandle(owner.Handle).Bounds;
        Shown += (_, _) => { if (ct.IsCancellationRequested) Close(); };
        using var registration = ct.Register(() => { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Close)); });
        ShowDialog(owner); return ct.IsCancellationRequested ? result with { Outcome = Outcome.Aborted, Message = "用户终止视频播放" } : result;
    }
    private void RemoveInputFilter() { if (messageFilterInstalled) { Application.RemoveMessageFilter(this); messageFilterInstalled = false; } }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { RemoveInputFilter(); timer.Dispose(); foreach (var image in batteryImages.Values) image.Dispose(); }
        base.Dispose(disposing);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(IntPtr parent, IntPtr window);
}
