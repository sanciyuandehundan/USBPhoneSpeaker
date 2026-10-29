# USBPhoneSpeaker · iPhone USB 喇叭

将 Windows 的播放声音通过 USB 数据线传给 iPhone 原生 App。无需个人热点、Wi-Fi 或订阅。MIT 开源。

这是首个实验版本：自动编译与缓冲测试不能代替 iPhone 真机验收，端到端延迟尚未实测。

## 安装与使用

1. 在 [Releases](https://github.com/sanciyuandehundan/USBPhoneSpeaker/releases) 下载 Windows ZIP 和 `USBPhoneSpeaker-unsigned.ipa`。
2. Windows 安装 Apple Devices 或 iTunes，使 Apple Mobile Device Service 可用。使用支持数据传输的 USB 线连接 iPhone，在手机上点击“信任”。
3. 将 IPA 拖进 Sideloadly，用自己的 Apple 账号签名安装。此 IPA 未签名，不能直接点击安装；按 Sideloadly 和 iOS 的提示信任开发者，若要求则开启开发者模式。免费账号的签名有效期由苹果规定，到期需重新签名。
4. 打开手机上的“USB 喇叭”，选缓冲档位，点击“开始接收”。
5. 解压 Windows ZIP，运行 `USBPhoneSpeaker.exe`，选 USB 手机和电脑的播放设备，点击“开始发送”。
6. 在电脑播放声音，调节 iPhone 音量。切换 Windows 播放设备后，需要停止、刷新并重新选择。

## 延迟优先的实现

- Windows 使用事件驱动 WASAPI loopback，申请 10 ms 采集缓冲；实际周期由声卡和系统决定。
- float32 PCM 原样传输，无编码、解码或浏览器播放缓冲。48 kHz 立体声约 384 KB/s。
- 通过 Apple USB 多路复用服务直连手机 `127.0.0.1:24680`，只列出 USB 设备，禁用 Nagle；不经过 Wi-Fi。
- Windows 发送队列只保留两个采集块，堵塞时丢旧块；单次写入超过 150 ms 则中断，提示重新连接。
- iPhone 用 AVAudioSourceNode 和 C11 原子变量构成的单生产者、单消费者环形缓冲。音频回调不分配内存、不加锁。
- 手机申请 5 ms 系统 I/O 周期，提供 8、15、30 ms 目标缓冲，默认 15 ms。缓冲积压超过目标加 25 ms 时追赶最新音频。界面显示实际手机缓冲、I/O 周期和欠载次数。

这些数值是申请值或单个环节的缓冲，不是“总延迟保证”。USB 服务内部、Windows 音频引擎和 iPhone 输出路径也会增加延迟。优先用 iPhone 自带扬声器；蓝牙耳机通常会增加明显延迟。8 ms 档如果断续，改成 15 或 30 ms。

测总延迟可同时录下电脑本地播放的短促点击声和 iPhone 发出的点击声，在录音波形中比较两次脉冲的间隔，并考虑两个扬声器到麦克风的距离。用这种实测决定是否满足游戏或视频使用要求。

## 当前限制与排错

- 需要 Windows 10 1703 或更新版本，推荐 Windows 11；iPhone 需要 iOS 16 或更新版本。
- 这是转发电脑已有播放设备的声音，不会在 Windows 新增一个虚拟声卡。电脑端也可能发声；不要直接把 Windows 输出静音，否则转发的音频也可能静音。可选择真实连接的耳机输出，并保持该输出音量。
- 受版权保护的音频可能不允许 loopback 捕获；不绕过系统保护。
- 电话打断或切换手机音频输出后，需要重新开始接收和发送。锁屏后台播放已配置，但持续后台播放需要真机验证。
- 未识别手机时，检查 Apple Mobile Device Service、USB 线和“信任此电脑”；连接失败时先启动手机接收。无需开启个人热点或关闭防火墙。
- 软件只监听手机回环地址，不把声音发布到局域网；不会上传音频到 GitHub 或其他云服务。

## 从源码构建

Windows 安装 .NET 9 SDK：

```powershell
dotnet publish windows/USBPhoneSpeaker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o build/windows
```

iPhone 端需要 macOS、Xcode 和 XcodeGen。GitHub Actions 提供 macOS 构建机，所以 Windows 用户无需自己拥有 Mac：

```sh
brew install xcodegen
cd ios
xcodegen generate
xcodebuild -project USBPhoneSpeaker.xcodeproj -scheme USBPhoneSpeaker -configuration Release -sdk iphoneos -destination 'generic/platform=iOS' -archivePath ../build/USBPhoneSpeaker.xcarchive CODE_SIGNING_ALLOWED=NO archive
```

每次推送都会构建两端，并在 Actions 提供产物；推送 `v*` 标签会发布到 Releases。工作流不需要开发者证书或 Apple 账号，签名只在你自己的 Sideloadly 中进行。

音频缓冲与协议测试：

```sh
clang -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined tests/ring_test.c ios/Sources/AudioRing.c -o ring_test
./ring_test
```

协议：16 字节头部 `USPK`、小端 uint32 版本 `1`、采样率、声道数。手机准备好音频引擎后返回单字节 `1`，随后电脑连续发送小端 float32 交错 PCM。一次连接只使用一个固定格式；改变格式需要重连。

实现参考：[Apple 音频源节点](https://developer.apple.com/documentation/avfaudio/avaudiosourcenode)、[NAudio](https://github.com/naudio/NAudio)、[libusbmuxd 协议实现](https://github.com/libimobiledevice/libusbmuxd)。本项目独立实现所需的 USB 服务协议，不包含或分发 libusbmuxd。
