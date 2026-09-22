# 项目索引

核对日期：2026-09-22。本文是当前仓库的源码导航，不是重构后的目录承诺，也不代表构建/测试已通过。项目清单以 [解决方案](../AeterniTunnel.slnx) 和各项目文件为依据。

阅读分工：[AGENTS.md](../AGENTS.md) 定义协作规则；[文档索引](./README.md) 导航文档；本文定位项目和代码；[重构方案](./refactor-plan.md) 定义目标；[todolist](./todolist.md) 是唯一任务进度来源。

## 1. 解决方案与项目

所有现有项目目标框架为 net10.0。下表只列直接 ProjectReference，传递引用另见依赖图。

| 项目 | 类型与当前职责 | 直接项目依赖 | 入口 |
|---|---|---|---|
| [Aeterni.Tunnel.Engine](../Aeterni.Tunnel.Engine/Aeterni.Tunnel.Engine.csproj) | 通信类库；ATS/ATC、协议、传输、隧道、配置和宿主 API | Common | [ServerHost](../Aeterni.Tunnel.Engine/Hosting/ServerHost.cs)、[AgentHost](../Aeterni.Tunnel.Engine/Hosting/AgentHost.cs)；没有独立 Program |
| [Aeterni.Tunnel.Common](../Aeterni.Tunnel.Common/Aeterni.Tunnel.Common.csproj) | 基础类库，当前主要实现轻量 TOML 解析 | 无 | [MinimalToml](../Aeterni.Tunnel.Common/MinimalToml.cs) |
| [Aeterni.Tunnel.Web](../Aeterni.Tunnel.Web/Aeterni.Tunnel.Web.csproj) | Blazor Web App；管理台与内嵌 ATS 宿主 | Engine | [Program.cs](../Aeterni.Tunnel.Web/Program.cs) |
| [Aeterni.Tunnel.Desktop](../Aeterni.Tunnel.Desktop/Aeterni.Tunnel.Desktop.csproj) | 已接入 Engine 的 Avalonia ATC 客户端 | Engine | [Program.cs](../Aeterni.Tunnel.Desktop/Program.cs)、[App.axaml.cs](../Aeterni.Tunnel.Desktop/App.axaml.cs) |
| [AeterniLink](../AeterniLink/AeterniLink.csproj) | 独立 Avalonia 界面、控件及托盘入口；目前未引用 Engine | 无 | [Program.cs](../AeterniLink/Program.cs)、[App.axaml.cs](../AeterniLink/App.axaml.cs) |
| [Aeterni.Tunnel.Engine.Tests](../Aeterni.Tunnel.Engine.Tests/Aeterni.Tunnel.Engine.Tests.csproj) | xUnit 通信/配置/宿主等测试，包含真实 socket 场景 | Engine、Common | 测试运行器 |
| [Aeterni.Tunnel.Engine.Benchmarks](../Aeterni.Tunnel.Engine.Benchmarks/Aeterni.Tunnel.Engine.Benchmarks.csproj) | Engine 性能基线控制台；固定负载测量编解码、通道和 TCP/TLS | Engine | [Program.cs](../Aeterni.Tunnel.Engine.Benchmarks/Program.cs) |

AeterniLink 已加入解决方案，但不能据名称认为它是现有 ATC 实现的替代入口。当前 ATC 功能应优先从 Desktop 的 AgentClientService 和 Engine 的 AgentHost 查找。

### 已确定的迁移目标

| 部分 | 目标 | 当前定位 |
|---|---|---|
| 服务端 | Blazor Interactive Server + ServerHost | 保留现有 Web 项目与宿主模型 |
| 客户端 UI | Blazor WebAssembly | 待创建/迁移，当前没有对应项目或启动入口 |
| 客户端桌面壳 | Tauri | 待实现；管理窗口、托盘与原生宿主生命周期 |
| 客户端通信宿主 | 本地原生 .NET 进程承载 Engine，经受控桥接被 UI 调用 | 目标边界已明确，工程名与 IPC 细节在后续 UI-001 冻结 |
| 现有 Avalonia 工程 | 替代客户端验收后退役 Desktop/AeterniLink 的 Avalonia 实现 | 过渡期保留必要兼容/修复，最终产品不再使用 Avalonia |

