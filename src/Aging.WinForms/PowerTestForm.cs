using Aging.Core;

namespace Aging.WinForms;

/// <summary>Layout recovered from Reboot.au3:18808 and Sleep.au3:20401; state comes from the coordinator.</summary>
public sealed class PowerTestForm : Form
{
    private readonly MainForm owner;
    private readonly int itemIndex;
    private readonly Label countdown, cycles, heading, mode;
    private readonly Label pause, stop;
    private readonly ListView errors, events;
    private readonly HashSet<string> seen = [];
    private string lastMessage = "";
    public int ItemIndex => itemIndex;

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.Style |= 0x40000; return p; }
    }

    public PowerTestForm(MainForm owner, int itemIndex)
    {
        this.owner = owner; this.itemIndex = itemIndex;
        var item = Entry?.Item ?? owner.DisplayQueue[itemIndex];
        bool sleep = item.Kind != TaskKind.Reboot;
        Ui.Style(this, item.Name + " 测试", new Size(sleep ? 720 : 700, sleep ? 370 : 360));
        FormBorderStyle = FormBorderStyle.None; MinimumSize = Size.Empty;
        ClientSize = new(sleep ? 720 : 700, sleep ? 370 : 360);
        TopMost = true; ShowInTaskbar = false; Font = new Font("Microsoft YaHei", 8.5F);
        heading = Ui.Label(this, "> " + item.Name.ToUpperInvariant() + " 测试 >>", new(-2, 8, 272, 35));
        heading.ForeColor = Color.Red; heading.Font = new Font(Font.FontFamily, 21); Ui.Drag(this, heading);
        mode = Ui.Label(this, "", new(0, 70, 270, 36)); mode.Font = new Font(Font.FontFamily, 20); mode.TextAlign = ContentAlignment.MiddleCenter;
        if (sleep) mode.Text = item.Name + " / " + item.SleepSeconds + " 秒";
        int top = sleep ? 120 : 76;
        var caption = Ui.Label(this, "倒计时", new(0, top, 80, 55)); caption.TextAlign = ContentAlignment.MiddleRight;
        countdown = Counter(new(90, top, 90, 55), Color.FromArgb(220, 81, 80));
        pause = Command("暂停", new(195, top, 72, 25), () =>
        {
            try { if (owner.Coordinator.PauseRequested) owner.Coordinator.Resume(); else owner.Coordinator.Pause(); UpdateState(); }
            catch (Exception e) { Ui.Error(this, e); }
        });
        stop = Command("结束", new(195, top + 30, 72, 25), owner.StopRun);
        int cycleTop = sleep ? 190 : 147;
        caption = Ui.Label(this, "已运行周期", new(0, cycleTop, 80, 55)); caption.TextAlign = ContentAlignment.MiddleRight;
        cycles = Counter(new(90, cycleTop, 90, 55), Color.FromArgb(255, 251, 240));
        int errorTop = sleep ? 260 : 218;
        caption = Ui.Label(this, "错误次数", new(0, errorTop, 80, 63)); caption.TextAlign = ContentAlignment.MiddleRight;
        errors = new ListView { Bounds = new(90, errorTop, 170, sleep ? 63 : 105), View = View.Details, HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true, Scrollable = false, BackColor = Color.FromArgb(238, 238, 238) };
        errors.Columns.Add("", 110); errors.Columns.Add("", 43, HorizontalAlignment.Right); Controls.Add(errors);
        events = new ListView { Bounds = new(280, 10, ClientSize.Width - 280, ClientSize.Height - 20), View = View.Details, HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true, Font = new Font(Font.FontFamily, 8), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        events.Columns.Add("", events.Width - 18); Controls.Add(events);
        var footer = Ui.Label(this, "GW TEST", new(15, ClientSize.Height - 31, 101, 21)); footer.ForeColor = Ui.Teal; Ui.Drag(this, footer);
        UpdateState();
    }
    private ItemRun? Entry => owner.Coordinator.Current?.Items.ElementAtOrDefault(itemIndex);
    private Label Counter(Rectangle bounds, Color background)
    {
        var label = Ui.Label(this, "0", bounds); label.TextAlign = ContentAlignment.MiddleCenter;
        label.BorderStyle = BorderStyle.FixedSingle; label.BackColor = background; label.Font = new Font(Font.FontFamily, 34, FontStyle.Bold); return label;
    }
    private Label Command(string text, Rectangle bounds, Action action)
    {
        var label = Ui.Label(this, text, bounds); label.BorderStyle = BorderStyle.FixedSingle; label.TextAlign = ContentAlignment.MiddleCenter;
        label.Cursor = Cursors.Hand; label.Click += (_, _) => action(); return label;
    }
    public void UpdateState()
    {
        if (IsDisposed || Entry is not { } entry) return;
        var run = owner.Coordinator.Current!;
        countdown.Text = entry.RemainingSeconds.ToString(); cycles.Text = entry.CompletedCycles.ToString();
        pause.Enabled = run.CurrentIndex == itemIndex && owner.Coordinator.CanPause;
        pause.Text = owner.Coordinator.PauseRequested ? "继续" : "暂停";
        stop.Enabled = !run.IsTerminal && run.CurrentIndex == itemIndex;
        heading.Text = "> " + entry.Item.Name.ToUpperInvariant() + " 测试 >>";
        heading.ForeColor = Ui.StateColor(entry.Phase);
        if (run.Simulation) heading.Text = "> " + entry.Item.Name + " 演练 >>";
        errors.BeginUpdate(); errors.Items.Clear();
        string[] categories = entry.Item.Kind == TaskKind.Reboot ? ["REBOOT", "蓝屏", "意外关机", "WIFI", "FAN"] : [entry.Item.Name, "蓝屏", "WIFI"];
        foreach (string category in categories)
        {
            bool disabled = category == "WIFI" && !entry.Item.WifiCheck || category == "FAN" && entry.Item.FanProfile == "N";
            int count = category == categories[0] ? entry.Errors.Count : entry.Errors.Count(e => e.Contains(category, StringComparison.OrdinalIgnoreCase) || category == "FAN" && e.Contains("风扇"));
            var row = new ListViewItem([category, disabled ? "N/A" : count.ToString()]);
            row.ForeColor = disabled ? Color.Gray : count > 0 ? Ui.Fail : Ui.Pass; errors.Items.Add(row);
        }
        errors.EndUpdate();
        if (entry.Message != lastMessage)
        {
            lastMessage = entry.Message;
            events.Items.Add(new ListViewItem(DateTime.Now.ToString("HH:mm:ss") + " " + entry.Message) { ForeColor = Color.White, BackColor = Ui.StateColor(entry.Phase) });
        }
        foreach (string error in entry.Errors)
            if (seen.Add("E:" + error)) events.Items.Add(new ListViewItem(error) { ForeColor = Color.White, BackColor = Ui.Fail });
        foreach (string report in entry.Reports)
            if (seen.Add("R:" + report)) events.Items.Add(new ListViewItem(report) { ForeColor = Ui.Pass });
        if (events.Items.Count > 0) events.EnsureVisible(events.Items.Count - 1);
    }
}
