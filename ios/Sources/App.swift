import SwiftUI

@main
struct USBPhoneSpeakerApp: App {
    @StateObject private var receiver = Receiver()

    var body: some Scene {
        WindowGroup {
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
                Spacer()
                Text("使用手机扬声器，避免蓝牙额外延迟。\n手机音量控制播放响度；无需开启个人热点。")
                    .font(.footnote).foregroundStyle(.secondary)
            }
            .padding(28)
            .preferredColorScheme(.dark)
        }
    }
}