先完成 Engine 的 EN-000～EN-051，再执行 UI-001～UI-003；不要从当前目录表推断 Avalonia 仍是未来客户端技术栈，也不要将目标项目视为已经存在。以下依赖图、运行入口及发布描述均指当前源码。

~~~mermaid
flowchart TD
    Web["Aeterni.Tunnel.Web"] --> Engine["Aeterni.Tunnel.Engine"]
    Desktop["Aeterni.Tunnel.Desktop"] --> Engine
    Tests["Aeterni.Tunnel.Engine.Tests"] --> Engine
    Tests --> Common["Aeterni.Tunnel.Common"]
    Engine --> Common
    Benchmarks["Aeterni.Tunnel.Engine.Benchmarks"] --> Engine
    Link["AeterniLink：当前无项目引用"]
~~~

图中箭头表示直接项目引用，不是网络流量方向。

## 2. 根目录导航

| 路径 | 用途 |
|---|---|
| [AeterniTunnel.slnx](../AeterniTunnel.slnx) | 解决方案项目清单 |
| [global.json](../global.json) | 本地 .NET SDK 选择与 rollForward 策略 |
| [README.md](../README.md) | 使用、配置、开发与发布说明；细节应与对应源码/工作流核对 |
| [AGENTS.md](../AGENTS.md) | Agent 接手流程、边界与验证要求 |
| [docs/](./README.md) | 架构、台账、项目导航和辅助资料 |
| [.github/workflows/release.yml](../.github/workflows/release.yml) | 实际发布目标、测试和打包步骤 |

bin/、obj/、node_modules/ 属于生成/依赖目录，不作为实现入口。server.toml、agent.toml 属于部署/运行配置，具体读取位置由宿主决定，不在索引中放真实凭证或假定文件已存在。

## 3. 当前运行链路

### ATS 启动与管理

~~~text
Web/Program.cs
  → BuildServerHost / ConfigLoader.ToHostOptions
  → Hosting/ServerHost
  → Server/ServerListener
  → Transport/TcpTlsTransport 接受连接
  → Channels/ChannelMultiplexer
  → Server/ServerSession（Hello、注册、心跳、命令）
~~~

Web Program 注册并预热 ServerHost；ATS 随 Web 进程启动。管理页面通过 [AeterniServerStatusService](../Aeterni.Tunnel.Web/Status/AeterniServerStatusService.cs) 查询快照、保存配置和重启 ATS。

### ATC 启动与隧道

~~~text
Desktop/Program.cs → App → MainWindow / MainWindowViewModel
  → Services/AgentClientService
  → Hosting/AgentHost
  → Client/AgentSession
  → TcpTlsTransport + ChannelMultiplexer
  → ATS 登录、注册和本地服务转发
~~~

Engine 的 AgentHost 可被其他宿主直接使用；目前解决方案没有独立 ATS/ATC CLI 项目。

### 隧道数据路径

- TCP：公网访问 → [ProxyListener](../Aeterni.Tunnel.Engine/Server/ProxyListener.cs) → OpenTunnel / Channel → AgentSession → [TcpBridge](../Aeterni.Tunnel.Engine/Channels/TcpBridge.cs) → 本地服务。
- UDP：公网数据报 → [UdpProxyListener](../Aeterni.Tunnel.Engine/Server/UdpProxyListener.cs) → 同一 TCP 连接上的数据通道 → AgentSession 的本地 UDP 转发。
- HTTP/HTTPS：vhost 监听 → Host/SNI 路由 → 对应客户端通道 → 本地服务。
- 当前 UDP 路径只有最近来源映射；多来源隔离属于 EN-020。当前隧道数据经 ATS 转发，不能把现有 UDP 枚举理解为 P2P 数据面。

