# Changelog

## 1.0.4

- `tv_ip` 仍是上次已知地址。这个地址还能控就继续用，不重新扫描。连不上时按发现应答里的 Wi-Fi MAC（没有则用蓝牙 MAC）重新找到同一台电视并改用新地址，DHCP 变更后不用改配置。
- 留空 `tv_ip` 时，第一次发按键仍自动选择第一台设备并记住它的 MAC；之后地址变化跟随这一台，而不是再次取扫描结果里的第一台。
- 重新发现没有设备、只有别的 MAC，或有多台又还没有记住 MAC 时，不猜测目标，按键返回错误。被替换过的旧地址仍会指到当前地址。

## 1.0.3

- Native AOT 发布启用 Satori GC（SDK `PublishWithSatoriGC` `11.0.0-satori`）。发布命令加上 `--self-contained true`。进程启动时向标准错误写 `[*] SatoriGC=true`，对应 `GC.GetConfigurationVariables().ContainsKey("SatoriGC")`。

## 1.0.2

- 电视关机或网络断开后，`/api/send` 和 CLI `send` 不再把写进半开 TCP 连接的按键当成成功。发送前检查对端是否已关闭，发送后等待这段数据被确认；对端不应答时在几秒内返回错误（HTTP 500，`ok: false`）。保活发现电视不可达后断开并停止，不会空转或反复打日志。
- 启动日志里的 LAN URL 不再使用 Mihomo 等 TUN 的 `198.18.0.0/15`。同时跳过回环、Docker `172.17.0.0/16`、hassio `172.30.32.0/23`，以及 `tun` / `utun` / `Meta` 接口。优先采用通往电视 IP 的路由源地址，或物理网卡上的局域网地址；没有可用地址时列出全部 IPv4 候选。
- 增加信源键 `source`（29，别名 `input`）。`tv` / `tv_home` / `launcher` 回到 TCL 桌面（`home`，19）。`hdmi1` 是宏：`home`、等待 2.5 秒、`source`，回到上次使用的输入；已在 HDMI1 上再按会先经过主页。宏忽略 `repeat`，等待可用 `TCL_MACRO_PAUSE_MS` 修改。Web 遥控在电源键下方加了这三个按钮。

## 1.0.1

- 启用 .NET runtime async（`Directory.Build.props` 中 `<Features>runtime-async=on</Features>`）：异步方法由运行时（CoreCLR / NativeAOT）直接挂起与恢复，不再生成状态机类。

## 1.0.0

- 首次发布。基于 jarvis2f/tcl-remote 的 .NET 11 Native AOT 移植。
- 握手读取两帧并区分明文与 AES；空闲连接发送零长度保活，断线后重连重试。
- 发现不绑定 UDP 6537，并按 IP 去重。
