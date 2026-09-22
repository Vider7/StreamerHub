using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace StreamerHub;

public static class TrayApp
{
    static NotifyIcon? _tray;

    public static void Start(string url, Action onQuit, UpdateService updater)
    {
        var t = new Thread(() => Run(url, onQuit, updater))
        {
            IsBackground = true,
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static void Run(string url, Action onQuit, UpdateService updater)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => OpenUrl(url));
        menu.Items.Add(new ToolStripSeparator());
        var updateNow = new ToolStripMenuItem("Update now") { Enabled = false };
        updateNow.Click += (_, _) =>
        {
            if (updater.ApplyNow() != 0)
            {
                System.Windows.Forms.MessageBox.Show("The update could not be applied.\nSee logs/app.log for details.", "StreamerHub update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        menu.Items.Add("Check for updates", null, (_, _) => updater.CheckNow());
        menu.Items.Add(updateNow);
        menu.Opening += (_, _) => updateNow.Enabled = updater.Ready;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) =>
        {
            if (_tray != null) _tray.Visible = false;
            onQuit();
            Application.Exit();
        });

        _tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "StreamerHub - dashboard running",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenUrl(url);

        var host = new Form
        {
            ShowInTaskbar = false,
            Opacity = 0,
            WindowState = FormWindowState.Minimized,
        };
        Application.Run(host);
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(255, 159, 28));
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var fg = new SolidBrush(Color.FromArgb(255, 10, 10, 12));
            var tri = new[]
            {
                new Point(14, 10),
                new Point(14, 22),
                new Point(23, 16),
            };
            g.FillPolygon(fg, tri);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("open browser failed: " + ex.Message);
        }
    }
}