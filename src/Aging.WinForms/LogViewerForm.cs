using Aging.Core;
using System.Globalization;
using System.Text;

namespace Aging.WinForms;

// ViewLOG.au3:14461–14698: status band, function/aging columns, log selector, bottom chart.
public sealed class LogViewerForm : Form
{
    private readonly Label status = new() { Dock = DockStyle.Top, Height = 36, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Microsoft YaHei", 22F), ForeColor = Color.White, BackColor = Color.DimGray, Text = "未选择日志" };
    private readonly TextBox function = ReportBox();
    private readonly TextBox details = ReportBox();
    private readonly ComboBox choice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DropDownWidth = 700 };
    private readonly SplitContainer columns = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.None, SplitterWidth = 2, BorderStyle = BorderStyle.FixedSingle };
    private readonly TelemetryChart chart = new() { Dock = DockStyle.Bottom, Height = 200, BackColor = Color.Black, Visible = false };
    private readonly string root;
    private bool loading;
    public LogViewerForm(string root)
    {
        this.root = root;
        var screen = Screen.PrimaryScreen!.WorkingArea;
        Ui.Style(this, "查看日志", new Size(Math.Max(900, screen.Width - 100), Math.Max(620, screen.Height - 64)));
        chart.Height = screen.Height >= 1020 ? 232 : 182;
        columns.Panel1.Controls.Add(function); columns.Panel2.Controls.Add(details);
        var selector = new TableLayoutPanel { Dock = DockStyle.Top, Height = 30, ColumnCount = 3, RowCount = 1, Padding = new Padding(4, 2, 4, 2) };
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116)); selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); selector.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        selector.Controls.Add(new Label { Text = "选择老化日志", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, 0, 0); selector.Controls.Add(choice, 1, 0);
        var menuButton = new Button { Text = "打开…", Dock = DockStyle.Fill, Margin = Padding.Empty };
        var menu = new ContextMenuStrip(); menu.Items.Add("打开日志文件…", null, (_, _) => Open()); menu.Items.Add("刷新日志列表", null, (_, _) => LoadTree()); menu.Items.Add("打开日志目录", null, (_, _) => Ui.OpenFolder(root));
        menuButton.Click += (_, _) => menu.Show(menuButton, new Point(0, menuButton.Height)); selector.Controls.Add(menuButton, 2, 0);
        columns.Panel2.Controls.Add(selector);
        Controls.Add(columns); Controls.Add(chart); Controls.Add(status);
        Shown += (_, _) => { columns.SplitterDistance = (int)(columns.ClientSize.Width * .4); LoadTree(); };
        choice.SelectedIndexChanged += (_, _) => { if (!loading && choice.SelectedItem is LogChoice file) LoadFile(file.Path); };
    }
    private static TextBox ReportBox() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 8.5F), BackColor = Color.White, BorderStyle = BorderStyle.None };
    private void Open()
    { using var dialog = new OpenFileDialog { Filter = "日志文件|*.log;*.txt|全部文件|*.*", InitialDirectory = Directory.Exists(root) ? root : "" }; if (dialog.ShowDialog(this) == DialogResult.OK) LoadFile(dialog.FileName); }
    public void LoadTree()
    {
        loading = true; choice.Items.Clear();
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (string file in Directory.EnumerateFiles(root, "GWTEST_Aging_*.log", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(500))
                choice.Items.Add(new LogChoice(file, Path.GetRelativePath(root, file)));
            if (choice.Items.Count > 0) choice.SelectedIndex = 0;
            else { SetStatus("没有老化日志", Color.DimGray); function.Text = "功能测试\r\n\r\n没有选择本轮日志。"; details.Text = "可以从“打开…”选择原版或新工具的日志文件。"; }
        }
        catch (Exception e) { details.Text = e.Message; }
        finally { loading = false; }
        if (choice.SelectedItem is LogChoice selected) LoadFile(selected.Path);
    }
    public void LoadFile(string path)
    {
        try
        {
            Text = "查看日志 — " + path;
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("日志超过 32 MiB，请用文本编辑器打开。");
            string directory = Path.GetDirectoryName(path)!;
            if (Path.GetFileName(path).Equals("GW_CoreTemp.log", StringComparison.OrdinalIgnoreCase))
            { LoadTelemetry(path); SetStatus("温度 / CPU 历史记录", Color.DimGray); details.Text = "采样曲线见下方。缺少温度数据时留空，不以 0℃ 代替。"; return; }
            chart.Visible = false;
            if (!Path.GetFileName(path).StartsWith("GWTEST_Aging_", StringComparison.OrdinalIgnoreCase))
            { details.Text = ReadText(path); SetStatus("原始日志", Color.DimGray); LoadFunction(directory); return; }
            var doc = IniDocument.Load(path); var text = new StringBuilder();
            bool simulation = doc.Get("MAIN", "Simulation") == "Y";
            var items = doc.GetSection("AgingItem");
            int errors = LoadFunction(directory); bool uncertain = items.Count == 0, completed = doc.Get("MAIN", "AgingState") == "END";
            if (simulation) text.AppendLine("【流程演练】未测硬件，不能作为机器通过的凭证。").AppendLine();
            foreach (var entry in items)
            {
                string name = entry.Value.Split('|')[0], prefix = entry.Key + "_" + name, info = prefix + "_Info";
                string state = doc.Get(info, "State"), outcome = doc.Get(info, "Outcome");
                string first = doc.Get(info, "FirstTime"), last = doc.Get(info, "EndTime");
                text.AppendLine("=====================================================================================");
                text.AppendLine($"({entry.Key})\t[ {name} ]\t(Ver: {doc.Get("ToolVer", name)})");
                text.AppendLine("-------------------------------------------------------------------------------------");
                if (state == "WAIT" || first.Length == 0) text.AppendLine("等待测试");
                else
                {
                    text.AppendLine($"开始时间: {first}\t结束时间: {last}");
                    text.AppendLine($"测试用时: {Elapsed(first, last, state)}");
                    if (name is "Reboot" or "S3" or "S4" or "CS")
                    {
                        string cycles = doc.Get(info, "Cycles", "0");
                        text.Append($"测试次数: {cycles} 次 (目标: {doc.Get(info, "TargetTime", "0")} 次, 倒计时: {doc.Get(info, "Delay")} 秒)");
                        if (int.TryParse(cycles, out int completedCycles) && completedCycles > 0) text.Append("\t平均用时: " + doc.Get(info, "AverageTime", "0") + " 秒");
                        text.AppendLine();
                    }
                    else if (name == "Video")
                    {
                        text.AppendLine($"播放次数: {doc.Get(info, "PlayNum", "0")}");
                        foreach (var run in doc.GetSection(info).Where(x => x.Key.StartsWith("RunTime", StringComparison.OrdinalIgnoreCase))) text.AppendLine(run.Value);
                    }
                    else text.AppendLine($"目标: {doc.Get(info, "TargetTime", "0")} 分钟");
                }
                if (outcome.Length > 0) text.AppendLine("测试结果: " + outcome);
                var reports = doc.GetSection(prefix + "_Report").Where(x => !x.Key.Equals("Num", StringComparison.OrdinalIgnoreCase));
                foreach (var report in reports) text.AppendLine(report.Value.TrimStart('-'));
                text.AppendLine();
                if (int.TryParse(doc.Get(prefix + "_Error", "Num"), out int count) && count >= 0) errors += count;
                else uncertain = true;
                text.AppendLine("错误数量 [" + doc.Get(prefix + "_Error", "Num", "缺失，结果未确认") + "]");
                foreach (var error in doc.GetSection(prefix + "_Error").Where(x => !x.Key.Equals("Num", StringComparison.OrdinalIgnoreCase))) text.AppendLine($"[{error.Key}]  {error.Value}");
                if (state != "END") completed = false;
                if (outcome is "Fail" or "Uncertain" or "Aborted") uncertain = true;
                text.AppendLine().AppendLine();
            }
            int.TryParse(doc.Get("MAIN", "ExcludedCount", "0"), out int excludedCount);
            if (excludedCount > 0)
            {
                text.AppendLine("本轮未执行（不判通过）：");
                for (int i = 1; i <= excludedCount; i++) text.AppendLine(doc.Get("MAIN", "Excluded" + i));
            }
            details.Text = text.ToString();
            string total = doc.Get("MAIN", "Outcome");
            if (simulation) SetStatus("流程演练 · 不代表硬件结果", Color.DimGray);
            else if (errors > 0 || total is "Fail" or "Aborted") SetStatus("FAIL", Ui.Fail);
            else if (uncertain || total == "Uncertain") SetStatus("结果未确认", Color.DarkOrange);
            else if (!completed) SetStatus("测试未完成", Color.DarkOrange);
            else SetStatus(excludedCount > 0 ? "所选项目 PASS · 有项目未执行" : "PASS", Ui.Pass);
            string telemetry = Path.Combine(directory, "GW_CoreTemp.log"); if (File.Exists(telemetry)) LoadTelemetry(telemetry);
        }
        catch (Exception e) { chart.Visible = false; SetStatus("日志读取失败", Ui.Fail); details.Text = "无法读取日志：" + e.Message; }
    }
    private int LoadFunction(string directory)
    {
        string path = Path.Combine(directory, "GWTEST_Log.txt");
        var text = new StringBuilder("***********************************************************\r\n 功能测试\r\n***********************************************************\r\n\r\n");
        int errors = 0;
        if (File.Exists(path))
        {
            foreach (var pair in IniDocument.Load(path).GetSection("FunctionTest"))
            { text.AppendLine(pair.Key.Replace('~', '=') + " " + pair.Value); if (pair.Value.Equals("FAIL", StringComparison.OrdinalIgnoreCase)) errors++; }
        }
        else text.AppendLine("本轮目录没有功能测试记录。");
        function.Text = text.ToString(); return errors;
    }
    private static string Elapsed(string first, string last, string state)
    {
        if (state == "RUN") return "测试中";
        if (!DateTime.TryParse(first, out var begin) || !DateTime.TryParse(last, out var end) || end < begin) return "未获取";
        var duration = end - begin; return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }
    private void SetStatus(string text, Color color) { status.Text = text; status.BackColor = color; }
    private static string ReadText(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); byte[] bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return File.ReadAllText(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(936).GetString(bytes); }
    }
    private void LoadTelemetry(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) { chart.Visible = true; chart.SetData([], "温度日志超过 32 MiB，请使用文本编辑器或分段工具查看。"); return; }
        var samples = new List<TelemetrySample>(); int bad = 0, lines = 0;
        var itemNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in ReadText(path).Split('\n').Skip(1))
        {
            if (++lines > 100000) break;
            string[] fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 7 || !DateTimeOffset.TryParse(fields[6], out var when)) { if (line.Trim().Length > 0) bad++; continue; }
            if (!int.TryParse(fields[0], out int item)) { if (!itemNames.TryGetValue(fields[0], out item)) itemNames[fields[0]] = item = itemNames.Count + 1; }
            double? Num(string value) => double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
            samples.Add(new(when, item, Num(fields[1]), Num(fields[2]), int.TryParse(fields[3], out int bat) ? bat : null, fields[4], fields[5]));
        }
        chart.Visible = true; chart.SetData(samples, $"样本 {samples.Count} 条；跳过异常行 {bad} 条。红线 CPU 温度，绿线 CPU 占用率。缺失值留空。{(lines > 100000 ? "仅显示前 100000 行。" : "")}");
    }
    private sealed record LogChoice(string Path, string Label) { public override string ToString() => Label; }
}
internal sealed class TelemetryChart : Control
{
    private List<TelemetrySample> samples = [];
    private string note = "暂无采样";
    public TelemetryChart() { DoubleBuffered = true; }
    public void SetData(List<TelemetrySample> data, string text) { samples = data; note = text; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.Clear(Color.White); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(50, 34, Math.Max(10, Width - 70), Math.Max(10, Height - 82));
        using var brush = new SolidBrush(Color.DimGray); g.DrawString(note, Ui.DefaultFont, brush, new RectangleF(18, 6, Width - 28, 26));
        g.FillRectangle(Brushes.Black, rect);
        double max = Math.Max(100, samples.Count == 0 ? 100 : samples.Max(x => x.Temperature ?? 0));
        using var grid = new Pen(Color.FromArgb(0, 128, 64));
        for (int i = 0; i <= 5; i++) { int y = rect.Bottom - rect.Height * i / 5; g.DrawLine(grid, rect.Left, y, rect.Right, y); g.DrawString((max * i / 5).ToString("0"), Ui.DefaultFont, brush, 24, y - 8); }
        if (samples.Count == 0) { g.DrawString("没有有效样本", Ui.DefaultFont, brush, rect.Left + 12, rect.Top + 12); return; }
        double duration = Math.Max(1, (samples[^1].Time - samples[0].Time).TotalSeconds);
        void Draw(Func<TelemetrySample, double?> field, Color color)
        {
            using var pen = new Pen(color, 1.5F); PointF? previous = null;
            // Preserve spikes and missing-value gaps; the reader caps input at 100000 samples.
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i]; double? val = field(s);
                if (val == null) { previous = null; continue; }
                var p = new PointF(rect.Left + (float)((s.Time - samples[0].Time).TotalSeconds / duration) * rect.Width, rect.Bottom - (float)(Math.Clamp(val.Value, 0, max) / max) * rect.Height);
                if (previous is { } start) g.DrawLine(pen, start, p); else g.DrawEllipse(pen, p.X - 1, p.Y - 1, 2, 2); previous = p;
            }
        }
        Draw(x => x.Temperature, Color.Red); Draw(x => x.CpuPercent, Color.Lime);
        Color[] bands = [Color.FromArgb(29, 34, 136), Color.FromArgb(0, 160, 233), Color.FromArgb(125, 0, 34), Color.FromArgb(243, 153, 0), Color.FromArgb(96, 17, 102)];
        int startIndex = 0;
        for (int i = 1; i <= samples.Count; i++)
        {
            if (i < samples.Count && samples[i].Item == samples[startIndex].Item) continue;
            int left = rect.Left + (int)((samples[startIndex].Time - samples[0].Time).TotalSeconds / duration * rect.Width);
            int right = i == samples.Count ? rect.Right : rect.Left + (int)((samples[i].Time - samples[0].Time).TotalSeconds / duration * rect.Width);
            using var band = new SolidBrush(bands[Math.Abs(samples[startIndex].Item) % bands.Length]);
            g.FillRectangle(band, left, rect.Bottom + 1, Math.Max(1, right - left), 16);
            if (right - left > 30) g.DrawString("项目 " + samples[startIndex].Item, Ui.DefaultFont, Brushes.White, left + 2, rect.Bottom + 1);
            startIndex = i;
        }
        g.DrawString(samples[0].Time.ToString("MM-dd HH:mm:ss"), Ui.DefaultFont, brush, rect.Left, rect.Bottom + 22);
        g.DrawString(samples[^1].Time.ToString("MM-dd HH:mm:ss"), Ui.DefaultFont, brush, rect.Right - 140, rect.Bottom + 22);
    }
}
