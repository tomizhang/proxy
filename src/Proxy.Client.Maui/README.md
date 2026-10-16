# Cloudflare 代理 iOS 客户端 (基于 .NET MAUI)

本工程是为 Apple iOS (iPhone / iPad) 量身打造的 .NET MAUI 跨平台代理客户端，无缝对接后端的 Cloudflare WebSocket 加密隧道与自定义混淆协议。

---

## 🌟 核心特性

- **现代 iOS 原生设计**：采用卡片化布局与 iOS 配色风格，支持状态大圆标、实时测速仪、Ping 往返延迟监控。
- **双模本地代理服务**：
  - 本地自适应监听 `127.0.0.1:20808`（自适应兼容 SOCKS5 与 HTTP/HTTPS CONNECT 协议）。
  - 支持国内常见域名及局域网直连分流（百度、腾讯、阿里、`*.cn` 等），不浪费代理流量。
- **自定义加密套件**：完整支持 `aes-256-gcm`、`chacha20-poly1305`、`custom-obfs` 动态流混淆与 `plain` 模式。
- **节点优选与 SNI 伪装**：完整支持 Cloudflare CDN 优选 IP 与真实域名 SNI 标头伪装。
- **自动化持久存储**：使用 iOS `NSUserDefaults`（MAUI Preferences API）自动记忆服务器节点配置与密码。

---

## 📱 iOS 手机使用指引

在 iPhone / iPad 上启动并连接代理后，本地将在 `127.0.0.1:20808` 提供代理服务：

### 方式 1：Wi-Fi 手动代理（免安装第三方工具，零门槛）
1. 打开 iPhone【设置】 -> 【无线局域网 (Wi-Fi)】。
2. 点击当前已连接 Wi-Fi 右侧的 **(i)** 蓝色感叹号图标。
3. 滑到最底部，点击【配置代理】 -> 选择【手动】。
4. **服务器**填写：`127.0.0.1`，**端口**填写：`20808`，认证留空，点击右上角【存储】。
5. 此时即可免插件使用 Safari、Chrome 等应用畅游海外网络！

### 方式 2：搭配规则代理工具（Shadowrocket / Surge / Loon / Stash）
1. 在 Shadowrocket（小火箭）等工具中点击【添加节点】。
2. 节点类型选择 **SOCKS5** 或 **HTTP**。
3. 服务器地址填写 `127.0.0.1`，端口填写 `20808`。
4. 开启连接后即可实现全局 iOS VPN 流量接管与规则分流。

---

## 🚀 GitHub Actions 自动化编译打包说明

由于 iOS 编译必须依赖 Apple 的 macOS 与 Xcode 工具链，本项目已配置好完整的 GitHub Actions CI/CD 工作流：

- **工作流文件**：[`.github/workflows/ios-build.yml`](../../.github/workflows/ios-build.yml)
- **云端运行环境**：GitHub 官方 `macos-14` (Apple Silicon M系列虚拟机 + Xcode 15/16 + .NET 8 SDK)。

### 1. 如何触发云端编译
1. 将代码推送 (Push) 到 GitHub 仓库的 `main` 或 `master` 分支。
2. 或在 GitHub 仓库页面点击 **Actions** 选项卡 -> 选择 **Build iOS MAUI App** -> 点击 **Run workflow** 手动一键触发。
3. 发布带有版本标签的 Release（如 `v1.0.0`）会自动附加构建成果。

### 2. 构建产物下载
编译完成后，在 GitHub Actions 任务详情页底部的 **Artifacts** 区域即可下载：
- **`CloudflareProxy-iOS-Unsigned.ipa`**：未签名的 iOS 应用程序包，可直接通过以下方式侧载安装到真机：
  - **TrollStore（巨魔商店）**：免越狱永久签名安装
  - **AltStore / Sideloadly**：免费 Apple ID 侧载安装
  - **牛蛙助手 / 轻松签 / 爱思助手**：自签名安装
- **`CloudflareProxy-iOS-Simulator.zip`**：iOS 模拟器专用 `.app` 压缩包，解压后拖入 Mac Xcode 模拟器即可直接运行调试。

---

## 📂 项目结构

```text
src/Proxy.Client.Maui/
├── Platforms/
│   └── iOS/
│       ├── Info.plist                # iOS 权限声明、后台运行与应用信息
│       ├── AppDelegate.cs            # iOS 生命周期托管
│       └── Program.cs                # iOS 原生入口
├── MainPage.xaml                     # 现代 iOS 卡片化界面
├── MainPage.xaml.cs                  # 代理核心生命周期调度与数据绑定
├── MauiProgram.cs                    # MAUI 启动构建器
└── Proxy.Client.Maui.csproj          # 工程配置文件 (net8.0-ios)
```
