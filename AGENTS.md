# AGENTS.md

## 项目与当前主线

Aeterni Tunnel 是 .NET 10 跨平台内网穿透工程：

- ATS：公网服务端，提供客户端接入、隧道、端口/vhost 路由及管理。
- ATC：内网客户端，连接 ATS、注册隧道、健康检查和自动重连。
- Engine：共享通信引擎；Web 当前为 Blazor Interactive Server 管理台；Desktop/AeterniLink 当前仍含 Avalonia 实现，属于后续迁移对象。
- Common：现有共享基础代码；Engine.Tests：xUnit 测试；Engine.Benchmarks：可重复的 Engine 性能基线控制台；解决方案为 AeterniTunnel.slnx。

目标技术栈已确定：**服务端 Blazor Interactive Server，客户端 Tauri + Blazor WebAssembly，最终不再使用 Avalonia**。当前主线仍是 **Engine 通信层重构优先**；EN-051 完成后再按 UI-001～UI-003 迁移客户端。推迟的是执行时间，不是技术栈决定。

客户端 Engine 在原生 .NET 本地宿主运行，Blazor WebAssembly UI 经 Tauri 受控桥接调用；不在 WASM/Rust 重写通信核心，不为客户端另开 ATS 登录/信令入口。Engine 阶段不创建 UI 工程或先实现桥接。旧 Avalonia 客户端仅保留必要兼容/修复，替代客户端验收后再清理工程与发布链。

组件库迁移和房间/语音产品均不是前置任务。重构方案属于目标设计，不能视为已实现功能；真实状态以代码和验证证据为准。

## 每次接手的阅读顺序

1. 本文件。
2. [docs/README.md](docs/README.md)：文档导航及各文档职责。
3. [docs/todolist.md](docs/todolist.md)：当前快照、活动任务、依赖、验收卡及最新执行记录。
4. [docs/refactor-plan.md](docs/refactor-plan.md)：通信范围、单端口约束、安全、性能、兼容与决策。
5. [docs/project-index.md](docs/project-index.md)：当前项目依赖、运行入口、模块与测试定位。
6. 任务相关源码、测试和 [README.md](README.md) 的现有使用方式。

开始前查看 git status 和已有差异，保护其他开发者的工作。遇到实际代码与文档不同，先核实并记录，不凭旧会话记忆继续。

用户最新明确指令优先于文档。约束变化应同步架构和台账，并记录替代关系；不要让已失效的旧路线继续指导实现。

## Engine 不可混淆的边界

- Engine 只负责通信、协议、连接身份与授权执行、加密、流/消息/数据报、隧道、P2P/中继、链路恢复和指标。
- Engine 不实现账号产品、房间、成员/禁言/好友、聊天存储、游戏规则、音频采集播放/编解码/降噪或 UI。
- 业务通过宿主 API 和授权提供者使用 Engine；业务负载保持不透明，通信层不解释其业务含义，业务数据库不成为 Engine 依赖。
- 沿用 ATS/ATC/宿主架构；优先内部模块拆分，避免无关重写或为计划中的抽象批量创建项目。

## 单一客户端接入端口

- ATS 只提供一个客户端接入端口。认证、登录、心跳、能力协商、服务注册、Peer 信令和控制扩展均走该入口。
- 同一接入端口允许多条经过认证绑定的数据连接；单端口不要求所有流量共用一条 TCP 连接。
- 允许独立的公网隧道/vhost 业务端口，以及 P2P 直连、STUN/TURN/数据中继所需端口。用途、协议、授权、配额与防火墙要求必须写清。
- 不得新增必须由客户端配置的独立登录/信令/RPC 入口。辅助端点经统一接入协商，不成为基础登录的额外依赖。
- TCP 与 UDP 同数字端口是不同监听；Web 管理入口属于管理员使用，不能被当作客户端必需控制入口。
- 新增监听或连接类型必须检查此约束，并更新相应测试与文档。

## 安全、性能与兼容