客户端统一接入端口与隧道/vhost 业务端口分别看待。重构后的信令仍须共用单一接入端口，具体约束见重构方案。

## 4. Engine 模块索引

以下路径均为当前存在的目录/文件。

| 模块 | 关键文件 | 查找内容 |
|---|---|---|
| Hosting | [ServerHost](../Aeterni.Tunnel.Engine/Hosting/ServerHost.cs)、[ServerHostOptions](../Aeterni.Tunnel.Engine/Hosting/ServerHostOptions.cs)、[AgentHost](../Aeterni.Tunnel.Engine/Hosting/AgentHost.cs)、[ProxyDefinition](../Aeterni.Tunnel.Engine/Hosting/ProxyDefinition.cs) | 启停/重启、隧道集合、健康检查与配置热更新的宿主边界 |
| Client | [AgentSession](../Aeterni.Tunnel.Engine/Client/AgentSession.cs)、[AgentOptions](../Aeterni.Tunnel.Engine/Client/AgentOptions.cs) | 登录、重连、心跳、控制消息和本地转发 |
| 健康检查 | [HealthChecker](../Aeterni.Tunnel.Engine/Client/HealthChecker.cs)、[IHealthChecker](../Aeterni.Tunnel.Engine/Client/IHealthChecker.cs)、[HealthCheckOptions](../Aeterni.Tunnel.Engine/Client/HealthCheckOptions.cs) | TCP/HTTP 可用性检测与宿主摘除/恢复 |
| Server 会话 | [ServerListener](../Aeterni.Tunnel.Engine/Server/ServerListener.cs)、[ServerSession](../Aeterni.Tunnel.Engine/Server/ServerSession.cs) | 接入监听、Hello-first 认证门控、在线会话索引与隧道注册 |
| 端口和转发 | [PortManager](../Aeterni.Tunnel.Engine/Server/PortManager.cs)、[PortRange](../Aeterni.Tunnel.Engine/Server/PortRange.cs)、[ProxyListener](../Aeterni.Tunnel.Engine/Server/ProxyListener.cs)、[UdpProxyListener](../Aeterni.Tunnel.Engine/Server/UdpProxyListener.cs) | 端口限制、分配释放、TCP/UDP 转发 |
| vhost | [VhostHttpListener](../Aeterni.Tunnel.Engine/Server/VhostHttpListener.cs)、[VhostHttpsListener](../Aeterni.Tunnel.Engine/Server/VhostHttpsListener.cs)、[IVhostRegistry](../Aeterni.Tunnel.Engine/Server/IVhostRegistry.cs)、[SniParser](../Aeterni.Tunnel.Engine/Protocol/SniParser.cs) | HTTP Host 与 TLS SNI 路由 |
| 传输 | [ITunnelTransport](../Aeterni.Tunnel.Engine/Transport/ITunnelTransport.cs)、[ITunnelConnection](../Aeterni.Tunnel.Engine/Transport/ITunnelConnection.cs)、[TcpTlsTransport](../Aeterni.Tunnel.Engine/Transport/TcpTlsTransport.cs)、[TcpConnection](../Aeterni.Tunnel.Engine/Transport/TcpConnection.cs) | 连接工厂、字节流、TCP/TLS |
| 通道 | [ChannelMultiplexer](../Aeterni.Tunnel.Engine/Channels/ChannelMultiplexer.cs)、[Channel](../Aeterni.Tunnel.Engine/Channels/Channel.cs)、[TcpBridge](../Aeterni.Tunnel.Engine/Channels/TcpBridge.cs) | 控制/数据分发、队列、背压、连接桥接 |
| 帧协议 | [FrameContract](../Aeterni.Tunnel.Engine/Protocol/FrameContract.cs)、[Frame](../Aeterni.Tunnel.Engine/Protocol/Frame.cs)、[FrameType](../Aeterni.Tunnel.Engine/Protocol/FrameType.cs)、[FrameCodec](../Aeterni.Tunnel.Engine/Wire/FrameCodec.cs)、[ProtocolException](../Aeterni.Tunnel.Engine/Wire/ProtocolException.cs) | 帧头、版本、负载边界、半包粘包 |
| 控制消息 | [Messages/](../Aeterni.Tunnel.Engine/Protocol/Messages)、[MessageCodec](../Aeterni.Tunnel.Engine/Protocol/Messages/MessageCodec.cs)、[MessageJsonContext](../Aeterni.Tunnel.Engine/Protocol/Messages/MessageJsonContext.cs)、[LinkType](../Aeterni.Tunnel.Engine/Protocol/LinkType.cs) | Hello、注册、命令、JSON 类型与源生成；枚举不等于已实现传输 |
| 配置 | [ConfigLoader](../Aeterni.Tunnel.Engine/Config/ConfigLoader.cs)、[ServerConfig](../Aeterni.Tunnel.Engine/Config/ServerConfig.cs)、[AgentConfig](../Aeterni.Tunnel.Engine/Config/AgentConfig.cs)、[LogConfig](../Aeterni.Tunnel.Engine/Config/LogConfig.cs) | TOML 读写、默认值、配置到宿主选项转换 |
| 日志与流量 | [Logging/](../Aeterni.Tunnel.Engine/Logging)、[TrafficCounter](../Aeterni.Tunnel.Engine/Traffic/TrafficCounter.cs) | 滚动日志、日志级别、环形缓冲和字节统计 |
| 引擎状态接口 | [DashboardListener](../Aeterni.Tunnel.Engine/Server/DashboardListener.cs)、[StatusResponse](../Aeterni.Tunnel.Engine/Server/StatusResponse.cs)、[StatusJsonContext](../Aeterni.Tunnel.Engine/Server/StatusJsonContext.cs) | 可选引擎 HTTP 状态接口与快照；区别于 Blazor 管理台 |

