import SwiftUI

@main
struct USBPhoneSpeakerApp: App {
    @StateObject private var receiver = Receiver()
    @State private var folderStatus = "正在准备文件夹……"

    var body: some Scene {
        WindowGroup {
          ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                Image(systemName: "hifispeaker.fill").font(.system(size: 56)).foregroundStyle(.cyan)
                Text("USB 喇叭").font(.largeTitle.bold())
                Text("把 Windows 的声音，交给 iPhone。")
                    .foregroundStyle(.secondary)
                Text(receiver.status).frame(maxWidth: .infinity, alignment: .leading)
                Picker("播放缓冲", selection: $receiver.targetMs) {
                    Text("极低 8 ms").tag(UInt32(8))
                    Text("低延迟 15 ms").tag(UInt32(15))
                    Text("稳定 30 ms").tag(UInt32(30))
                }
                .pickerStyle(.segmented)
                .disabled(receiver.active)
                Button {
                    if receiver.active { receiver.stop() } else { receiver.start() }
                } label: {
                    Text(receiver.active ? "停止接收" : "开始接收")
                        .font(.headline).frame(maxWidth: .infinity).padding(.vertical, 10)
                }
                .buttonStyle(.borderedProminent)
                Text(receiver.statistics).font(.footnote.monospacedDigit()).foregroundStyle(.secondary)
                Text("使用手机扬声器，避免蓝牙额外延迟。\n手机音量控制播放响度；无需开启个人热点。")
                    .font(.footnote).foregroundStyle(.secondary)
                Divider()
                Label("USB 传文件", systemImage: "folder.fill").font(.headline)
                Text(folderStatus).font(.footnote).foregroundStyle(.secondary)
                Text("电脑：打开“Apple 设备”，选中 iPhone，在“文件”里选择“USB 喇叭”，添加文件。使用 iTunes 时，在“文件共享”中选择本 App。")
                Text("手机：打开“档案”，进入“浏览”和“我的 iPhone”，再打开“USB 喇叭”文件夹。")
                Text("传文件无需开始接收音频。删除本 App 也会删除这里的文件，请先移走需要保留的文件。")
                    .font(.footnote).foregroundStyle(.secondary)
            }
            .padding(28)
          }
            .preferredColorScheme(.dark)
            .task { prepareSharedFolder() }
        }
    }

    private func prepareSharedFolder() {
        do {
            let files = FileManager.default
            let folder = try files.url(for: .documentDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
            // An unused, empty Documents directory may not appear in the system file browser.
            if try files.contentsOfDirectory(atPath: folder.path).isEmpty {
                let instructions = """
                电脑通过 USB 连接 iPhone 后，在 Apple 设备的文件列表中选择 USB 喇叭，即可添加文件。
                手机打开档案，在浏览、我的 iPhone、USB 喇叭文件夹中管理收到的文件。
                删除 USB 喇叭 App 会同时删除这里的文件，请提前移走需要保留的文件。
                """
                try instructions.write(to: folder.appendingPathComponent("USB 传文件说明.txt"), atomically: true, encoding: .utf8)
            }
            folderStatus = "文件夹已准备好，可通过 USB 传文件。"
        } catch {
            folderStatus = "创建文件夹失败：\(error.localizedDescription)"
        }
    }
}
