using Aging.Core;
using System.Globalization;

namespace Aging.WinForms;

// Layouts and labels follow recovered/ControlPanel.au3:16799–16908.
public sealed class ItemEditor : Form
{
    private readonly AgingItem original;
    private readonly NumericUpDown cycles = Number(1, 100000, 50);
    private readonly NumericUpDown delay = Number(30, 86400, 30);
    private readonly NumericUpDown sleep = Number(30, 86400, 60);
    private readonly NumericUpDown hours = Number(0.1M, 10000, 1, 2);
    private CheckBox? hardware, time, wifi, window;
    private ComboBox? fan, mode, option, resolution;
    public AgingItem? Result { get; private set; }

    public ItemEditor(AgingItem? original = null, string? configPath = null)
    {
        this.original = original ?? CreateDefault("Reboot", configPath);
        string name = this.original.Name;
        Size size = name switch { "Reboot" => new(340, 320), "BurnIn" => new(340, 200), "S3" or "S4" or "CS" => new(340, 340), "Video" => new(340, 160), _ => new(400, 290) };
        Ui.Style(this, name == "BurnIn" ? "BurnIn Test" : name == "Reboot" ? "REBOOT" : name, size);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        Font = new Font("Microsoft YaHei", 9F);
        cycles.Value = this.original.Cycles; delay.Value = this.original.DelaySeconds;
        sleep.Value = this.original.SleepSeconds; hours.Value = (decimal)this.original.Hours;
        if (name == "Reboot")
        {
            Field("次数", cycles, 20, 30, 120, 100);
            Field("倒计时 (秒)", delay, 20, 72, 120, 100);
            Label("检查:", new(20, 114, 120, 26));
            hardware = Check("硬件", this.original.HardwareCheck, new(150, 114, 150, 26));
            time = Check("时间", this.original.TimeCheck, new(150, 146, 150, 26));
            wifi = Check("WIFI", this.original.WifiCheck, new(150, 178, 150, 26));
            fan = Choice("检查-风扇", ["N", "IP3", "WB", "Ed"], this.original.FanProfile, 20, 210, 120, 100);
        }
        else if (name is "S3" or "S4" or "CS")
        {
            Field("次数", cycles, 20, 30, 100, 140);
            Field("倒计时 (秒)", delay, 20, 76, 100, 140);
            Field("睡眠时间 (秒)", sleep, 20, 122, 100, 140);
            Label("检查", new(20, 168, 100, 32));
            hardware = Check("硬件", this.original.HardwareCheck, new(130, 168, 180, 32));
            time = Check("时间", this.original.TimeCheck, new(130, 204, 180, 32));
            wifi = Check("WIFI", this.original.WifiCheck, new(130, 240, 180, 32));
        }
        else if (name == "BurnIn")
        {
            Field("时间 (小时)", hours, 20, 30, 80, 180);
            mode = Choice("", AgingItem.BurnInModeNames, AgingItem.BurnInModeNames[this.original.BurnInMode], 20, 72, 80, 180);
        }
        else if (name is "3DMark" or "FurMark")
        {
            Field("时间 (小时)", hours, 20, 30, 100, 140);
            string[] versions = ReadChoices(configPath, "3DMark", "SupportVersion", ["3DMarkV2_AllItem", "3DMarkV2_Ice_Storm", "3DMarkV2_Sky_Diver", "3DMarkV2_Time_Spy", "3DMarkV2_Fire_Strike", "3DMark11_Extreme", "3DMark11_Performance"]);
            option = Choice(name == "3DMark" ? "版本" : "抗锯齿", name == "3DMark" ? versions : ["0X", "2X", "4X", "8X"], name == "3DMark" ? this.original.MarkVersion : this.original.Msaa, 20, 76, 100, 240);
            string[] defaults = name == "FurMark" ? ["3840x2160", "1920x1080", "1366x768", "1280x720"] : ["3840x2160", "1920x1080", "1366x768", "1280x800", "1280x720", "1024x768"];
            resolution = Choice("分辨率", ReadChoices(configPath, name, "SupportResolution", defaults), this.original.Resolution, 20, 122, 100, 140);
            window = Check("窗口模式", this.original.WindowMode, new(130, 168, 180, 32));
        }
        else Label("Video 使用工具路径中配置的视频文件，\r\n测试结果由操作员判定。", new(20, 20, 300, 64));
        int saveX = name == "Reboot" ? 150 : name == "BurnIn" ? 110 : 130;
        var save = Ui.Button(this, "保存", new(saveX, size.Height - 48, 90, 32), (_, _) => Save());
        AcceptButton = save;
        KeyPreview = true; KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }
    public static AgingItem CreateDefault(string name, string? configPath = null)
    {
        IniDocument? doc = configPath != null && File.Exists(configPath) ? IniDocument.Load(configPath) : null;
        string Get(string key, string fallback) => doc?.Get(name, key, fallback) ?? fallback;
        int Int(string key, int fallback) => int.TryParse(Get(key, ""), out int value) ? value : fallback;
        double Duration() => double.TryParse(Get("Time", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v is >= .1 and <= 10000 ? v : 1;
        string Bool(string key, string fallback = "N") => Get(key, fallback).Trim() is "1" or "Y" ? "Y" : "N";
        return AgingItem.Parse(name switch
        {
            "Reboot" => $"Reboot|{Math.Clamp(Int("Cycles", 50), 1, 100000)}|{Math.Clamp(Int("Delay", 30), 30, 86400)}|{Bool("HWCheck")}|{Bool("TimeCheck")}|{Bool("WifiCheck")}|{Get("FANCheck", "N")}",
            "S3" or "S4" or "CS" => $"{name}|{Math.Clamp(Int("Cycles", 50), 1, 100000)}|{Math.Clamp(Int("Delay", 60), 30, 86400)}|{Math.Clamp(Int("SleepTime", 60), 60, 86400)}|{Bool("HWCheck")}|{Bool("TimeCheck")}|{Bool("WifiCheck")}",
            "BurnIn" => $"BurnIn|{Duration().ToString(CultureInfo.InvariantCulture)}|{Math.Clamp(Int("TestMode", 0), 0, 3)}",
            "DDR Test" => $"BurnIn|{Duration().ToString(CultureInfo.InvariantCulture)}|3",
            "FurMark" => $"FurMark|1|{Get("Anti-aliasing", "8X")}|{Get("Resolution", "1920x1080")}|N",
            "3DMark" => $"3DMark|1|{Get("Version", "3DMarkV2_AllItem")}|{Get("Resolution", "1920x1080")}|N",
            _ => "Video"
        });
    }
    private static string[] ReadChoices(string? path, string section, string key, string[] fallback)
    {
        if (path == null || !File.Exists(path)) return fallback;
        string[] values = IniDocument.Load(path).Get(section, key).Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return values.Length > 0 ? values : fallback;
    }
    private static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals = 0) => new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : .1M, Font = new Font("Microsoft YaHei", 14F) };
    private Label Label(string text, Rectangle bounds) { var c = Ui.Label(this, text, bounds); c.TextAlign = ContentAlignment.MiddleRight; return c; }
    private void Field(string text, Control control, int x, int y, int labelWidth, int inputWidth)
    { if (text.Length > 0) Label(text, new(x, y, labelWidth, 32)); control.Bounds = new(x + labelWidth + 10, y, inputWidth, 32); Controls.Add(control); }
    private CheckBox Check(string text, bool selected, Rectangle bounds) => Ui.Check(this, text, selected, bounds);
    private ComboBox Choice(string text, string[] choices, string selected, int x, int y, int labelWidth, int inputWidth)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Microsoft YaHei", 14F), DropDownWidth = Math.Max(inputWidth, 240) };
        box.Items.AddRange(choices); if (!choices.Contains(selected)) box.Items.Add(selected); box.SelectedItem = selected;
        Field(text, box, x, y, labelWidth, inputWidth); return box;
    }
    private void Save()
    {
        try
        {
            string B(CheckBox? c) => c?.Checked == true ? "Y" : "N";
            string h = hours.Value.ToString(CultureInfo.InvariantCulture);
            string raw = original.Name switch
            {
                "Reboot" => $"Reboot|{cycles.Value}|{delay.Value}|{B(hardware)}|{B(time)}|{B(wifi)}|{fan!.Text}",
                "S3" or "S4" or "CS" => $"{original.Name}|{cycles.Value}|{delay.Value}|{sleep.Value}|{B(hardware)}|{B(time)}|{B(wifi)}",
                "BurnIn" => $"BurnIn|{h}|{mode!.SelectedIndex}",
                "FurMark" => $"FurMark|{h}|{option!.Text}|{resolution!.Text}|{B(window)}",
                "3DMark" => $"3DMark|{h}|{option!.Text}|{resolution!.Text}|{B(window)}", _ => "Video"
            };
            if (original.Kind is TaskKind.S3 or TaskKind.S4 or TaskKind.CS && sleep.Value < 60) throw new FormatException("睡眠时间不应短于 60 秒");
            Result = AgingItem.Parse(raw); DialogResult = DialogResult.OK; Close();
        }
        catch (Exception e) { Ui.Error(this, e); }
    }
}
