# UEM-Control Unity

`UEM-Control` 是面向 PICO 头显的 Unity 控制端项目，用于在 XR 场景中集成 H5 控制界面、RTSP/USB 摄像头画面、PICO 透视显示，以及手腕/右手姿态数据发送。

## 功能概览

- PICO 透视显示：`PicoSeeThroughBootstrap` 在启动和恢复时启用 `PXR_Manager.EnableVideoSeeThrough`。
- H5 控制面板：`UemH5WebViewPanel` 默认加载 `http://127.0.0.1:8070/control_ui/index.html`，并通过 Android 离屏 WebView 渲染到 Unity 面板。
- 网络摄像头面板：`UemNetworkCameraPanel` 使用 LibVLCSharp 打开 RTSP 流，默认地址为 `rtsp://192.168.3.10:8554/usb_camera`。
- USB 摄像头面板：`UemUsbCameraPanel` 使用 `WebCamTexture` 自动选择 USB/UVC/DSJ 摄像头，默认请求 `1280x720@30fps`。
- 腕部控制发送：`WristPoseTcpSender` 默认向 `127.0.0.1:5005` 发送右手腕位姿 CSV；`WristPoseControlToggle` 提供按住启用的 deadman 控制。
- 右手姿态发送：`RightHandUnityUdpTcpSender` 默认向 `127.0.0.1:5006` 发送右手 21 关节 JSON 行数据。

## 环境要求

- Unity `2022.3.15f1c1`
- Android Build Support
- PICO Unity Integration SDK
- PICO 头显或可运行 XR Hands 的目标设备

当前 `Packages/manifest.json` 中的 PICO XR 包使用本地路径依赖：

```json
"com.unity.xr.picoxr": "file:D:/桌面1/PICO-Unity-Integration-SDK-release_3.4.0"
```

如果在另一台机器打开项目，需要先准备同版本 PICO SDK，并把该路径改成本机实际路径。

## 快速开始

1. 克隆仓库：

   ```powershell
   git clone git@github.com:PandaHu33/UEM_Control_Unity.git
   ```

2. 用 Unity Hub 打开仓库根目录。

3. 确认 `Packages/manifest.json` 里的本地 PICO SDK 路径有效。

4. 打开主场景：

   ```text
   Assets/Scenes/SampleScene.unity
   ```

5. 按实际运行环境检查 Inspector 中的地址和端口：

   | 模块 | 默认值 | 说明 |
   | --- | --- | --- |
   | H5 WebView | `http://127.0.0.1:8070/control_ui/index.html` | H5 控制界面地址 |
   | RTSP Camera | `rtsp://192.168.3.10:8554/usb_camera` | 网络摄像头流 |
   | WristPoseTcpSender | `127.0.0.1:5005` | 手腕位姿 CSV 接收端 |
   | RightHandUnityUdpTcpSender | `127.0.0.1:5006` | 右手 21 关节 JSON 接收端 |

6. 切换到 Android 平台后构建并部署到 PICO。

如果接收端运行在电脑上，头显内的 `127.0.0.1` 指向头显自身；需要把 Host 改成电脑局域网 IP，或使用 `adb reverse` / 其他转发方式。

## 主要目录

```text
Assets/Scripts/      Unity 运行逻辑脚本
Assets/Plugins/      Android WebView、LibVLCSharp 和平台原生库
Assets/Scenes/       主场景
Packages/            Unity Package Manager 依赖
ProjectSettings/     Unity 项目设置
```

配套的 H5、桥接、手部追踪和 RTSP 启动脚本通常位于同一工作区下的 `Control-Program-For-5-DOF`。

## 数据格式

`WristPoseTcpSender` 每行发送一条 CSV：

```text
sequence,time,hand,tracked,px,py,pz,qx,qy,qz,qw
```

其中 `tracked` 为 `1` 时表示该行包含有效手腕位姿；为 `0` 时表示当前不可用或控制未启用。

`RightHandUnityUdpTcpSender` 每行发送一个 JSON 对象，包含 `frameId`、`unityTime`、`rightTracked`、`rightPositions`、`rightRotations`，关节顺序映射到 21 点手部格式。

## Git 说明

仓库使用 GitHub 官方 Unity `.gitignore` 模板。以下内容不应提交：

- `Library/`
- `Temp/`
- `Obj/` / `obj/`
- `Build/` / `Builds/`
- `Logs/`
- `UserSettings/`
- Unity 自动生成的 `.csproj` 和 `.sln`

这些文件会由 Unity 本地重新生成；提交时只保留 `Assets/`、`Packages/`、`ProjectSettings/` 和必要的项目配置。

## 常见问题

- Unity 打开时报 PICO XR 包缺失：检查 `Packages/manifest.json` 中的本地 `file:` 路径。
- H5 面板为空：确认 H5 服务已启动，或在 `UemH5WebViewPanel` 中修改 `url`。
- RTSP 画面不显示：确认 RTSP 地址可访问，并检查 LibVLCSharp 日志。
- USB 摄像头不显示：确认 PICO Android Camera 权限、USB/UVC 设备枚举，以及扩展坞供电。
- 手部数据没有发出：确认 XR Hands 子系统运行、手部被追踪，并且接收端监听了对应 TCP 端口。
