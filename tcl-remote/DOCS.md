# TCL Remote

在 Home Assistant 里控制支持 TCL 局域网遥控协议的电视（UDP `6537` 发现，TCP `6553` 控制）。插件跑的是 Native AOT 单文件 `tcl-remote`，不带 .NET 运行时。

镜像基于 Alpine，里面只有 musl 版可执行文件和少量系统库。没有把 `debian-slim` 或完整 SDK 留在最终镜像里，方便在国内网络下的全志 H618（HAOS aarch64）上拉取。

## 安装

1. 设置 → 加载项 → 加载项商店 → 右上角 ⋮ → 仓库，填本仓库地址：

   `https://github.com/huiyuanai709/tcl-remote-dotnet`

2. 刷新后安装 **TCL Remote**。`config.yaml` 里的 `image` 指向 GHCR 上已经编好的小镜像（`aarch64` / `amd64`）。需要先有对应的 `v*` 发布；见下面的「自己构建」。
3. 选项：
   - **tv_ip**：电视 IP。留空则在第一次发按键时自动发现。
   - **port**：Web / API 端口，默认 `8765`。
   - **client_name**：握手时上报的控制端名称。
4. 启动。Web 遥控：`http://<HA主机>:8765/`。

插件使用 `host_network: true`。UDP 广播发现和访问电视的 TCP 才能走局域网。改端口后，用新端口访问；商店里的 Web UI 链接按 `8765` 生成。

HA Core 容器里的 `rest_command` **不能**用 `127.0.0.1` 访问这个插件（那是 Core 自己）。在 HAOS 上通常用宿主机网关 `172.30.32.1`，或电视所在网段能路由到的主机 LAN IP。下面示例用 `172.30.32.1`，不通就换成主机 IP。

关机或待机时，局域网遥控经常没有响应。电源仍建议走红外，和上游项目一样。

## 自己构建

在仓库根目录用 SDK 多阶段构建（拉的是 Alpine SDK，体积仍不小，适合在开发机上做）：

```bash
docker build -f tcl-remote/Dockerfile -t ghcr.io/huiyuanai709/tcl-remote-aarch64:1.0.0 .
```

amd64 把标签换成 `tcl-remote-amd64`。`uname -m` 决定 musl RID，请在目标架构上构建，不要在 x64 上交叉编译 arm64。

已经有 Release 二进制时，不必再拉 SDK。下载 **musl** 包（不要用 glibc 的 `linux-arm64` / `linux-x64`，那两个跑在 Alpine 里会缺动态链接器）：

```bash
cd tcl-remote
curl -fL -o tcl-remote \
  https://github.com/huiyuanai709/tcl-remote-dotnet/releases/download/v1.0.0/tcl-remote-linux-musl-arm64
chmod +x tcl-remote
docker build -f Dockerfile.prebuilt -t ghcr.io/huiyuanai709/tcl-remote-aarch64:1.0.0 .
```

把打好的镜像导入 HA 所在机器的 Docker 后，标签要和 `config.yaml` 里的 `image` 一致，监督器会优先用本地镜像。

## HTTP API

与上游 `web_remote.py` 兼容，并多了健康检查和按键路径，方便 `rest_command`。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/` | Web 遥控页 |
| GET | `/api/health` | `{"ok":true,"status":"healthy"}` |
| GET | `/api/keys` | 按键表和 `default_ip` |
| GET | `/api/discover?timeout=` | 发现，超时限制在 0.5–10 秒 |
| POST | `/api/send` | JSON `{"key":"vol_up","ip":"可选","repeat":1}` |
| GET/POST | `/api/send/{key}?ip=&repeat=` | 同上，给 Home Assistant 用 |

未知或缺少 `key` 返回 400。连接或发送失败返回 500。`repeat` 限制在 1–20。也接受十进制按键码，例如 `/api/send/15`。

## 按键

```text
power
up down left right ok enter
back menu home
vol_up vol_down mute
ch_up ch_down
mouse_left mouse_right
```

## 在线状态

多数电视开机后响应 ping，关机后不响应。把 `<TV_IP>` 换成电视地址。

```yaml
command_line:
  - binary_sensor:
      name: TCL TV Power
      unique_id: tcl_tv_power_ping
      command: "ping -W 1 -c 1 <TV_IP> >/dev/null 2>&1 && echo on || echo off"
      command_timeout: 3
      device_class: connectivity
      payload_on: "on"
      payload_off: "off"
      scan_interval: 10
```

## rest_command

每个常用键一条，或一条通用命令。插件在 HAOS 上通过主机网关访问：

```yaml
rest_command:
  tcl_tv_key:
    url: "http://172.30.32.1:8765/api/send/{{ key }}"
    method: POST
  tcl_vol_up:
    url: "http://172.30.32.1:8765/api/send/vol_up"
    method: GET
  tcl_vol_down:
    url: "http://172.30.32.1:8765/api/send/vol_down"
    method: GET
  tcl_mute:
    url: "http://172.30.32.1:8765/api/send/mute"
    method: GET
  tcl_home:
    url: "http://172.30.32.1:8765/api/send/home"
    method: GET
  tcl_ok:
    url: "http://172.30.32.1:8765/api/send/ok"
    method: GET
  tcl_back:
    url: "http://172.30.32.1:8765/api/send/back"
    method: GET
  tcl_up:
    url: "http://172.30.32.1:8765/api/send/up"
    method: GET
  tcl_down:
    url: "http://172.30.32.1:8765/api/send/down"
    method: GET
  tcl_left:
    url: "http://172.30.32.1:8765/api/send/left"
    method: GET
  tcl_right:
    url: "http://172.30.32.1:8765/api/send/right"
    method: GET
