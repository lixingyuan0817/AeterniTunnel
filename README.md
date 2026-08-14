# Aeterni Tunnel

轻量级内网穿透工具：把内网服务通过一台公网服务器暴露给外部访问。包含服务端（**ATS**）、客户端（**ATC**）和 Web 管理台，开箱即用。

```
外网用户 ──▶ ATS（公网服务器） ──加密通道──▶ ATC（内网机器） ──▶ 内网服务
                    ↕
             Web 管理台（同一进程内嵌）
```

| 角色 | 职责 |
|---|---|
| **ATS** | 服务端：端口监听、vhost 域名路由、多客户端管理、隧道管理、流量统计 |
| **ATC** | 客户端：连接 ATS、注册隧道、健康检查、断线自动重连 |
| **Web 管理台** | 登录鉴权、客户端树（主机 → 分组 → 隧道）、配置管理、实时日志 |

## 特性

- **多种隧道**：TCP / UDP / HTTP / HTTPS（vhost 域名反代），或直接用 `IP:端口` 访问
- **多客户端**：任意数量客户端同时接入，客户端 → 分组 → 隧道树形视图
- **自动重连**：断线后指数退避重连（1s 起步，上限 30s），隧道配置不丢失
- **配置热更新**：修改 `agent.toml` 自动增删隧道，无需重启客户端
- **流量统计**：每隧道上下行字节与实时速率
- **健康检查**：TCP / HTTP 探测，失败自动摘除，恢复后重新注册
- **管理台**：登录鉴权（webToken）、服务端下发指令删除隧道、ATS 配置在线修改并重启生效、实时日志流

## 快速开始

依赖：**.NET 10 SDK**、**Node.js ≥ 20**（Tailwind v4）

```bash
# 1. 还原并跑测试
dotnet restore AeterniTunnel.slnx
dotnet test AeterniTunnel.slnx

# 2. 启动（ATS 随 Web 一起启动）
cd Aeterni.Tunnel.Web
npm install          # 首次：Tailwind v4 + GSAP
dotnet watch run     # 或 dotnet run
```

启动后访问 **http://localhost:5280**（端口见 `Properties/launchSettings.json`）。

### 登录管理台

管理台使用 **webToken** 登录，三个来源（优先级从高到低）：

| 来源 | 说明 |
|---|---|
| 环境变量 `AETERNI_WEB_TOKEN` | 部署时注入，配置文件中不落明文 |
| `server.toml` 的 `webToken` | 首次启动自动生成（明文打印在启动日志，配置中只存加盐 SHA256 哈希） |
| `--reset-token` | 忘记时重置：`dotnet run -- --reset-token`，输出新明文 |

> 注意：`--reset-token` 是一次性命令，会生成后直接退出；正常启动不要带此参数。

### 接入客户端（ATC）

在**内网机器**上创建 `agent.toml`（与 Web 同目录）：

```toml
serverAddr = "your-server.com"   # ATS 公网地址
serverPort = 17070               # 与服务端 server.toml 的 bindPort 一致
token = "..."                    # 与服务端 server.toml 的 token 一致（首次启动自动生成，见启动日志）
clientId = ""                    # 留空自动生成（agent-主机名-随机数）

[[tunnels]]
name = "web"                     # 隧道名
type = "tcp"                     # tcp / udp / http / https
localAddr = "127.0.0.1"
localPort = 8080                 # 内网服务地址
remotePort = 17061               # 公网端口（须在服务端 allowPorts 范围内）

[[tunnels]]
name = "nas"
type = "tcp"
localAddr = "192.168.1.10"
localPort = 5000
remotePort = 17062
```

启动 ATC：当前阶段客户端以类库形式提供——`AgentHost`（`Engine/Hosting`）可被任何宿主直接嵌入，读取上述 `agent.toml` 并建立隧道；独立客户端可执行程序尚未提供（用法参考 `Aeterni.Tunnel.Engine.Tests/AgentHostTests.cs`）。

之后在管理台即可看到该客户端及其隧道；`remotePort` 即对外访问端口。

## 配置

### server.toml（服务端，Web 目录下）

