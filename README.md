# tcl-remote-dotnet

TCL 电视的局域网遥控器，用 C# / .NET 11 写成，发布为 Native AOT 单文件。包含命令行、Web 遥控，以及可加到 Home Assistant 加载项商店的插件。

这是 [jarvis2f/tcl-remote](https://github.com/jarvis2f/tcl-remote) 的移植。协议和 Web 页面来自该项目（MIT）。本仓库根据 TCL 55F8（协议版本 14）上的实测修正了握手、保活和发现。

## 命令

```bash
tcl-remote discover
tcl-remote --ip 192.168.5.9 send vol_up --repeat 3
tcl-remote --ip 192.168.5.9 send 15
tcl-remote --ip 192.168.5.9 shell
tcl-remote serve --ip 192.168.5.9 --host 127.0.0.1 --port 8765
```

| 命令 | 作用 |
| --- | --- |
| `discover` | 扫描局域网。不绑定 UDP 6537，从临时端口发广播/单播 |
| `send` | 发送按键名或十进制按键码 |
| `shell` | 交互终端 |
| `serve` | Web 页面和 HTTP API |

按键：`power`、`up` `down` `left` `right` `ok` `enter`、`back` `menu` `home`、`vol_up` `vol_down` `mute`、`ch_up` `ch_down`、`mouse_left` `mouse_right`。

环境变量：`TCL_TV_IP`、`TCL_HOST`、`TCL_PORT`、`TCL_NAME`。Home Assistant 插件还会读 `/data/options.json`（路径可用 `TCL_OPTIONS_FILE` 覆盖）。

`serve` 默认只听 `127.0.0.1:8765`。需要局域网访问时再加 `--host 0.0.0.0`，并且只在可信网络里这样做。

## HTTP API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/` | 上游 `remote.html` |
| GET | `/api/health` | 健康检查 |
| GET | `/api/keys` | `{"ok":true,"keys":{...},"default_ip":...}` |
| GET | `/api/discover?timeout=` | 设备列表，超时限制在 0.5–10 秒 |
| POST | `/api/send` | `{"key":"vol_up","ip":"可选","repeat":1}` |
| GET 或 POST | `/api/send/{key}` | 同上，查询参数 `ip`、`repeat`，给 Home Assistant `rest_command` 用 |

缺少或未知的 `key` 返回 400。`repeat` 限制为 1–20。

## 协议上和上游的差别

实测（TCL 55F8，192.168.5.9，协议 v14）里，上游 Python 有几处会丢键或协商错加密，这里按实测改了：

- 握手后电视会发**两帧**。第一帧经常是 AES 的 `253>>1>>0`，第二帧才是明文能力串，`>>` 分割后的第 7 段（下标 6）才是 `algorithmType`。两帧都读；以 `数字>>` 开头的当明文，否则长度是 16 的倍数就按 AES 解密。
- 空闲大约 20 秒电视会拆掉 TCP。同一条连接上大约每 8 秒写一个长度为 0 的帧。按键发送后再写一帧空帧：对端已经关闭时，第一次 `write` 可能成功但数据丢掉，第二次才会报错，这时重连并重发这一键。
- 发现应答是发回发送方的源端口的，所以不必绑定 UDP 6537。同一台电视会连回好几次，按 IP 合并。MAC 里的 `&#058` 会还原成冒号。

AES-128-CBC，密钥 ASCII `tnscreentnscreen`，IV 为 `12 34 56 78 90 AB CD EF` 重复两次，PKCS7。`algorithmType == 1` 时按键加密，否则明文。按键正文是 `149>>{十进制码}`，电视不回包。

## 构建

需要 .NET SDK 11（`global.json` 写的是 `11.0.100-rc.1`，`rollForward: latestMajor`，RC 和以后的 GA 都可以）以及 Native AOT 的本机工具链：Linux 上是 `clang` 和 `zlib1g-dev`，Windows 上是 VS C++ 生成工具。

```bash
dotnet test -c Release
dotnet publish src/TclRemote/TclRemote.csproj -c Release -r linux-x64 -o out
./out/tcl-remote --help
```

发布 RID：`linux-x64`、`linux-arm64`、`linux-musl-x64`、`linux-musl-arm64`、`win-x64`。musl 包请在 Alpine 里编（见 `tcl-remote/Dockerfile`），不要在 glibc 发行版上交叉编译。

JSON 使用源生成，工程打开了 trim / AOT 分析，并在应用项目里把警告当错误。

## Home Assistant

根目录有 `repository.yaml`。把本仓库 URL 加进加载项商店即可看到 `tcl-remote/`。说明、`rest_command`、通用媒体播放器和 ping 传感器示例在 [tcl-remote/DOCS.md](tcl-remote/DOCS.md)。

插件镜像是 **Alpine 3.22 + musl AOT 二进制**。最终层没有 .NET 运行时。本机测得 `linux-x64` / `linux-musl-x64` 可执行文件约 8.9 MB，镜像未压缩约 18 MB、`docker save | gzip` 约 7.7 MB。Debian slim 会带上一整套 glibc 用户态，在国内拉镜像更慢，所以没用它。glibc 的 `linux-x64` / `linux-arm64` 发布包留给普通 Linux。AES 在运行时 `dlopen` `libssl.so.3`，镜像里因此保留了 `libssl3`（Alpine 3.22 基础层里已经有）。

```bash
# 开发机构建（多阶段，含 SDK）
docker build -f tcl-remote/Dockerfile -t ghcr.io/huiyuanai709/tcl-remote-amd64:1.0.2 .

# 只用发布页上下载的 musl 二进制
cd tcl-remote
curl -fL -o tcl-remote https://github.com/huiyuanai709/tcl-remote-dotnet/releases/download/v1.0.2/tcl-remote-linux-musl-arm64
docker build -f Dockerfile.prebuilt -t ghcr.io/huiyuanai709/tcl-remote-aarch64:1.0.2 .
```

打 `v*` tag 时，GitHub Actions 会在对应架构的托管 runner 上发布上述二进制，并推送 `ghcr.io/huiyuanai709/tcl-remote-amd64` 与 `tcl-remote-aarch64`。

## 许可

[MIT](LICENSE)。Copyright (c) 2026 huiyuanai709。协议实现与 `remote.html` 源自 jarvis2f/tcl-remote，Copyright (c) 2026 jarvis2f，同样为 MIT。
