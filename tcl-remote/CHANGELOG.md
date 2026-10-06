# Changelog

## 1.0.0

- 首次发布。基于 jarvis2f/tcl-remote 的 .NET 11 Native AOT 移植。
- 握手读取两帧并区分明文与 AES；空闲连接发送零长度保活，断线后重连重试。
- 发现不绑定 UDP 6537，并按 IP 去重。