| 字段 | 默认 | 说明 |
|---|---|---|
| `bindPort` | `7000` | ATS 控制通道端口（ATC 连接用） |
| `token` | 首次自动生成 | ATC 接入凭据（服务端唯一） |
| `webToken` / `webTokenSalt` | 首次自动生成 | 管理台登录，加盐 SHA256 哈希存储 |
| `allowPorts` | 空 = 不限 | 客户端可注册的公网端口或区间，如 `[7071, "7071-7171"]` |
| `maxPortsPerClient` | `0` = 不限 | 单个客户端最多隧道数 |
| `vhostHttpPort` / `vhostHttpsPort` | `0` = 关闭 | 域名反代入口（80/443） |
| `subDomainHost` | 空 | 子域后缀，如 `aeterni.dev` → `myapp.aeterni.dev` |
| `webBind` | `127.0.0.1:7500` | 遗留字段，当前未使用 |

> 配置修改可在管理台「设置」页在线完成（保存后自动重启 ATS 生效）。

## 目录结构

```
AeterniTunnel/
├── Aeterni.Tunnel.Engine/        核心引擎（跨平台类库，ATS/ATC 共用）
│   ├── Protocol/                 消息协议（System.Text.Json 源生成，AOT 友好）
│   ├── Transport/                传输层（TCP / TLS1.3）
│   ├── Wire/                     通道复用（ChannelMultiplexer）
│   ├── Server/                   ATS：监听、会话、端口管理、vhost、流量
│   ├── Client/                   ATC：会话、健康检查、自动重连
│   ├── Config/                   TOML 解析与配置加载
│   ├── Hosting/                  ServerHost / AgentHost 宿主 API
│   └── Logging/                  文件日志（滚动 / 级别）
├── Aeterni.Tunnel.Engine.Tests/  xUnit 测试（85 个）
├── Aeterni.Tunnel.Web/           Blazor Web App 管理台（内嵌 ServerHost）
├── docs/                         文档与可视化
├── AeterniTunnel.slnx
└── global.json                   固定 .NET 10 SDK
```

## 技术栈

- **.NET 10**（C#），解决方案 `AeterniTunnel.slnx`
- **Blazor Web App**（Interactive Server）管理台；登录走静态 SSR + Cookie 认证
- **Tailwind CSS v4**（`wwwroot/css/app.css` 为入口，产物 `app.tailwind.css`）
- **GSAP** 页面动效（`wwwroot/js/aeterni-fx.js`，第三方库本地化，离线可用）
- 协议序列化用 **System.Text.Json 源生成**，支持 NativeAOT 发布

## 开发

```bash
# 测试（Engine）
dotnet test AeterniTunnel.slnx

# 修改 Tailwind 源文件（app.css）后重新生成产物
cd Aeterni.Tunnel.Web
npm run css:build     # 一次性
npm run css:watch     # 开发热编译

# 热重载开发
cd Aeterni.Tunnel.Web
dotnet watch run
```

> `dotnet watch` 只重编译 .NET 代码，**不会**自动生成 Tailwind CSS；改 `app.css` 后需跑 `npm run css:build`（或保持 `css:watch` 常驻）。

### 发布（Release 工作流）

打标签自动构建三平台 **self-contained 单文件**并发布到 GitHub Release：

```bash
git tag v1.0.0 && git push origin v1.0.0
```

产物（免装 .NET 运行时，解压即用）：

| 平台 | 产物 |
|---|---|
| Linux x64 | `aeterni-tunnel-<版本>-linux-x64.tar.gz` |
| macOS arm64 | `aeterni-tunnel-<版本>-osx-arm64.tar.gz` |
| Windows x64 | `aeterni-tunnel-<版本>-win-x64.zip` |

> 为什么不用 NativeAOT：管理台基于 **Blazor InteractiveServer**，组件激活依赖反射，ASP.NET Core 官方不支持 NativeAOT（本地实证：AOT 产物运行即报组件构造器失效）。self-contained 单文件已满足"免装运行时、单文件分发"。纯引擎宿主（未来若提供，不含 Blazor）可另行 AOT。