Control、Security、Peers、Relay、Tunneling、Diagnostics 是重构方案中的目标模块名，目前没有对应独立目录。不要为修复现有问题先创建这些空目录；按任务逐步提取实现。

## 5. Web、桌面与配置入口

| 需求 | 入口 |
|---|---|
| Web 服务注册、Cookie 认证、内嵌 ATS、重置 webToken | [Web/Program.cs](../Aeterni.Tunnel.Web/Program.cs) |
| 管理台 token 来源、校验与修改 | [AeterniWebAuthService](../Aeterni.Tunnel.Web/Auth/AeterniWebAuthService.cs)、[登录组件](../Aeterni.Tunnel.Web/Components/Auth/Login.razor) |
| 管理台数据和 ATS 配置保存/重启 | [AeterniServerStatusService](../Aeterni.Tunnel.Web/Status/AeterniServerStatusService.cs) |
| 管理页面/路由/布局 | [Pages/](../Aeterni.Tunnel.Web/Components/Pages)、[Routes.razor](../Aeterni.Tunnel.Web/Components/Routes.razor)、[Layout/](../Aeterni.Tunnel.Web/Components/Layout) |
| 桌面交互与状态 | [MainWindowViewModel](../Aeterni.Tunnel.Desktop/ViewModels/MainWindowViewModel.cs)、[Views/](../Aeterni.Tunnel.Desktop/Views)、[Dialogs/](../Aeterni.Tunnel.Desktop/Dialogs) |
| 桌面到 Engine 的适配 | [AgentClientService](../Aeterni.Tunnel.Desktop/Services/AgentClientService.cs) |
| 桌面 agent.toml 写入与路径 | [AgentTomlWriter](../Aeterni.Tunnel.Desktop/Services/AgentTomlWriter.cs) |
| AeterniLink 托盘与窗口行为 | [App.axaml.cs](../AeterniLink/App.axaml.cs)、[MainWindow.axaml.cs](../AeterniLink/MainWindow.axaml.cs)、[Controls/](../AeterniLink/Controls) |

配置定位注意：

