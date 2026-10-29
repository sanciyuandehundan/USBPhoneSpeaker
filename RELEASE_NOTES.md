首个实验版本：将 Windows 播放声音通过 USB 直传至 iPhone。

- Windows 便携程序：选择播放设备，捕获系统声音并通过 Apple USB 服务发送。
- iPhone 原生接收端：未签名 IPA，可用 Sideloadly 签名安装，无需个人热点。
- 低延迟设计：未压缩 PCM、事件驱动采集、无锁播放缓冲、积压丢弃，提供 8/15/30 ms 手机目标缓冲。
- GitHub Actions 编译两端，并检查缓冲欠载、积压、环绕和协议头解析。

下载 Windows ZIP 后解压运行 EXE；IPA 交给 Sideloadly 安装。Windows 需要 Apple Devices 或 iTunes 提供的 USB 服务，iPhone 需要 iOS 16 或更新版本。

尚未完成 iPhone 真机收发、锁屏和端到端延迟实测。界面缓冲数字不等于总延迟，当前版本不承诺具体延迟数值。
