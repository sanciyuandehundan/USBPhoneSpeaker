import AVFoundation
import Network
import SwiftUI

final class AudioPlayback {
    let engine = AVAudioEngine()
    let ring: OpaquePointer
    let rate: UInt32
    let channels: UInt32
    private let pointers: UnsafeMutablePointer<UnsafeMutablePointer<Float>?>

    init(rate: UInt32, channels: UInt32, targetMs: UInt32) throws {
        self.rate = rate
        self.channels = channels
        guard let ring = ring_create(rate, channels, targetMs) else {
            throw NSError(domain: "Audio", code: 1, userInfo: [NSLocalizedDescriptionKey: "无法分配音频缓冲"])
        }
        self.ring = ring
        pointers = .allocate(capacity: Int(channels))
        pointers.initialize(repeating: nil, count: Int(channels))
        let pointers = self.pointers
        let format = AVAudioFormat(standardFormatWithSampleRate: Double(rate), channels: channels)!
        let source = AVAudioSourceNode(format: format) { _, _, frames, buffers in
            let list = UnsafeMutableAudioBufferListPointer(buffers)
            for channel in 0..<Int(channels) {
                pointers[channel] = list[channel].mData!.assumingMemoryBound(to: Float.self)
            }
            ring_read(ring, pointers, frames)
            return noErr
        }
        engine.attach(source)
        engine.connect(source, to: engine.mainMixerNode, format: format)
        engine.prepare()
        try engine.start()
    }

    deinit {
        engine.stop()
        pointers.deinitialize(count: Int(channels))
        pointers.deallocate()
        ring_destroy(ring)
    }
}

final class Receiver: ObservableObject {
    @Published var active = false
    @Published var status = "用 USB 连接电脑，解锁后选择“信任”。"
    @Published var statistics = "等待连接"
    @Published var targetMs: UInt32 = 15

    private let queue = DispatchQueue(label: "USBPhoneSpeaker.receive", qos: .userInteractive)
    private var listener: NWListener?
    private var connection: NWConnection?
    private var playback: AudioPlayback?
    private var pending = Data()
    private var timer: DispatchSourceTimer?
    private var selectedTarget: UInt32 = 15
    private var observers: [NSObjectProtocol] = []