- Web 的 server.toml 位于 ContentRootPath；具体拼接逻辑在 Program 和状态服务中。
- Desktop 默认 agent.toml 位于 AppContext.BaseDirectory，由 AgentTomlWriter.DefaultPath 定义；不要默认它位于源码根目录或 Web 目录。
- ConfigLoader 接受宿主传入路径；Engine 类库本身不统一规定所有宿主的配置工作目录。
- ATS token 与管理台 webToken 用途不同；管理台认证逻辑不是 Peer 身份认证实现。

## 6. 测试索引

下表是定位入口，不是覆盖率或测试通过声明。完整测试数以实际运行结果为准。

| 主题 | 测试入口 |
|---|---|
| 帧格式、长度、半包粘包 | [FrameCodecTests](../Aeterni.Tunnel.Engine.Tests/FrameCodecTests.cs) |
| 消息序列化、未知类型行为 | [MessageCodecTests](../Aeterni.Tunnel.Engine.Tests/MessageCodecTests.cs) |
| TCP/TLS 传输 | [TcpTlsTransportTests](../Aeterni.Tunnel.Engine.Tests/TcpTlsTransportTests.cs) |
| 通道隔离、关闭、Ping 和背压 | [ChannelMultiplexerTests](../Aeterni.Tunnel.Engine.Tests/ChannelMultiplexerTests.cs)、[AdvancedTests](../Aeterni.Tunnel.Engine.Tests/AdvancedTests.cs) |
| 登录、注册和控制流程 | [ControlPlaneTests](../Aeterni.Tunnel.Engine.Tests/ControlPlaneTests.cs) |
| TCP/UDP 端到端转发 | [DataPlaneTests](../Aeterni.Tunnel.Engine.Tests/DataPlaneTests.cs) |
| 服务端删除指令、重连后状态 | [CommandTests](../Aeterni.Tunnel.Engine.Tests/CommandTests.cs) |
| 重连、重复会话、多客户端 | [RobustnessTests](../Aeterni.Tunnel.Engine.Tests/RobustnessTests.cs) |
| 宿主、热更新、端口策略和健康恢复 | [AgentHostTests](../Aeterni.Tunnel.Engine.Tests/AgentHostTests.cs)、[HealthCheckerTests](../Aeterni.Tunnel.Engine.Tests/HealthCheckerTests.cs) |
| vhost HTTP、SNI 解析 | [VhostHttpTests](../Aeterni.Tunnel.Engine.Tests/VhostHttpTests.cs)、[SniParserTests](../Aeterni.Tunnel.Engine.Tests/SniParserTests.cs) |
| 端口允许列表/配额、引擎 Dashboard | [DashboardTests](../Aeterni.Tunnel.Engine.Tests/DashboardTests.cs) |
| 配置、日志与流量 | [ConfigTests](../Aeterni.Tunnel.Engine.Tests/ConfigTests.cs)、[LoggingTests](../Aeterni.Tunnel.Engine.Tests/LoggingTests.cs)、[TrafficTests](../Aeterni.Tunnel.Engine.Tests/TrafficTests.cs) |
| 测试执行约束 | [AssemblyInfo.cs](../Aeterni.Tunnel.Engine.Tests/AssemblyInfo.cs)、[xunit.runner.json](../Aeterni.Tunnel.Engine.Tests/xunit.runner.json) |

部分测试会绑定真实端口，不能把套接字/环境失败当成已经通过。现有测试项目不等于已有 Web UI、AeterniLink 或跨 NAT P2P 自动化覆盖。

## 7. 按重构任务定位代码

任务状态和详细依赖只在 todolist 维护。此表仅帮助确定先读哪些文件。

