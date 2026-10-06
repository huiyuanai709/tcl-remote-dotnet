# Changelog

## 1.0.1

- 启用 .NET runtime async（`Directory.Build.props` 中 `<Features>runtime-async=on</Features>`）：异步方法由运行时（CoreCLR / NativeAOT）直接挂起与恢复，不再生成状态机类。

## 1.0.0

- 首次发布。基于 jarvis2f/tcl-remote 的 .NET 11 Native AOT 移植。
- 握手读取两帧并区分明文与 AES；空闲连接发送零长度保活，断线后重连重试。
- 发现不绑定 UDP 6537，并按 IP 去重。
