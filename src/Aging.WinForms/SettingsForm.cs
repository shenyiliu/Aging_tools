using Aging.Core;
using Aging.Windows;
using System.Text.Json;

namespace Aging.WinForms;

public sealed class SettingsForm : Form
{
    private readonly AppFiles files;
    private readonly List<AgingItem> queue;
    private readonly AppOptions options;
    private readonly ListView list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly CheckBox stopOnFail, switchBoot, shutdown, resume, nextBoot, maxVolume;
    private readonly TextBox video, reference, pwrtest, burnin, sft, sn, after, engines;
    private readonly ContextMenuStrip addMenu = new();
    public SettingsForm(AppFiles files)
    {
        this.files = files; options = JsonData.Clone(files.Options); queue = [.. files.Queue];
        Ui.Style(this, "参数设置 — 老化测试", new Size(680, 550));
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var tabs = new TabControl { Bounds = new(18, 18, 644, 474) }; Controls.Add(tabs);
        var aging = new TabPage("老化队列") { BackColor = Color.White };
        var paths = new TabPage("工具路径") { BackColor = Color.White };
        var enginePage = new TabPage("引擎适配") { BackColor = Color.White };
        tabs.TabPages.AddRange([aging, paths, enginePage]);
        Ui.Label(aging, "* 通过表格中右键菜单或右侧按钮，可创建、编辑和删除老化项目，所有项目将按顺序依次执行。", new(12, 8, 604, 32));
        list.Bounds = new(12, 44, 574, 212); list.Columns.Add("No.", 40); list.Columns.Add("老化项目", 108); list.Columns.Add("时间 / 次数", 90); list.Columns.Add("倒计时 / 子项", 180); list.Columns.Add("", 144);
        list.Font = new Font("Microsoft YaHei", 10F); list.ShowItemToolTips = true;
        aging.Controls.Add(list); list.DoubleClick += (_, _) => Edit();
        foreach (string name in new[] { "BurnIn", "Reboot", "CS", "S3", "S4", "3DMark", "FurMark", "Video" })
        { string selected = name; addMenu.Items.Add(name == "BurnIn" ? "BurnInTest" : name == "Reboot" ? "REBOOT" : name, null, (_, _) => Add(selected)); }
        addMenu.Opening += (_, _) => addMenu.Items[^1].Enabled = !queue.Any(x => x.Kind == TaskKind.Video);
        var addButton = Ui.Button(aging, "+", new(590, 44, 26, 26), (_, _) => { });
        addButton.Click += (_, _) => addMenu.Show(addButton, new Point(0, addButton.Height));
        Ui.Button(aging, "✎", new(590, 74, 26, 26), (_, _) => Edit());
        Ui.Button(aging, "X", new(590, 104, 26, 26), (_, _) => Delete());
        Ui.Button(aging, "XX", new(590, 134, 26, 26), (_, _) => DeleteAll());
        Ui.Button(aging, "↑", new(590, 200, 26, 26), (_, _) => MoveItem(-1));
        Ui.Button(aging, "↓", new(590, 230, 26, 26), (_, _) => MoveItem(1));
        var menu = new ContextMenuStrip();
        var create = new ToolStripMenuItem("新建");
        foreach (string name in new[] { "BurnIn", "Reboot", "CS", "S3", "S4", "3DMark", "FurMark", "Video" })
        { string selected = name; create.DropDownItems.Add(name == "BurnIn" ? "BurnInTest" : name, null, (_, _) => Add(selected)); }
        menu.Items.Add(create); menu.Items.Add("编辑", null, (_, _) => Edit()); menu.Items.Add("删除", null, (_, _) => Delete()); menu.Items.Add("删除所有", null, (_, _) => DeleteAll()); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("上移", null, (_, _) => MoveItem(-1)); menu.Items.Add("下移", null, (_, _) => MoveItem(1));
        menu.Opening += (_, _) => { create.DropDownItems[^1].Enabled = !queue.Any(x => x.Kind == TaskKind.Video); menu.Items[1].Enabled = Selected >= 0 && queue[Selected].Kind != TaskKind.Video; menu.Items[2].Enabled = Selected >= 0; };
        list.ContextMenuStrip = menu; list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) Delete(); if (e.KeyCode == Keys.Enter) Edit(); };
        stopOnFail = Ui.Check(aging, "老化发现错误时停止", options.StopOnFail, new(12, 278, 220, 28));
        switchBoot = Ui.Check(aging, "按原规则切项重启", options.SwitchReboot, new(264, 278, 250, 28));
        shutdown = Ui.Check(aging, "老化结束后关机", options.ShutdownAfterAging, new(12, 316, 230, 28));
        resume = Ui.Check(aging, "登录后续测", options.ResumeAtLogon, new(264, 316, 230, 28));
        nextBoot = Ui.Check(aging, "下次开机开始（等待手动重启）", options.AgingInNextBoot, new(12, 350, 300, 26));
        maxVolume = Ui.Check(aging, "测试时使用最大音量", options.TestWithMaxVolume, new(332, 350, 284, 26));
        nextBoot.CheckedChanged += (_, _) => { if (nextBoot.Checked) resume.Checked = true; };
        resume.CheckedChanged += (_, _) => { if (!resume.Checked) nextBoot.Checked = false; };
        if (nextBoot.Checked) resume.Checked = true;
        Ui.Label(aging, "下次开机需登录此账户。Video 与结束后关机互斥。", new(12, 378, 604, 24));
        Ui.Button(aging, "导入旧 Config.ini", new(12, 406, 150, 30), (_, _) => Import());
        Ui.Button(aging, "导出配置", new(176, 406, 110, 30), (_, _) => Export());
        Ui.Button(aging, "打开数据目录", new(300, 406, 150, 30), (_, _) => Ui.OpenFolder(files.Root));
        reference = PathField(paths, "GW_TEST 工具目录", options.ReferenceRoot, 12, true);
        video = PathField(paths, "视频文件", options.VideoPath, 54);
        pwrtest = PathField(paths, "PwrTest x64", options.PwrTestPath, 96);
        burnin = PathField(paths, "BurnIn 9.2 bit.exe", options.BurnInPath, 138);
        sft = PathField(paths, "SFT 功能测试", options.SftPath, 180);
        sn = PathField(paths, "SN / KEY 专用工具", options.SnKeyPath, 222);
        after = PathField(paths, "完成后脚本 EXE/PS1", options.AfterAgingScript, 264);
        Ui.Button(paths, "按工具目录匹配路径", new(12, 320, 196, 30), (_, _) =>
        {
            var found = AppFiles.DiscoverTools(reference.Text.Trim());
            if (found.Power.Length > 0) pwrtest.Text = found.Power;
            if (found.Video.Length > 0) video.Text = found.Video;
            if (found.Sft.Length > 0) sft.Text = found.Sft;
            string bit = AppFiles.DiscoverBurnIn(reference.Text.Trim()); if (bit.Length > 0) burnin.Text = bit;
            MessageBox.Show(this, $"已找到：PwrTest {(found.Power.Length > 0 ? "✓" : "缺失")}，视频 {(found.Video.Length > 0 ? "✓" : "缺失")}，SFT {(found.Sft.Length > 0 ? "✓" : "缺失")}，BurnIn {(bit.Length > 0 ? "✓" : "缺失")}。\r\nBurnIn 使用内置 9.2 脚本适配，无需填写 JSON；FurMark/3DMark 仍未完成内置适配。", "匹配工具路径");
        });
        Ui.Label(paths, "已包含的组件位于本工具 Tools 目录，可直接匹配。FurMark/3DMark 原包缺少引擎。\r\n温度按 CPU 支持读取；IP3/WB/Ed 风扇仅适用于对应主板。", new(12, 364, 604, 62));
        Ui.Label(enginePage, "每个引擎按 SHA256、版本、参数和报告规则冻结。未验证的适配不能运行。", new(12, 8, 604, 40));
        engines = new TextBox { Bounds = new(12, 52, 604, 330), Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9), Text = JsonSerializer.Serialize(options.Engines, JsonData.Options) };
        enginePage.Controls.Add(engines);
        Ui.Button(enginePage, "载入模板", new(12, 396, 120, 30), (_, _) =>
        {
            string template = Path.Combine(AppContext.BaseDirectory, "engine-profiles.example.json");
            if (File.Exists(template)) engines.Text = File.ReadAllText(template);
        });
        Ui.Label(enginePage, "模板不能直接通过真实预检，需填入专机验证结果。", new(146, 396, 458, 30));
        var save = Ui.Button(this, "保存", new(456, 506, 98, 30), (_, _) => Save());
        var cancel = Ui.Button(this, "取消", new(564, 506, 98, 30), (_, _) => Close());
        AcceptButton = save; CancelButton = cancel; RefreshQueue();
    }
    private TextBox PathField(Control page, string label, string value, int y, bool directory = false)
    {
        Ui.Label(page, label, new(12, y, 150, 26));
        var box = Ui.TextBox(page, value, new(164, y, 396, 26));
        Ui.Button(page, "…", new(568, y, 48, 27), (_, _) =>
        {
            if (directory) { using var dialog = new FolderBrowserDialog(); if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath; }
            else { using var dialog = new OpenFileDialog { CheckFileExists = true }; if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName; }
        }); return box;
    }
    private int Selected => list.SelectedIndices.Count == 0 ? -1 : list.SelectedIndices[0];
    private void RefreshQueue(int select = -1)
    {
        list.Items.Clear();
        for (int i = 0; i < queue.Count; i++)
        {
            var item = queue[i];
            string first = item.IsPower ? item.Cycles.ToString() : item.IsStress ? item.Hours.ToString("0.##") : "";
            string second = item.IsPower ? item.DelaySeconds.ToString() : item.Kind == TaskKind.BurnIn ? new[] { "综合", "综合(DX9)", "硬盘" }[item.BurnInMode] : item.Kind == TaskKind.Mark3D ? item.MarkVersion : item.Kind == TaskKind.FurMark ? item.Msaa : "";
            string third = item.Kind is TaskKind.S3 or TaskKind.S4 or TaskKind.CS ? item.SleepSeconds.ToString() : item.Kind is TaskKind.FurMark or TaskKind.Mark3D ? item.Resolution : "";
            var row = new ListViewItem([(i + 1).ToString(), item.Name, first, second, third]) { ToolTipText = item.Serialize() };
            list.Items.Add(row); if (i == select) row.Selected = true;
        }
        bool hasVideo = queue.Any(x => x.Kind == TaskKind.Video);
        shutdown.Enabled = !hasVideo; if (hasVideo) shutdown.Checked = false;
    }
    private void Add(string kind)
    {
        try
        {
            if (kind == "Video") { if (queue.Any(x => x.Kind == TaskKind.Video)) return; queue.Add(AgingItem.Parse("Video")); RefreshQueue(queue.Count - 1); return; }
            using var form = new ItemEditor(ItemEditor.CreateDefault(kind, files.ConfigPath), files.ConfigPath);
            if (form.ShowDialog(this) == DialogResult.OK) { int videoIndex = queue.FindIndex(x => x.Kind == TaskKind.Video); int index = videoIndex < 0 ? queue.Count : videoIndex; queue.Insert(index, form.Result!); RefreshQueue(index); }
        }
        catch (Exception e) { Ui.Error(this, e); }
    }
    private void Edit() { int i = Selected; if (i < 0 || queue[i].Kind == TaskKind.Video) return; using var form = new ItemEditor(queue[i], files.ConfigPath); if (form.ShowDialog(this) == DialogResult.OK) { queue[i] = form.Result!; RefreshQueue(i); } }
    private void Delete()
    {
        int i = Selected; if (i < 0) return;
        if (MessageBox.Show(this, $"是否删除 老化项 ‘{queue[i].Name}’?", "请确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        queue.RemoveAt(i); RefreshQueue(Math.Min(i, queue.Count - 1));
    }
    private void DeleteAll()
    {
        if (queue.Count == 0 || MessageBox.Show(this, "是否删除所有老化项?", "请确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        queue.Clear(); RefreshQueue();
    }
    private void MoveItem(int delta)
    {
        int i = Selected; if (i < 0 || i + delta < 0 || i + delta >= queue.Count || queue[i].Kind == TaskKind.Video || queue[i + delta].Kind == TaskKind.Video) return;
        (queue[i], queue[i + delta]) = (queue[i + delta], queue[i]); RefreshQueue(i + delta);
    }
    private void Import()
    {
        using var dialog = new OpenFileDialog { Filter = "INI 配置|*.ini" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { files.Import(dialog.FileName); DialogResult = DialogResult.Retry; Close(); }
        catch (Exception e) { Ui.Error(this, e); }
    }
    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "INI 配置|*.ini", FileName = "Config.ini" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var doc = IniDocument.Load(files.ConfigPath); LegacyConfig.WriteQueue(doc, queue);
            // Export is explicit; reference files are never targeted automatically.
            if (Path.GetFullPath(dialog.FileName).Equals(Path.Combine(options.ReferenceRoot, "Other", "Config.ini"), StringComparison.OrdinalIgnoreCase)) throw new IOException("请导出到新的文件，保留原版配置");
            doc.Save(dialog.FileName);
        }
        catch (Exception e) { Ui.Error(this, e); }
    }
    private void Save()
    {
        try
        {
            options.StopOnFail = stopOnFail.Checked; options.SwitchReboot = switchBoot.Checked; options.ShutdownAfterAging = shutdown.Checked;
            options.AgingInNextBoot = nextBoot.Checked;
            options.TestWithMaxVolume = maxVolume.Checked;
            options.ReferenceRoot = reference.Text.Trim(); options.VideoPath = video.Text.Trim(); options.PwrTestPath = pwrtest.Text.Trim();
            options.SftPath = sft.Text.Trim(); options.SnKeyPath = sn.Text.Trim(); options.AfterAgingScript = after.Text.Trim();
            options.BurnInPath = burnin.Text.Trim();
            options.Engines = JsonSerializer.Deserialize<Dictionary<string, EngineProfile>>(engines.Text, JsonData.Options) ?? throw new FormatException("引擎适配配置不能为空");
            if (queue.Count == 0 || options.ShutdownAfterAging && queue.Any(x => x.Kind == TaskKind.Video)) throw new FormatException("队列不能为空，且视频与完成后关机互斥");
            foreach (var item in queue) item.Validate();
            if (resume.Checked)
            {
                if (!WindowsPlatform.IsAdministrator) throw new InvalidOperationException("请以管理员身份启动工具，再注册最高权限登录续测任务");
                LogonResume.Register(Program.MainExecutable, files.Root);
                if (!LogonResume.IsRegisteredFor(Program.MainExecutable, files.Root)) throw new IOException("计划任务注册校验失败");
            }
            else if (options.ResumeAtLogon) LogonResume.Remove();
            options.ResumeAtLogon = resume.Checked;
            files.Save(queue, options); DialogResult = DialogResult.OK; Close();
        }
        catch (Exception e) { Ui.Error(this, e); }
    }
}
