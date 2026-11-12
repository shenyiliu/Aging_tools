using Aging.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aging.WinForms;

internal static class Ui
{
    public static readonly Color Teal = Color.FromArgb(0, 120, 215);
    public static readonly Color Pass = Color.FromArgb(38, 140, 37);
    public static readonly Color Fail = Color.FromArgb(222, 43, 43);
    public static readonly Font DefaultFont = new("Microsoft YaHei UI", 9F);
    public static void Style(Form form, string title, Size size)
    {
        form.Text = title; form.Font = DefaultFont; form.AutoScaleMode = AutoScaleMode.Dpi;
        form.BackColor = Color.White; form.ClientSize = size; form.StartPosition = FormStartPosition.CenterParent;
        form.MinimumSize = form.Size; form.Icon = AppBranding.Icon;
    }
    public static Button Button(Control parent, string text, Rectangle bounds, EventHandler click)
    {
        var button = new Button { Text = text, Bounds = bounds, FlatStyle = FlatStyle.Flat, BackColor = Color.White, TabIndex = parent.Controls.Count };
        button.FlatAppearance.BorderColor = Color.FromArgb(210, 215, 220); button.Click += click; parent.Controls.Add(button); return button;
    }
    public static Label Label(Control parent, string text, Rectangle bounds)
    { var label = new Label { Text = text, Bounds = bounds, TextAlign = ContentAlignment.MiddleLeft }; parent.Controls.Add(label); return label; }
    public static TextBox TextBox(Control parent, string value, Rectangle bounds)
    { var box = new TextBox { Text = value, Bounds = bounds }; parent.Controls.Add(box); return box; }
    public static CheckBox Check(Control parent, string text, bool value, Rectangle bounds)
    { var c = new CheckBox { Text = text, Checked = value, Bounds = bounds }; parent.Controls.Add(c); return c; }
    public static void Error(IWin32Window? owner, Exception e) => MessageBox.Show(owner, e.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    public static void ShowModeless(Form form, IWin32Window owner)
    {
        if (!form.Visible) form.Show(owner);
        form.Activate();
    }
    public static string Phase(RunPhase phase, bool simulation = false) => phase switch
    { RunPhase.Idle => "等待", RunPhase.Preflight => "预检", RunPhase.Countdown => "倒计时", RunPhase.Running => "运行", RunPhase.Paused => "已暂停", RunPhase.AwaitingReboot => "等待重启", RunPhase.Completed => simulation ? "演练完成" : "PASS", RunPhase.Failed => simulation ? "演练失败" : "FAIL", RunPhase.Aborted => "已终止", _ => "未执行" };
    public static Color StateColor(RunPhase phase) => phase switch
    { RunPhase.Completed => Pass, RunPhase.Failed or RunPhase.Aborted => Fail, RunPhase.Running or RunPhase.Countdown or RunPhase.AwaitingReboot => Color.FromArgb(249, 159, 15), _ => Color.Gray };
    public static void OpenFolder(string path)
    { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true }); }
    public static void Drag(Form form, Control control)
    { control.MouseDown += (_, e) => { if (e.Button != MouseButtons.Left) return; ReleaseCapture(); SendMessage(form.Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }; }
    public static void Capture(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); bitmap.Save(path);
    }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
