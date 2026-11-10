using Aging.Core;
using Aging.Windows;
using System.Text;

namespace Aging.WinForms;

public sealed class CompatibilityForm : Form
{
    private readonly AppFiles files;
    private MachineCompatibilityReport? report;
    private readonly TaskCompletionSource initialReport = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task ReportReady => initialReport.Task;
    private readonly TextBox text = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    public CompatibilityForm(AppFiles files)
    {
        this.files = files;
        Ui.Style(this, "本机兼容性检测", new Size(940, 650));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6) };
        var refresh = new Button { Text = "重新检测", AutoSize = true }; refresh.Click += async (_, _) => await RefreshReportAsync();
        var export = new Button { Text = "导出本机报告", AutoSize = true }; export.Click += (_, _) => Export();
        bar.Controls.Add(refresh); bar.Controls.Add(export); Controls.Add(text); Controls.Add(bar);
        Shown += async (_, _) => { try { await RefreshReportAsync(); } finally { initialReport.TrySetResult(); } };
    }
    private async Task RefreshReportAsync()
    {
        text.Text = "正在读取本机系统、固件和组件信息，不执行任何硬件测试…";
        try
        {
            report = await MachineCompatibility.CaptureAsync(files.Options);
            MachineCompatibility.Save(report, Path.Combine(files.Root, "compatibility", "latest.json"));
            var content = new StringBuilder($"机型：{report.Manufacturer} / {report.Model}\r\nCPU：{report.Cpu}\r\n固件：{report.FirmwareVersion}\r\n系统：{report.OperatingSystem} {report.WindowsVersion} {report.Architecture}\r\n");
            foreach (string adapter in report.DisplayAdapters) content.AppendLine("显卡：" + adapter);
            content.AppendLine().AppendLine(report.Note).AppendLine();
            foreach (var item in report.Items) content.AppendLine($"[{item.Status}] {item.Name}\r\n  {item.Detail}\r\n");
            text.Text = content.ToString();
        }
        catch (Exception e) { text.Text = "检测未完成：" + e.Message; }
    }
    private void Export()
    {
        if (report == null) return;
        using var dialog = new SaveFileDialog { Filter = "兼容性报告 JSON|*.json", FileName = "GW_Aging_Compatibility.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { MachineCompatibility.Save(report, dialog.FileName); } catch (Exception e) { Ui.Error(this, e); }
    }
}