| 任务 | 优先阅读 |
|---|---|
| EN-000 / EN-001 | 测试项目、FrameCodec、ChannelMultiplexer、TcpTlsTransport |
| EN-002 / EN-003 | ServerSession、AgentOptions、TcpTlsTransport、ConfigLoader、Web Program、宿主选项 |
| EN-010 / EN-011 | ITunnelTransport/Connection、FrameContract、Messages、MessageJsonContext |
| EN-012 | AgentSession、ServerSession、ServerListener、AgentHost/ServerHost、AgentClientService |
| EN-020 / EN-021 | UdpProxyListener、ProxyListener、TcpBridge、PortManager、vhost 与注册/注销逻辑 |
| EN-030 / EN-031 | ChannelMultiplexer、Channel、FrameCodec、统一监听与传输工厂 |
| EN-040～EN-044 | 重构方案的 P2P 约束与 EN-010/011 契约；当前没有可直接复用的 Peer 数据面 |
| EN-050 / EN-051 | 测试矩阵、发布工作流、README、实际配置转换及端口入口 |
| UI-001～UI-003（Engine 完成后） | 重构方案第 9 节、Engine 宿主契约、现有 AgentClientService/ViewModel/配置路径、Web 组件与发布工作流；Tauri/WASM 工程尚未创建 |

## 8. 开发与构建入口

命令均为定位参考，本轮索引更新没有执行这些构建或测试。

~~~bash
# 仓库根目录：还原与完整回归
dotnet restore AeterniTunnel.slnx
dotnet test AeterniTunnel.slnx

# 根目录：仅定位某组 Engine 测试
dotnet test Aeterni.Tunnel.Engine.Tests/Aeterni.Tunnel.Engine.Tests.csproj --filter FullyQualifiedName~ControlPlaneTests

# 根目录：运行 Engine 性能基线（--quick 用于快速冒烟）
dotnet run --project Aeterni.Tunnel.Engine.Benchmarks/Aeterni.Tunnel.Engine.Benchmarks.csproj -c Release -- --quick

# 根目录：已有 ATC 桌面入口
dotnet run --project Aeterni.Tunnel.Desktop/Aeterni.Tunnel.Desktop.csproj
~~~

Web 前端依赖与运行：

~~~bash
cd Aeterni.Tunnel.Web
npm install
npm run css:build
dotnet watch run
~~~

- CSS 源为 [wwwroot/css/app.css](../Aeterni.Tunnel.Web/wwwroot/css/app.css)，产物为 [app.tailwind.css](../Aeterni.Tunnel.Web/wwwroot/css/app.tailwind.css)；不要误改同目录之外的 wwwroot/app.css 作为 Tailwind 入口。
- 构建脚本见 [package.json](../Aeterni.Tunnel.Web/package.json)；Web 项目文件定义了 BeforeBuild/BeforePublish 的 TailwindBuild 目标，需要 npm 依赖。样式变更仍按 AGENTS 显式运行 CSS 构建，不依赖 watch 自动发现 CSS 变化。
- Web 开发监听配置见 [launchSettings.json](../Aeterni.Tunnel.Web/Properties/launchSettings.json)；它与 server.toml 中 ATC 连接的 bindPort 不同。
- 发布以 [release.yml](../.github/workflows/release.yml) 为准：当前 Web 为 linux-x64 self-contained 单文件，Desktop 为 win-x64/linux-x64/osx-arm64 NativeAOT；AeterniLink 未出现在发布任务中。此处是工作流配置核对，不代表本轮已验证产物。
- Web 项目编译包含 Tailwind 步骤，因此解决方案构建/测试也需留意前端依赖；纯 Engine 测试可用于定位引擎问题，不能替代阶段要求的完整验证。

## 9. 索引维护

- 新增/删除/重命名项目、调整 ProjectReference、移动关键模块、改变宿主/配置/测试/发布入口时，同步修改本文。
- 新功能落地后，把对应模块从“规划”更新为真实路径；不要提前为未创建的文件添加链接。
- 本文记录职责与位置，不复制任务状态和完成百分比；进度同步 todolist。
- 更新本文时维护 docs/README.md；交接记录写明变更的入口与验证证据。
- 校验相对链接、项目清单和任务引用；架构变化仍须同步 refactor-plan 的决策记录。
