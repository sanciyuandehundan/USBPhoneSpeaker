using NAudio.CoreAudioApi;

namespace USBPhoneSpeaker;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal record OutputDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

internal sealed class MainForm : Form
{
    readonly ComboBox phones = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox outputs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Button refresh = new() { Text = "刷新设备", AutoSize = true };
    readonly Button start = new() { Text = "开始发送", AutoSize = true, Enabled = false };
    readonly Label status = new() { Text = "连接 iPhone 后点击刷新设备。", Dock = DockStyle.Fill, AutoSize = true };
    CancellationTokenSource? session;
    bool closeAfterStop;

    public MainForm()
    {
        Text = "USBPhoneSpeaker · iPhone USB 喇叭";
        ClientSize = new Size(650, 420);
        MinimumSize = new Size(570, 420);
        Font = new Font("Microsoft YaHei UI", 10);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 8 };
        panel.Controls.Add(new Label { Text = "把电脑声音送到 iPhone", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), AutoSize = true });
        panel.Controls.Add(new Label { Text = "USB 直连 · 无需个人热点或 Wi-Fi · 原生低延迟播放", AutoSize = true, Margin = new Padding(0, 8, 0, 18) });
        panel.Controls.Add(new Label { Text = "USB 设备（手机需解锁并信任此电脑）", AutoSize = true });
        panel.Controls.Add(phones);
        panel.Controls.Add(new Label { Text = "要转发的电脑播放设备", AutoSize = true, Margin = new Padding(0, 12, 0, 0) });
        panel.Controls.Add(outputs);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 16) };
        buttons.Controls.AddRange([refresh, start]);
        panel.Controls.Add(buttons);
        panel.Controls.Add(status);
        Controls.Add(panel);
        refresh.Click += async (_, _) => await RefreshDevices();
        start.Click += async (_, _) => await Toggle();
        FormClosing += OnClosing;
        Shown += async (_, _) => await RefreshDevices();
    }

    async Task RefreshDevices()
    {
        refresh.Enabled = start.Enabled = false;
        status.Text = "正在查找 USB 设备……";
        try
        {
            using var devices = new MMDeviceEnumerator();
            outputs.Items.Clear();
            using var defaultDevice = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string defaultId = defaultDevice.ID;
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    int index = outputs.Items.Add(new OutputDevice(device.ID, device.FriendlyName));
                    if (device.ID == defaultId) outputs.SelectedIndex = index;
                }
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var found = await Usbmux.List(timeout.Token);
            phones.Items.Clear();
            phones.Items.AddRange(found.Cast<object>().ToArray());
            if (found.Count > 0) phones.SelectedIndex = 0;
            status.Text = found.Count > 0 ? "先在 iPhone App 点击“开始接收”，然后在这里开始发送。" : "没有发现 USB 设备。请连接数据线、解锁手机并点击“信任”。";
            start.Enabled = found.Count > 0 && outputs.SelectedItem != null;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or OperationCanceledException or System.Runtime.InteropServices.COMException)
        {
            status.Text = "无法读取设备。请安装 Apple Devices 或 iTunes，并确认 Apple Mobile Device Service 正在运行。\n" + ex.Message;
        }
        finally { refresh.Enabled = true; }
    }

    async Task Toggle()
    {
        if (session != null) { session.Cancel(); return; }
        var phone = (Phone)phones.SelectedItem!;
        var output = (OutputDevice)outputs.SelectedItem!;
        session = new CancellationTokenSource();
        var token = session.Token;
        start.Text = "停止发送";
        refresh.Enabled = phones.Enabled = outputs.Enabled = false;
        status.Text = "正在连接手机……";
        try
        {
            await Task.Run(() => AudioSender.Run(phone, output.Id,
                text => BeginInvoke(() => status.Text = text), token), token);
            status.Text = "发送已结束。";
        }
        catch (Exception) when (token.IsCancellationRequested) { status.Text = "已停止。"; }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or NotSupportedException or System.Runtime.InteropServices.COMException)
        { status.Text = "发送中断：" + ex.Message + "\n检查手机接收状态后，可重新点击开始发送。"; }
        finally
        {
            session.Dispose(); session = null;
            start.Text = "开始发送";
            refresh.Enabled = phones.Enabled = outputs.Enabled = true;
            if (closeAfterStop) Close();
        }
    }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (session == null) return;
        e.Cancel = true;
        closeAfterStop = true;
        session.Cancel();
    }
}