    init() {
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] note in
            guard let raw = note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt,
                  raw == AVAudioSession.InterruptionType.began.rawValue else { return }
            self?.stop(message: "播放被电话或其他音频打断，请结束后重新开始接收。")
        })
        observers.append(center.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: .main) { [weak self] note in
            guard let self, self.active else { return }
            guard let raw = note.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt,
                  let reason = AVAudioSession.RouteChangeReason(rawValue: raw),
                  reason == .newDeviceAvailable || reason == .oldDeviceUnavailable else { return }
            self.stop(message: "音频输出设备已变化，请重新开始接收。低延迟请使用手机扬声器。")
        })
    }

    deinit { observers.forEach(NotificationCenter.default.removeObserver) }

    func start() {
        let target = targetMs
        active = true
        queue.async { [self] in
            do {
                let session = AVAudioSession.sharedInstance()
                try session.setCategory(.playback, mode: .default)
                try session.setPreferredSampleRate(48000)
                try session.setPreferredIOBufferDuration(0.005)
                try session.setActive(true)
                selectedTarget = target
                let tcp = NWProtocolTCP.Options()
                tcp.noDelay = true
                let parameters = NWParameters(tls: nil, tcp: tcp)
                // usbmux forwards to device loopback; never expose computer audio over Wi-Fi.
                parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: 24680)
                let listener = try NWListener(using: parameters)
                self.listener = listener
                listener.stateUpdateHandler = { [weak self, weak listener] state in
                    guard let self, let listener, self.listener === listener else { return }
                    switch state {
                    case .ready: self.update("等待电脑连接。请在 Windows 端点击“开始发送”。")
                    case .failed(let error): self.fail(error.localizedDescription)
                    default: break
                    }
                }
                listener.newConnectionHandler = { [weak self] incoming in self?.accept(incoming) }
                listener.start(queue: queue)
                let timer = DispatchSource.makeTimerSource(queue: queue)
                timer.schedule(deadline: .now() + 1, repeating: 1)
                timer.setEventHandler { [weak self] in self?.updateStatistics() }
                self.timer = timer
                timer.resume()
            } catch { fail(error.localizedDescription) }
        }
    }

    func stop(message: String = "已停止接收。") {
        active = false
        queue.async { [self] in
            cleanup()
            do { try AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation) }
            catch { update("停止音频会话失败：\(error.localizedDescription)") ; return }
            update(message)
        }
    }

    private func cleanup() {
        listener?.cancel(); listener = nil
        connection?.cancel(); connection = nil
        timer?.cancel(); timer = nil
        playback = nil
        pending.removeAll(keepingCapacity: true)
    }

    private func fail(_ message: String) {
        cleanup()
        DispatchQueue.main.async { self.active = false; self.status = message; self.statistics = "连接已结束" }
    }

    private func update(_ message: String) {
        DispatchQueue.main.async { self.status = message }
    }

    private func accept(_ incoming: NWConnection) {
        guard connection == nil else { incoming.cancel(); return }
        connection = incoming
        pending.removeAll(keepingCapacity: true)
        incoming.stateUpdateHandler = { [weak self, weak incoming] state in
            guard let self, let incoming, self.connection === incoming else { return }
            switch state {
            case .ready: self.receive(incoming)
            case .failed(let error): self.disconnected(error.localizedDescription)
            default: break
            }
        }
        incoming.start(queue: queue)
    }

    private func disconnected(_ message: String) {
        connection?.cancel(); connection = nil
        playback = nil
        pending.removeAll(keepingCapacity: true)
        update("\(message)\n等待电脑重新连接。")
        DispatchQueue.main.async { self.statistics = "等待连接" }
    }

    private func receive(_ incoming: NWConnection) {
        incoming.receive(minimumIncompleteLength: 1, maximumLength: 32768) { [weak self, weak incoming] data, _, complete, error in
            guard let self, let incoming, self.connection === incoming else { return }
            if let data {
                self.pending.append(data)
                do { try self.consume(incoming) }
                catch { self.disconnected(error.localizedDescription); return }
            }
            if let error { self.disconnected(error.localizedDescription); return }
            if complete { self.disconnected("电脑已停止发送。"); return }
            self.receive(incoming)
        }
    }

    private func consume(_ incoming: NWConnection) throws {
        if playback == nil {
            guard pending.count >= 16 else { return }
            var rate: UInt32 = 0
            var channels: UInt32 = 0
            let valid = pending.withUnsafeBytes { bytes in
                parse_header(bytes.baseAddress!.assumingMemoryBound(to: UInt8.self), &rate, &channels)
            }
            guard valid != 0 else {
                throw NSError(domain: "Protocol", code: 1, userInfo: [NSLocalizedDescriptionKey: "不支持的音频格式或协议版本"])
            }
            playback = try AudioPlayback(rate: rate, channels: channels, targetMs: selectedTarget)
            pending.removeFirst(16)
            incoming.send(content: Data([1]), completion: .contentProcessed { [weak self, weak incoming] error in
                guard let self, let incoming, self.connection === incoming else { return }
                if let error { self.disconnected(error.localizedDescription) }
            })
            update("正在通过 USB 播放 · \(rate) Hz · \(channels) 声道")
        }
        guard let playback else { return }
        let frameBytes = Int(playback.channels) * 4
        let frames = pending.count / frameBytes
        if frames > 0 {
            pending.withUnsafeBytes { bytes in
                ring_write(playback.ring, bytes.baseAddress!.assumingMemoryBound(to: Float.self), UInt32(frames))
            }
            pending.removeFirst(frames * frameBytes)
        }
    }

    private func updateStatistics() {
        guard let playback else { return }
        let buffered = Double(ring_buffered(playback.ring)) * 1000 / Double(playback.rate)
        let drops = Double(ring_dropped(playback.ring)) * 1000 / Double(playback.rate)
        let underruns = ring_underruns(playback.ring)
        let session = AVAudioSession.sharedInstance()
        let text = String(format: "手机缓冲 %.1f ms · 系统 I/O %.1f ms\n欠载 %u 次 · 丢弃积压 %.0f ms\n以上不是端到端总延迟。", buffered, session.ioBufferDuration * 1000, underruns, drops)
        DispatchQueue.main.async { self.statistics = text }
    }
}