- 新通信能力先完成认证、授权和加密，再处理业务数据；未认证请求不能注册服务或分配受保护资源。
- 使用标准密码协议及维护中的实现，不自创加密握手；禁止硬编码 token、webToken、密钥及证书私钥。
- 不自动关闭证书验证、不静默明文重试；旧明文部署与旧线协议分别设计迁移。
- 新消息先能力协商。旧端当前遇到未知消息可能抛异常，不能假设会忽略。
- 可靠流、可靠消息、实时数据报分开定义语义；不承诺跨重连 exactly-once、全网络直连或未经实现的 TCP 无损迁移。
- 队列、并发、帧长度、探测、中继流量和重试都有上限。缓冲池所有权与取消释放必须明确。
- 用同环境基准证明性能变化；不能通过关闭 TLS 或减少安全验证获得表面性能提升。
- P2P 后端尚未冻结，按 EN-040 原型结果决定；QUIC/WebRTC 名称不代表具体库满足全部平台/API 需求。

## 任务执行与实时交接

[docs/todolist.md](docs/todolist.md) 是唯一进度台账。状态只使用 TODO、DOING、BLOCKED、DONE、DEFERRED。

- 实现工作按优先级及依赖选择任务；领取前检查活动任务与工作区冲突。
- 开始即更新任务状态、负责人、日期、当前动作；只读咨询或文档任务不得擅自启动代码实现。
- 每完成可验证子步骤、得到重要测试结果、产生阻塞或改变设计，立即同步台账，不能等到最终提交才补记。
- 切换任务、上下文交接、提交或结束回复前，更新交接快照及执行记录：已做、未做、证据、风险和下一条可执行动作。
- DONE 必须满足任务验收并附文件/测试证据；未运行、失败、环境受限、通过必须区分。
- BLOCKED 写明具体原因、证据、解除条件及能继续的独立工作；需要用户输入时明确问题。
- 不覆盖其他任务执行记录，不重用任务 ID；任务拆分、跳过、延期均保留原因和依赖变化。
- 存在实际提交才填写哈希；未提交如实记录。不要凭文档中的计划声明代码完成。
- 架构选择变化追加到 refactor-plan 的决策表；新增报告/文档同步 docs/README.md。
- 下一位 Agent 应能仅凭台账和仓库开始下一步，不依赖私有记忆、聊天上下文或临时文件。

## 代码与文档修改规则

- 保持现有代码风格，补丁小而精确；跨平台逻辑避免绑定特定 OS。
- 协议、传输、安全、配置、端口管理改动优先补充有意义的行为测试。
- 保持 AgentHost/ServerHost 宿主兼容；Web 修改遵守 SSR、Interactive Server 生命周期及认证边界。
- README 描述已经可用的能力；重构方案描述目标；todolist 描述完成状态。
- 配置项、启动方式和安全默认值实际改变时，同步 README 及相关部署/迁移文档。
- 新增或修改 docs 文档时同步维护 docs/README.md 的索引信息。
- 新增/删除/重命名项目、改变依赖、移动模块或调整宿主/配置/测试/发布入口时，同步 docs/project-index.md；明确区分现有代码与规划目录。
- 纯文档修改检查链接、任务引用、内容一致性及 git diff --check；无需因此运行 .NET/CSS 构建。

## 环境与常用验证

依赖 .NET 10 SDK、Node.js >= 20；SDK 选择见 global.json。

~~~bash
dotnet restore AeterniTunnel.slnx
dotnet test AeterniTunnel.slnx
~~~

Engine 实现任务运行相关测试，阶段交付前运行解决方案测试；按任务需要补充集成、跨网络、性能或平台检查。测试受限时在台账中记录环境和未验证范围，不能伪报通过。

Web 管理台：

~~~bash
cd Aeterni.Tunnel.Web
npm install
dotnet watch run
~~~

若修改 wwwroot/css/app.css 或前端样式/构建链，执行：

~~~bash
cd Aeterni.Tunnel.Web
npm run css:build
# 开发时可使用 npm run css:watch
~~~

dotnet watch run 不会自动构建 Tailwind CSS。管理台为 Blazor Interactive Server，不要无充分理由改为 NativeAOT。避免无关 UI 或构建链改动。