```

开发者工具里可以先调：

```yaml
action: rest_command.tcl_tv_key
data:
  key: home
```

## 电源（红外）

局域网电源键在电视关机时不可靠。红外发射器学到电源码后，用脚本包一层。下面沿用上游示例里的和家亲 GK01 / ESPHome 服务名，请改成你自己的。

```yaml
script:
  tcl_tv_power_toggle:
    alias: TCL TV Power Toggle
    mode: single
    sequence:
      - action: esphome.gk01_send_ir_data
        data:
          ir_data: "<POWER_IR_CODE>"

  tcl_tv_turn_on:
    alias: TCL TV Turn On
    mode: single
    sequence:
      - condition: state
        entity_id: binary_sensor.tcl_tv_power
        state: "off"
      - action: script.tcl_tv_power_toggle
      - delay: "00:00:08"
      - action: homeassistant.update_entity
        target:
          entity_id: binary_sensor.tcl_tv_power

  tcl_tv_turn_off:
    alias: TCL TV Turn Off
    mode: single
    sequence:
      - condition: state
        entity_id: binary_sensor.tcl_tv_power
        state: "on"
      - action: script.tcl_tv_power_toggle
      - delay: "00:00:08"
      - action: homeassistant.update_entity
        target:
          entity_id: binary_sensor.tcl_tv_power
```

没有红外时，可以改成调用 `rest_command.tcl_tv_key`，`key: power`，并接受待机时可能失败。

## 通用媒体播放器

```yaml
media_player:
  - platform: universal
    name: TCL TV
    unique_id: tcl_tv_homekit_remote
    device_class: tv
    state_template: >-
      {% if is_state('binary_sensor.tcl_tv_power', 'on') %}
        on
      {% else %}
        off
      {% endif %}
    commands:
      turn_on:
        action: script.tcl_tv_turn_on
      turn_off:
        action: script.tcl_tv_turn_off
      volume_up:
        action: rest_command.tcl_vol_up
      volume_down:
        action: rest_command.tcl_vol_down
      volume_mute:
        action: rest_command.tcl_mute
      media_play:
        action: rest_command.tcl_ok
      media_pause:
        action: rest_command.tcl_ok
```

## HomeKit 遥控器

```yaml
homekit:
  - name: TCL TV
    mode: accessory
    port: 21064
    filter:
      include_entities:
        - media_player.tcl_tv

automation:
  - id: tcl_tv_homekit_remote_keys
    alias: TCL TV - HomeKit Remote Keys
    mode: queued
    triggers:
      - trigger: event
        event_type: homekit_tv_remote_key_pressed
    variables:
      key_map:
        arrow_up: up
        arrow_down: down
        arrow_left: left
        arrow_right: right
        select: ok
        back: back
        exit: back
        information: menu
        play_pause: ok
      event_entity: "{{ trigger.event.data.get('entity_id') }}"
      key_name: "{{ trigger.event.data.get('key_name') }}"
    conditions:
      - condition: template
        value_template: "{{ key_name in key_map }}"
      - condition: template
        value_template: "{{ event_entity in [none, 'media_player.tcl_tv'] }}"
      - condition: state
        entity_id: binary_sensor.tcl_tv_power
        state: "on"
    actions:
      - action: rest_command.tcl_tv_key
        data:
          key: "{{ key_map[key_name] }}"
```

单独的 accessory 模式方便 iPhone 控制中心只看到这台电视。

## 小爱同学

米家虚拟事件进 HA 后，按事件名发按键。把 `<XIAOMI_EVENT_ENTITY>` 换成你的事件实体。

```yaml
automation:
  - id: tcl_tv_xiaoai_remote_keys
    alias: TCL TV - Xiaoai Remote Keys
    mode: queued
    triggers:
      - trigger: state
        entity_id: <XIAOMI_EVENT_ENTITY>
        not_from:
          - unavailable
        not_to:
          - unavailable
          - unknown
    variables:
      event_name: "{{ trigger.to_state.attributes.get('事件名称') }}"
      key_map:
        电视上: up
        电视下: down
        电视左: left
        电视右: right
        电视确定: ok
        电视返回: back
        电视首页: home
        电视菜单: menu
        电视静音: mute
        电视音量加: vol_up
        电视音量减: vol_down
        电视频道加: ch_up
        电视频道减: ch_down
    conditions:
      - condition: template
        value_template: >-
          {% set age = as_timestamp(now()) - as_timestamp(trigger.to_state.state, 0) %}
          {{ age >= 0 and age <= 5 }}
      - condition: template
        value_template: "{{ event_name in key_map }}"
      - condition: state
        entity_id: binary_sensor.tcl_tv_power
        state: "on"
    actions:
      - action: rest_command.tcl_tv_key
        data:
          key: "{{ key_map[event_name] }}"
```

米家里为每句语音触发同名虚拟事件，例如听到「电视音量加」就上报事件名称「电视音量加」。电源仍走上面的开关脚本。

## 说明

- `ping` 只是近似状态：待机仍应答 ICMP 会显示开，开机禁 ping 会显示关。
- 插件没有鉴权。`host_network` 会把 `port` 暴露在主机上，只在可信局域网使用。
- 电视约 20 秒无数据会断开 TCP。程序每约 8 秒发送零长度帧；若连接已经死掉，下一次按键会重连并重发，避免静默丢键。
- 镜像选择 Alpine + `linux-musl-*`，是为了最终层只有 Alpine 和单个可执行文件。glibc 的 `linux-x64` / `linux-arm64` 发布包给普通 Linux 用，不能塞进这个 Alpine 镜像。
