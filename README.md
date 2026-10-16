# C# 自定义加密跨 Cloudflare 代理系统

基于 **.NET 9 (C#)** 构建的高可靠代理工具套件，包含 Kestrel 代理服务端与现代 WinForms 桌面客户端。

---

## 🌟 核心特性

- **穿越 Cloudflare CDN**：
  - 基于 WebSocket (WSS) 二进制数据帧承载，外层完全遵循标准 HTTPS/WSS 规范，可完美穿透 Cloudflare 免费版/付费版 CDN，隐匿真实服务器 IP。
  - 内置 **45 秒主动双向 Heartbeat 心跳机制**，彻底解决 Cloudflare 边缘节点 100 秒无流量自动掐断连接的问题。
  - 服务端根路径自带标准 Nginx 欢迎页伪装，防扫描器主动探测。
- **自定义二进制协议与可插拔加密**：
  - 内部二进制协议帧（Magic + Command + SessionId + Address + Payload），精炼低损耗。
  - 内置多种加密套件插拔切换：
    - `aes-256-gcm` (默认，硬件加速 AEAD 认证加密)
    - `chacha20-poly1305` (跨平台高速流加密，支持自动托管降级)
    - `custom-obfs` (基于动态盐值与滚动掩码的自研混淆算法)
    - `plain` (明文直通，用于测试对比)
- **高可靠服务端 (ASP.NET Core Kestrel)**：
  - 基于对象池 `ArrayPool<byte>` 与高并发管道缓冲设计，消除首包竞态，彻底规避内存泄漏与 GC 卡顿。
  - 自动支持优雅会话生命周期回收与异常隔离。
- **WinForms 桌面客户端**：
  - 包含现代化监控面板：实时上传/下载速率计算（KB/s）、累加流量统计（MB）、双向延迟测速（ms）、运行日志窗口。
  - **TUN 虚拟网卡模式**：原生集成 `wintun.dll` 驱动接口，采用 `0.0.0.0/1` 与 `128.0.0.0/1` 双段路由接管全局流量，并内置自动解析 CDN 节点 IP 的**防回环直连路由**，安全无痕。
  - **本地 SOCKS5 模式**：监听 `127.0.0.1:10808`，供浏览器或指定软件免管理员权限灵活使用。
  - 系统托盘后台驻留与断开安全路由恢复机制。

---

## 📂 项目结构

```
proxy/
├── src/
│   ├── Proxy.Common/            # 核心协议二进制编解码、可插拔加密套件与配置模型
│   ├── Proxy.Server/            # ASP.NET Core Kestrel 代理服务端
│   ├── Proxy.Client.Core/       # 客户端传输引擎、Socks5 服务、Wintun 与路由管理器
│   └── Proxy.Client.WinForms/   # 现代化 WinForms 桌面客户端
├── tests/
│   └── Proxy.Tests/             # 单元测试与端到端 (E2E) 闭环集成测试套件
└── ProxySolution.sln
```

---

## 🚀 快速上手与使用指南

### 1. 运行服务端

在 VPS 服务器上（Linux 或 Windows 均可）：
```bash
cd src/Proxy.Server
dotnet run -- --port 5000 --path /tunnel --password "MySecretToken123!" --algo aes-256-gcm
```
或者发布为独立单个文件直接运行：
```bash
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

### 2. 配置 Cloudflare CDN (小黄云代理)

1. 在 Cloudflare 控制面板将你的二级域名（例如 `proxy.yourdomain.com`）解析到你的 VPS 真实 IP。
2. 确保 **代理状态 (Proxy status)** 处于已开启状态（即橙色小黄云 ☁️ 亮起）。
3. 在 Cloudflare 的 **SSL/TLS** 设置中，将加密模式设置为 **Full**（完全）。
4. 在 VPS 上使用 Nginx / Caddy 配置反向代理至服务端的 `5000` 端口（或直接由 Kestrel 配合 Let's Encrypt 证书监听 443）。

### 3. 运行 WinForms 桌面客户端

启动客户端：
```bash
dotnet run --project src/Proxy.Client.WinForms
```
#### 客户端配置说明：
1. **服务器域名 / CF 节点**：填入你的 Cloudflare 域名（例如 `proxy.yourdomain.com`），或直连 IP。
2. **端口**：走 Cloudflare 时填写 `443`（直连时填写实际端口如 `5000`）。
3. **开启 TLS**：走 Cloudflare CDN 时请务必**勾选**。
4. **WebSocket 路径**：填写服务端一致的路径（如 `/tunnel`）。
5. **SNI 伪装域名**：填入绑定的域名。
6. **鉴权密码 / Token**：填入与服务端一致的密码。
7. **加密算法**：下拉选择相同的加密套件（推荐 `aes-256-gcm`）。
8. **运行模式**：
   - **SOCKS5 模式**：点击“启动代理”后，在系统代理设置或浏览器 SwitchyOmega 中配置 `SOCKS5 127.0.0.1:10808` 即可使用。
   - **TUN 全局模式**：
     - 请将官方提供的 `wintun.dll`（可在 WireGuard 官网下载 x64 版本）放置在客户端可执行文件同目录下；
     - 右键以**管理员身份**运行客户端，勾选“启用 TUN 模式”，点击“启动代理”即可实现全电脑网络流量接管。

---

## 🧪 自动化测试验证

运行完整单元测试与端到端 (E2E) 仿真闭环测试：
```bash
dotnet test
```
测试涵盖：
- ✅ AES-256-GCM 往返加解密与密文篡改拦截
- ✅ ChaCha20-Poly1305 跨平台与纯托管回退验证
- ✅ 自研 Custom-Obfs 动态加盐流混淆验证
- ✅ 二进制协议帧序列化/反序列化完整性
- ✅ 真实 Socket 目标 Echo 端到端穿透与回包校验
"# proxy" 
