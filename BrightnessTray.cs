using System.Drawing;
using Forms = System.Windows.Forms;

namespace DisplayBrightness;

internal sealed class BrightnessTray : IDisposable
{
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.NotifyIcon icon = new();
    private readonly Forms.ToolStripMenuItem plan = new("定时计划");
    private readonly Forms.ToolStripLabel brightness = new();
    private readonly Icon image;

    public BrightnessTray(Action show, Action exit, Action opening)
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("/DisplayBrightness;component/AppIcon.ico", UriKind.Relative));
        using (var stream = resource.Stream)
        using (var original = new Icon(stream))
            image = new Icon(original, Forms.SystemInformation.SmallIconSize);

        menu.Items.Add(new Forms.ToolStripLabel("当前亮度"));
        menu.Items.Add(brightness);
        menu.Items.Add(plan);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开主窗口", null, (_, _) => show());
        menu.Items.Add("退出", null, (_, _) => exit());
        menu.Opening += (_, _) => opening();
        icon.Icon = image;
        icon.Text = "显示器亮度";
        icon.ContextMenuStrip = menu;
        icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                show();
        };
        icon.Visible = true;
    }

    public void SetBrightness(string[] screens, string planStatus)
    {
        brightness.Text = screens.Length == 0 ? "未检测到屏幕" : string.Join(Environment.NewLine, screens);
        string tooltip = $"显示器亮度\n{brightness.Text}\n{planStatus}";
        icon.Text = tooltip.Length > 127 ? tooltip[..126] + "…" : tooltip;
    }

    public void SetPlan(string heading, IEnumerable<(string Text, bool Active)> rows)
    {
        plan.Text = heading;
        while (plan.DropDownItems.Count > 0)
        {
            Forms.ToolStripItem item = plan.DropDownItems[0];
            plan.DropDownItems.RemoveAt(0);
            item.Dispose();
        }

        foreach (var (text, active) in rows)
            plan.DropDownItems.Add(new Forms.ToolStripLabel(active ? $"进行中 · {text}" : text));
        plan.Enabled = plan.DropDownItems.Count > 0;
    }

    public void Dispose()
    {
        icon.Dispose();
        menu.Dispose();
        image.Dispose();
    }
}
