# Changelog

## 1.0.2

- 电视关机或网络断开后，`/api/send` 和 CLI `send` 不再把写进半开 TCP 连接的按键当成成功。发送前检查对端是否已关闭，发送后等待这段数据被确认；对端不应答时在几秒内返回错误（HTTP 500，`ok: false`）。保活发现电视不可达后断开并停止，不会空转或反复打日志。
- 启动日志里的 LAN URL 不再使用 Mihomo 等 TUN 的 `198.18.0.0/15`。同时跳过回环、Docker `172.17.0.0/16`、hassio `172.30.32.0/23`，以及 `tun` / `utun` / `Meta` 接口。优先采用通往电视 IP 的路由源地址，或物理网卡上的局域网地址；没有可用地址时列出全部 IPv4 候选。

## 1.0.1

- 启用 .NET runtime async（`Directory.Build.props` 中 `<Features>runtime-async=on</Features>`）：异步方法由运行时（CoreCLR / NativeAOT）直接挂起与恢复，不再生成状态机类。

## 1.0.0

- 首次发布。基于 jarvis2f/tcl-remote 的 .NET 11 Native AOT 移植。
- 握手读取两帧并区分明文与 AES；空闲连接发送零长度保活，断线后重连重试。
- 发现不绑定 UDP 6537，并按 IP 去重。
