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
    readonly Button sendFiles = new() { Text = "选择文件并发送", AutoSize = true, Enabled = false };
    readonly Label status = new() { Text = "连接 iPhone 后点击刷新设备。", Dock = DockStyle.Fill, AutoSize = true };
    CancellationTokenSource? session;
    bool closeAfterStop;

    public MainForm()
    {
        Text = "USBPhoneSpeaker · iPhone USB 喇叭";
        ClientSize = new Size(700, 460);
        MinimumSize = new Size(620, 460);
        Font = new Font("Microsoft YaHei UI", 10);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 8 };
        panel.Controls.Add(new Label { Text = "把电脑声音送到 iPhone", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), AutoSize = true });
        panel.Controls.Add(new Label { Text = "USB 直连 · 无需个人热点或 Wi-Fi · 原生低延迟播放", AutoSize = true, Margin = new Padding(0, 8, 0, 18) });
        panel.Controls.Add(new Label { Text = "USB 设备（手机需解锁并信任此电脑）", AutoSize = true });
        panel.Controls.Add(phones);
        panel.Controls.Add(new Label { Text = "要转发的电脑播放设备", AutoSize = true, Margin = new Padding(0, 12, 0, 0) });
        panel.Controls.Add(outputs);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 16) };
        buttons.Controls.AddRange([refresh, start, sendFiles]);
        panel.Controls.Add(buttons);
        panel.Controls.Add(status);
        Controls.Add(panel);
        refresh.Click += async (_, _) => await RefreshDevices();
        start.Click += async (_, _) => await Toggle();
        sendFiles.Click += async (_, _) => await SendFiles();
        FormClosing += OnClosing;
        Shown += async (_, _) => await RefreshDevices();
    }

    async Task RefreshDevices()
    {
        refresh.Enabled = start.Enabled = sendFiles.Enabled = false;
        status.Text = "正在查找 USB 设备……";
        try
        {
            using var devices = new MMDeviceEnumerator();
            outputs.Items.Clear();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    outputs.Items.Add(new OutputDevice(device.ID, device.FriendlyName));
                }
            }
            if (outputs.Items.Count > 0)
            {
                using var defaultDevice = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                outputs.SelectedItem = outputs.Items.Cast<OutputDevice>().First(d => d.Id == defaultDevice.ID);
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var found = await Usbmux.List(timeout.Token);
            phones.Items.Clear();
            phones.Items.AddRange(found.Cast<object>().ToArray());
            if (found.Count > 0) phones.SelectedIndex = 0;
            status.Text = found.Count > 0 ? "播放声音：先在手机开始接收，再在这里开始发送。\n传文件：直接点“选择文件并发送”，无需打开 Apple 设备或 iTunes。\n为保持音频低延迟，传文件和播放声音分开进行。" : "没有发现 USB 设备。请连接数据线、解锁手机并点击“信任”。";
            start.Enabled = found.Count > 0 && outputs.SelectedItem != null;
            sendFiles.Enabled = found.Count > 0;
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
        refresh.Enabled = phones.Enabled = outputs.Enabled = sendFiles.Enabled = false;
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
            refresh.Enabled = phones.Enabled = outputs.Enabled = sendFiles.Enabled = true;
            if (closeAfterStop) Close();
        }
    }

    async Task SendFiles()
    {
        if (session != null) { session.Cancel(); return; }
        using var picker = new OpenFileDialog { Title = "选择要传到 iPhone 档案的文件", Multiselect = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var phone = (Phone)phones.SelectedItem!;
        session = new CancellationTokenSource();
        var token = session.Token;
        sendFiles.Text = "取消传文件";
        refresh.Enabled = phones.Enabled = outputs.Enabled = start.Enabled = false;
        status.Text = "正在打开手机文件夹，请保持 iPhone 解锁……";
        try
        {
            var names = await Task.Run(() => FileTransfer.Send(phone, picker.FileNames,
                text => BeginInvoke(() => status.Text = text), token), token);
            status.Text = $"已传入 {names.Count} 个文件。\n在手机“档案”的“我的 iPhone”中打开“USB 喇叭”。\n同名文件会自动加序号，不覆盖原文件。";
        }
        catch (Exception) when (token.IsCancellationRequested) { status.Text = "已取消，原文件不会被覆盖。\n中断的文件保留“正在传输”后缀，可在手机档案中删除。"; }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException
            or UnauthorizedAccessException or System.Security.Authentication.AuthenticationException or System.Security.Cryptography.CryptographicException)
        { status.Text = "传文件失败：" + ex.Message + "\n中断的文件保留“正在传输”后缀。"; }
        finally
        {
            session.Dispose(); session = null;
            sendFiles.Text = "选择文件并发送";
            refresh.Enabled = phones.Enabled = outputs.Enabled = true;
            start.Enabled = outputs.SelectedItem != null;
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
