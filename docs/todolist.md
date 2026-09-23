# Engine 重构 todolist

最后更新：2026-09-23。唯一进度台账；架构约束见 [重构方案](./refactor-plan.md)，协作规则见 [AGENTS.md](../AGENTS.md)。

## 1. 当前交接快照

- 本轮范围：EN-010、EN-011、EN-012 和 EN-020 已完成，建立通信契约、协议能力协商、身份/控制生命周期边界及 UDP 多来源关联。
- 当前活动任务：无；四项任务已通过全量测试与解决方案构建，提交已推送到远端主分支。
- 已完成代码任务：EN-000、EN-001、EN-002、EN-003、EN-010、EN-011、EN-012、EN-020。跨网络结果和 P2P 后端尚未验证。
- 下一项：EN-021 端口/监听资源事务与配额。
- 当前阻塞：无；待执行的测试与选型是任务，不记作阻塞。
- 首要约束：Engine 只提供通信；单一 ATS 客户端接入端口；隧道/P2P 数据允许其他端口。
- 已替代路线：先迁移 Tauri/WASM、统一组件库、再加 P2P。被替代的是执行顺序；Tauri + Blazor WebAssembly 已确定为客户端目标，仍排在 Engine 重构之后。
- UI 目标：服务端保留 Blazor Interactive Server；客户端迁移到 Tauri + Blazor WebAssembly，最终替换 Desktop 和 AeterniLink 中的 Avalonia 实现；现有代码尚未迁移。
- 待冻结事项：P2P 后端（EN-040）；通信契约和 v1 能力位已冻结于 EN-010/EN-011 文档，TLS 迁移规则已冻结于 EN-003 记录，EN-001 的本机筛查和相对门槛已冻结于性能报告，跨平台性能留待 EN-050。
- 接手前核对：工作区变更、任务活动状态、最近执行记录；不能把本文的“计划”当成已实现功能。

## 2. 状态与实时同步

| 状态 | 含义 |
|---|---|
| TODO | 尚未开始，或未满足依赖 |
| DOING | 已领取，正在执行；须记录负责人和当前动作 |
| BLOCKED | 存在具体外部阻塞；须写清证据、解除条件及可继续的独立工作 |
| DONE | 满足验收条件并附证据；未跑必要测试不能标记完成 |
| DEFERRED | 明确推迟，必须说明原因及恢复条件 |

依赖用任务 ID 表达；通常按 P0 → P1 → P2 → P3 → P4 执行，同优先级按依赖顺序。已确认的安全漏洞优先修复，不等待性能或 P2P 阶段。任务 ID 不重用；拆分任务时保留原 ID，并补子任务及依赖，不把旧任务直接删除。

实时同步指以下时点立即更新，而非仅在最终回复或提交时更新：

1. 开始前：改为 DOING，填写负责人/日期、范围和下一动作。
2. 完成一个可验证子步骤、得到重要测试结果、发现阻塞或作出架构决定后：更新记录和交接快照。
3. 切换任务、暂停、上下文交接、提交代码或结束一轮回复前：同步剩余工作、风险和下一条可执行命令。
4. 完成后：状态改为 DONE，附文件、测试命令/结果、相关提交（存在才填写）及未覆盖范围。

任务表是状态唯一来源；架构文档不复制进度。未测、失败、环境不具备、通过分别记录。机器本地临时日志不能作为其他 Agent 唯一证据，应在本文件摘要或仓库报告中保存关键结果。不要编造提交号或把未提交变更写成已提交。

### 执行记录模板

~~~text
日期/时区：
任务 ID / 负责人：
状态变化：
已完成：具体行为、文件路径、接口或协议变化
验证：命令、环境、结果摘要、证据路径；未执行则说明
剩余/风险：
阻塞与解除条件（如有）：
下一动作：具体文件、测试或命令
提交：实际哈希；尚未提交则写“未提交”
~~~

## 3. 优先级与任务总表

所有 Engine 代码任务初始为 TODO；后续 UI 任务为 DEFERRED。表中负责人“—”表示未领取。

| ID | 优先级 | 任务 | 依赖 | 状态 | 负责人 / 最近更新 |
|---|---|---|---|---|---|
| DOC-001 | P0 | 文档体系、范围、计划与交接规则 | — | DONE | Codex / 2026-09-21 |
| DOC-002 | P0 | 项目索引与源码导航 | DOC-001 | DONE | Codex / 2026-09-21 |
| DOC-003 | P0 | 确认双端 Blazor 与客户端迁移顺序 | DOC-002 | DONE | Codex / 2026-09-21 |
| EN-000 | P0 | 功能回归与代码安全边界基线 | DOC-001 | DONE | Codex / 2026-09-21 |
| EN-001 | P0 | 性能基线与数值验收门槛 | EN-000 | DONE | Codex / 2026-09-22 |
| EN-002 | P0 | 登录状态机与未授权资源操作修复 | EN-000 | DONE | Codex / 2026-09-21 |
| EN-003 | P0 | TLS 安全默认值与单入口兼容迁移 | EN-002 | DONE | Codex / 2026-09-22 |
| EN-010 | P1 | 通信契约及依赖边界 | EN-003 | DONE | Codex / 2026-09-23 |
| EN-011 | P1 | 版本、能力协商及扩展消息边界 | EN-010 | DONE | Codex / 2026-09-23 |
| EN-012 | P1 | 控制会话、身份授权和生命周期拆分 | EN-011 | DONE | Codex / 2026-09-23 |
| EN-020 | P1 | 隧道适配与 UDP 多来源关联 | EN-012 | DONE | Codex / 2026-09-23 |
| EN-021 | P1 | 端口/监听资源事务与配额 | EN-020 | TODO | — |
| EN-030 | P2 | 有界队列、公平调度与同端口连接隔离 | EN-001, EN-012, EN-021 | TODO | — |
| EN-031 | P2 | 帧热路径优化与性能复测 | EN-030 | TODO | — |
| EN-040 | P3 | P2P 通信后端原型与选型 | EN-010, EN-011 | TODO | — |
| EN-041 | P3 | 统一入口 Peer 信令与短期授权 | EN-012, EN-040 | TODO | — |
| EN-042 | P3 | 安全直连数据面 | EN-030, EN-041 | TODO | — |
| EN-043 | P3 | 授权中继与路径策略 | EN-042 | TODO | — |
| EN-044 | P3 | Peer 恢复、租约及宿主通信 API | EN-020, EN-043 | TODO | — |
| EN-050 | P4 | 跨平台/跨网络、安全和长稳验收 | EN-031, EN-044 | TODO | — |
| EN-051 | P4 | 部署、迁移、诊断与发布材料 | EN-050 | TODO | — |
| UI-001 | P5 | Tauri + Blazor WebAssembly 与原生 Engine 宿主接入 | EN-051, DOC-003 | DEFERRED | — |
| UI-002 | P5 | Blazor 客户端功能迁移与双端契约整理 | UI-001 | DEFERRED | — |
| UI-003 | P5 | 三平台交付与 Avalonia 退役 | UI-002 | DEFERRED | — |

EN-040 的依赖刻意不包含性能优化：若接口设计出现后端可行性风险，可以先执行有界原型，但须记录原因和恢复主线的下一动作，不提前扩展成产品实现。

## 4. 任务验收卡

### DOC-001：文档体系

- 交付：重写重构方案；建立本台账；更新 AGENTS、文档索引和根 README 入口。
- 验收：职责/端口/优先级一致，旧路线明确失效；本地链接可解析；代码任务不误标完成；差异只包含文档。
- 验证：本地链接与任务依赖检查、git diff --check、git diff --stat；本任务不执行 .NET 或 CSS 构建。

### DOC-002：项目索引

- 交付：项目职责、实际依赖、运行入口、Engine 模块、测试映射、配置/构建和任务定位索引。
- 验收：路径和依赖按现有源码核对；区分已实现结构与规划模块；同步 AGENTS、文档索引及 README 入口。
- 验证：本地链接、项目清单/引用、任务引用与依赖检查，git diff --check；不执行代码构建。

### DOC-003：双端 Blazor 目标

- 交付：服务端 Blazor Interactive Server、客户端 Tauri + Blazor WebAssembly、Engine 原生宿主边界和 Avalonia 退役顺序。
- 验收：架构、索引、AGENTS、README 与台账一致；现状与目标区分；Engine 仍优先，UI 任务明确推迟。
- 验证：文档链接、任务依赖和差异检查；本轮不创建新项目或修改 UI/Engine 代码。

### EN-000：现状基线

- 范围：Engine、Engine.Tests、宿主 API 和配置入口；梳理控制消息、隧道路径及资源生命周期。
- 执行：dotnet test AeterniTunnel.slnx；记录 SDK/OS、测试数量、失败与环境限制，不沿用 README 的历史数量。
- 验收：现有 TCP/UDP/vhost、重连、配置、端口策略的基线明确；列出未认证操作、资源泄漏和慢消费者的复现测试入口。
- 交付：在台账记录结果；如需详细报告，新建后同步 docs/README.md。不得把本轮源码阅读当作已通过测试。

### EN-001：性能基线

- 范围：FrameCodec、ChannelMultiplexer、TCP/TLS 端到端及多隧道混合负载。
- 测量：吞吐、分配字节/操作、CPU、峰值内存、控制 p50/p95/p99、连接建立耗时；覆盖小包、大流、慢消费者和不同并发量。
- 验收：可重复运行的命令/工具与硬件、SDK、构建模式、负载、预热和重复次数完整；冻结绝对/相对门槛及测量波动规则。
- 限制：未知数值标“待测”；优化前后采用相同安全模式，不能用关闭 TLS 获得性能优势。

### EN-002：认证前置

- 范围：ServerSession 控制分发、登录失败处理、请求资源分配路径。
- 验收：未 Hello、错误凭证、重复/并发 Hello、登录超时、认证前注册/注销/命令均有定义；未认证端无法消耗已授权服务资源。
- 测试：负面测试与合法流程回归；失败后端口、监听、会话及时回收。
- 约束：先做最小修复，不等待大规模目录迁移。

### EN-003：TLS 与兼容迁移

- 范围：TcpTlsTransport、AgentOptions、Config、Hosting 及必要宿主适配。
- 交付：强制安全模式、证书验证/信任配置、旧明文部署迁移说明；接入仍为一个端口。
- 验收：不可信/过期/身份不匹配证书被拒绝，无明文重试；安全旧协议路径互操作；旧配置明确迁移或拒绝，不能静默降级。
- 测试：旧/新客户端 × 旧/新服务端的实际支持矩阵；升级/回滚顺序、配置变化写入 README。
- 决策：同入口 TLS 切换及兼容部署方式写入重构方案决策表；不默认启用混合明文监听。

### EN-010：通信契约

- 交付：可靠流、可靠消息、实时数据报、PeerSession、授权提供者、传输工厂和状态/错误模型；接口名称在此冻结。
- 验收：定义长度、顺序、取消、半关闭、背压、缓冲所有权、传输能力和不支持行为；不将数据报强塞入 Stream。
- 约束：不引入 Room/User 业务模型或音频依赖；仅为实际复用建立项目。

### EN-011：协议演进

- 范围：Hello/HelloAck、MessageCodec、FrameContract、源生成上下文。
- 验收：未协商不发送新类型；可选字段与真实旧协议数据兼容；协议版本与产品版本分离。
- 测试：新旧互操作、未知可选/必需扩展、负载边界、畸形帧、半包粘包；确需新帧时测试升级与拒绝路径。
- 交付：能力矩阵与线协议约定，记录兼容范围；不能把“未知消息应忽略”当成旧端现有行为。

### EN-012：控制与生命周期

- 范围：AgentSession、ServerSession、ServerListener、AgentHost/ServerHost。
- 交付：控制会话与隧道分离、传输注入；复用在线索引并绑定认证主体；默认拒绝的通信授权接口。
- 验收：稳定 Peer 身份不可通过自报 ClientId 冒用；重复连接替换、并发取消、关闭、重连和事件订阅均有界且幂等。
- 测试：伪造身份/越权服务请求被拒绝；慢业务处理不阻塞网络读循环；旧宿主功能及健康检查保留。

### EN-020：隧道与 UDP

- 范围：TcpBridge、ProxyListener、UdpProxyListener、Agent 本地转发。
- 交付：隧道使用通信契约；UDP 使用来源关联 ID 和有界会话映射，对端本地 socket/端点正确关联。
- 验收：至少两个来源同时访问同一 UDP 隧道，响应不会错发；超时清理、同来源复用、最大会话数及重连清理明确。
- 兼容：新 UDP 封装须能力协商；旧模式的单来源限制明确保留或明确拒绝多人模式，不修改封装后假称兼容。
- 测试：TCP/UDP/HTTP/HTTPS 原有行为回归、UDP 多来源和乱序数据；可靠流半关闭。

### EN-021：端口与配额

- 范围：端口分配、vhost 注册、监听启动、注销和异常回滚。
- 验收：配额拒绝、重复注册、绑定失败、取消及断连不泄漏端口；不同 Peer 服务资源隔离。
- 测试：并发注册/释放、允许列表、超配额、故障注入；注册成功与资源提交顺序一致。

### EN-030：调度与连接隔离

- 范围：Channels、统一接入监听、数据连接绑定与限流。
- 交付：按字节与包数限制内存、公平调度、控制预算；同端口附加 TLS 数据连接和短期一次性绑定。
- 验收：慢通道不无限阻塞全部控制/数据；可靠数据不静默丢弃，实时数据可按有效期丢弃；关闭可解除所有等待。
- 测试：相同端口多连接、重放/越权/过期绑定拒绝、连接配额、监听端口清单；混合负载达到 EN-001 控制延迟门槛。
- 限制：附加连接数有上限；不声称单一 TCP 流通过优先级即可消除网络队头阻塞。

### EN-031：性能优化

- 范围：帧编解码、发送聚合、缓冲分配、数据转发热路径。
- 验收：达到 EN-001 冻结门槛；附前后测量表，注明收益/回归和负载；无内存池提前归还、取消泄漏或敏感数据复用。
- 测试：编解码边界、随机/恶意输入、并发关闭、长时间混合负载；优化每个热点后做相关回归。
- 限制：保留控制 JSON，除非数据证明替换必要并完成兼容评估。

### EN-040：P2P 原型

- 交付：一个选定后端、版本/许可证/维护状态、三平台支持与原生依赖清单；至少两个进程的加密合成数据通信及失败路径。
- 验收：候选交换、实际 UDP 映射/套接字复用、可靠消息和实时数据报语义、MTU、拥塞控制、资源释放及中继适配均有证据。
- 网络证据：区分 loopback、同 LAN 和跨 NAT，未测平台/网络明确列出；不能仅用本机互通宣告可上线。
- 决策：WebRTC 数据通道或其他安全传输由结果决定；QUIC 的运行时平台/API 实际能力需要验证。
- 约束：原型不包含房间、音频设备、编解码和 SFU；若需新增辅助端口，列明用途。

### EN-041：Peer 信令

- 范围：统一控制入口、在线会话索引、Peer 请求/响应、候选转发。
- 验收：授权绑定双方主体、服务、SessionId、有效期；双方同意和策略满足后才发候选/建会话；不新增独立信令端口。
- 测试：越权、过期/撤销凭证、重复请求、目标离线、并发连接、候选洪泛与会话超时。
- 安全：候选探测范围/频率受控，不提供任意内网扫描和开放中继。

### EN-042：安全直连

- 范围：选定传输适配、候选检查、Peer 加密握手、数据通道。
- 验收：对端密钥/证书绑定授权身份；业务包未经认证不投递；跨 NAT 直连时 ATS 仅有信令和保活流量。
- 测试：IPv4/IPv6、同 LAN、至少一种跨 NAT 环境、不可直连环境、伪造/重放包、丢包乱序与 MTU 边界。
- 证据：记录选中路径、实际流量和失败原因；不承诺固定直连成功率。

### EN-043：中继与策略

- 交付：PreferDirect / DirectOnly / RelayOnly；授权加密中继与按 Peer 对选择路径。
- 验收：DirectOnly 不使用中继；拒绝授权不触发绕过；中继看不到 Peer 明文；资源、会话和带宽有限制。
- 测试：UDP 禁用、不可打洞、直连断开、中继不可用和凭证过期；可靠性/实时语义退化明确上报。
- 部署：辅助端口/范围经统一入口告知，关闭 P2P 时不影响基础 ATS 接入；TURN 或自建中继的选择记录依据。

### EN-044：恢复与宿主 API

- 交付：OpenPeer/流/消息/数据报等通信 API、质量快照及事件；名称以最终契约为准。
- 验收：ATS 掉线与 Peer 掉线分离；授权租约到期关闭，撤销有明确生效上界；网络切换重试有上限。
- 测试：恢复/关闭竞态、订阅释放、配置热更新；可靠通道无法续接时明确报错，不伪装无损迁移。
- 示例：无 UI 的宿主合成负载示例，不加入房间、聊天历史或媒体业务。

### EN-050：系统验收

- 矩阵：Windows/Linux/macOS，旧/新端兼容，LAN/跨 NAT/IPv6/禁 UDP，延迟/丢包/乱序/重绑定。
- 安全：身份伪造、认证前操作、凭证重放/撤销、候选滥用、解析模糊测试、资源耗尽和日志脱敏。
- 长稳：连接频繁建立/释放、持续传输、控制链路断开、慢消费者；时长与门槛执行前冻结并记录。
- 验收：dotnet test AeterniTunnel.slnx 及对应集成/网络/基准检查通过；限制、失败和未测项明确，不以单机单测替代跨网络测试。

### EN-051：交付材料

- 交付：README 实际配置、单端口接入及业务端口清单、安全迁移、凭证轮换、P2P 限制、诊断和回滚步骤。
- 验收：说明与实际可执行配置一致；新 docs 报告均进入索引；无把计划能力写成现成功能。
- 回滚：验证配置备份/恢复与旧版兼容边界；不能以重新启用不安全默认值作为无提示回滚。
- 完成条件：全部前置任务验收完成，有可追溯证据；后续业务功能另立计划。

## 5. Engine 完成后的 UI 任务

以下任务采用 DEFERRED，原因是用户要求先完成 Engine。恢复条件为 EN-051 验收完成并进入后续客户端迁移工作；本轮不执行。P5 不改变 Engine 的 P0～P4 顺序。

### UI-001：客户端框架与原生宿主

- 交付：Tauri 桌面壳、Blazor WebAssembly UI、托管 Engine 的本地 .NET 宿主及受控通信桥接；项目名在实施时冻结。
- 验收：WASM 负责 UI，Engine 在原生宿主运行；Tauri 管理宿主启停、退出和异常恢复；桥接覆盖请求、结果、事件、取消与错误，不直接暴露任意执行能力。
- 端口：本地进程通信不新增 ATS 登录/信令端口；如需本地监听，限定本机并验证访问身份，不能成为公网入口。
- 验证：Windows/Linux/macOS 的最小 UI → 宿主 → Engine → ATS 链路；本地桥接版本兼容与进程清理。

### UI-002：功能迁移

- 交付：把当前 ATC 连接配置、隧道管理、健康/连接状态、日志和流量展示迁移为 Blazor 组件；服务端继续使用现有 Blazor Interactive Server。
- 验收：通过已完成的 Engine 宿主契约实现功能，不在 Tauri/Rust 或 WASM 中复制隧道协议；本地配置迁移有备份与兼容说明。
- 共享边界：仅提取实际共用的 DTO、组件或样式；不让服务端认证/配置实现与客户端生命周期互相依赖。
- 验证：客户端功能对照、桥接事件/重连、配置迁移和服务端现有流程回归；不扩展房间/语音产品范围。

### UI-003：交付与 Avalonia 退役

- 交付：Tauri + Blazor WebAssembly + 原生 .NET 宿主的三平台打包/发布和启动说明；迁移后的项目索引与依赖图。
- 验收：替代客户端满足原 ATC 功能与交付要求后，移除或归档 Desktop/AeterniLink 的 Avalonia 工程，清理解决方案引用、依赖及旧发布作业；最终产品不再使用 Avalonia。
- 验证：安装、启动、退出、升级、配置保留及回滚；Web 仍保持 Blazor Interactive Server，不因客户端变更改为 WASM 服务端或 NativeAOT 管理台。
- 约束：替代客户端尚未验证前不删除旧客户端；过渡期间只做必要兼容与缺陷修复，不新增 Avalonia 产品能力。

## 6. 执行记录

### 2026-09-23 / Asia/Shanghai — EN-010、EN-011、EN-012、EN-020 / Codex

- 状态：四项任务依次由 TODO → DOING → DONE。
- EN-010：冻结可靠流、可靠消息、实时数据报、PeerSession、默认拒绝授权、传输能力、状态和分类错误契约；现有 `ITunnelTransport` 接入 `ITransportFactory`。
- EN-011：Hello/HelloAck 增加默认值为 0 的可选能力位，服务端只返回支持交集并拒绝非 v1；冻结旧/新端兼容矩阵、未知扩展规则和 UDP 来源封装。
- EN-012：Agent/Server 均支持传输工厂注入；宿主可注入稳定身份解析器，只有解析成功才产生 `AuthenticatedPeerId`；控制处理使用有界队列，控制后的首个数据帧在通道接受前有界暂存。
- EN-020：UDP 协商后使用来源 ID，服务端来源表与 Agent 独立 socket 均限制为 1024 个并支持空闲回收；双来源乱序响应保持正确关联；未协商时保留旧单来源封装；可靠通道 `Close` 改为方向性 EOF 并覆盖半关闭响应。
- 验证：四项定向测试 39/39 通过，UDP/通道收尾定向测试 11/11 通过；`dotnet test AeterniTunnel.slnx --no-restore --nologo --verbosity minimal` 最终为 116/116 通过；安装锁定的 Web npm 依赖后，`dotnet build AeterniTunnel.slnx --no-restore --nologo --verbosity minimal -p:UseSharedCompilation=false` 成功，0 错误及 6 条现有 Avalonia 警告；`git diff --check` 通过。
- 兼容与限制：旧 v1 端缺少能力字段时按 0 处理，UDP 保留最近来源模式；共享 token 只提供兼容 `ClientId`，不能用于后续 Peer 授权；实时数据报和 P2P 数据面仍未实现，归 EN-040～EN-044。
- 下一动作：执行 EN-021，处理端口/监听资源事务、故障回滚与配额。
- 提交：fd3412a（refactor(engine): establish communication and UDP contracts）；8ff9295（docs: record completed engine contract phases）。两个远端分支已核验同步到 8ff9295。

### 2026-09-22 / Asia/Shanghai — EN-003 / Codex

- 状态：TODO → DOING → DONE。
- 目标：将生产配置的客户端接入切换为 TLS 安全默认值，要求 ATS 配置证书后才启动安全监听；明文仅作为显式测试/迁移选项，不在失败时静默降级；保持同一 `bindPort` 单入口。
- 已完成：`AgentOptions`、Agent/Server 配置默认启用 TLS；`ServerHost` 在 TLS 缺证书或明文未显式允许时拒绝启动；支持 PFX 路径/密码、TLS server name 和自定义根证书；Desktop 写配置始终落盘 `useTls`，避免旧字段无法表达迁移选择；Web/配置相对证书路径按 `server.toml` 目录解析。
- 验证：新增 TLS 策略测试 3/3 通过；Engine 全量测试 96/96 通过；Engine.Tests 构建通过（现有 5 个分析器警告）；Desktop 使用 `-p:UseSharedCompilation=false` 构建通过（现有 6 个 Avalonia 警告）。Web 构建未完成，因环境缺少 `node_modules/.bin/tailwindcss`，未修改 CSS。
- 已补充：自定义根证书成功握手、证书不受信拒绝、server name 不匹配拒绝；默认 TLS 缺证书拒绝启动；明文必须显式 `AllowInsecureTransport`；配置和同一端口迁移规则写入 README/重构方案。
- 当前限制：Web 设置页尚未提供证书路径/密码表单，需手动维护 server.toml；服务端 TLS 证书密码仍由部署配置提供，后续应迁移到受保护的环境/密钥注入；跨平台和真实网络互操作归 EN-050。
- 下一步：进入 EN-010，冻结通信契约及依赖边界；保留 EN-003 的安全矩阵作为后续回归入口。
- 提交：946f656（test(engine): verify TLS trust and migration matrix）；实现提交 2ba4128。

### 2026-09-22 / Asia/Shanghai — EN-001 / Codex（第二阶段）

- 状态：DOING → DONE。
- 已完成：扩展基准覆盖 128 个 64 KiB 消息的慢消费者场景、1/4/8 条 TCP/TLS 连接、进程总 CPU 时间以及 10 ms 采样的峰值工作集与托管堆；在 [performance-baseline.md](./performance-baseline.md) 记录三轮原始摘要，冻结本机绝对筛查、同机 A/B 相对门槛和波动重测规则。
- 验证：`dotnet build Aeterni.Tunnel.Engine.Benchmarks/Aeterni.Tunnel.Engine.Benchmarks.csproj -c Release --no-restore` 通过（0 警告/错误）；完整 Release 基准连续 3 次成功，慢消费者写入在排空前均未完成；`dotnet test AeterniTunnel.slnx --no-restore` 为 91/91 通过；`git diff --check` 通过。
- 限制：本机回环数值不能外推到其他硬件或公网；控制帧只含编解码回显，CPU/内存采样含基准进程自身开销。EN-031 同机比较复测；跨平台、跨 NAT 归 EN-050。
- 下一动作：EN-003 先核对配置和 TLS 现状，定义旧明文部署到单入口安全模式的兼容迁移方案，再实现安全默认值和证书校验测试。
- 提交：cc0ac3d（perf(engine): complete baseline sampling and regression gates）；初始基线提交 4ba9887。

### 2026-09-22 / Asia/Shanghai — EN-001 / Codex（第一阶段）

- 状态：TODO → DOING（基线入口与第一轮结果已完成，验收未完成）。
- 已完成：新增 `Aeterni.Tunnel.Engine.Benchmarks` 控制台项目并加入解决方案；覆盖 FrameCodec 64 B/1 KiB/64 KiB、ChannelMultiplexer 1 KiB、控制帧 p50/p95/p99、TCP/TLS 单连接和 4 连接回显。没有修改 Engine 通信热路径。
- 验证：`dotnet build Aeterni.Tunnel.Engine.Benchmarks/Aeterni.Tunnel.Engine.Benchmarks.csproj -c Release --no-restore` 通过；完整 Release 命令连续运行 2 次，均完成；`dotnet test Aeterni.Tunnel.Engine.Tests/Aeterni.Tunnel.Engine.Tests.csproj --no-build --no-restore` 为 91/91 通过；环境、负载、两次结果和限制记录于 [performance-baseline.md](./performance-baseline.md)。沙箱内绑定回环端口会收到 Permission denied，完整基线在授权环境运行。
- 已知限制：当前只测同机回环；FrameCodec 分配为单线程近似；尚未统一采集进程 CPU、峰值托管内存、慢消费者和更多并发档位，也尚未冻结绝对/相对门槛。
- 下一步：补齐 CPU/峰值内存与慢消费者采样规则，增加并发档位后复测；达到 EN-001 验收条件再标记 DONE，并把稳定范围交给 EN-031 优化复测使用。
- 提交：4ba9887（perf(engine): add reproducible performance baseline）。

### 2026-09-21 / Asia/Shanghai — EN-000 / Codex

- 状态：TODO → DOING → DONE。
- 范围：运行解决方案回归，核对控制消息、认证、端口/监听资源、慢消费者和现有宿主边界。
- 环境：macOS Darwin osx-arm64；.NET SDK 10.0.300、运行时 10.0.8；global.json 请求 10.0.100 并允许 latestFeature。
- 已完成：修正 ControlPlaneTests 在发送注册请求后才订阅回执的测试竞态；梳理控制面、隧道路径与宿主边界。
- 已发现：ServerSession 未统一检查登录状态，RegisterProxy/UnregisterProxy/CommandAck/Heartbeat 可在 Hello 前处理；错误 token 后服务端未立即关闭；重复 Hello 无明确规则。HandleRegisterAsync 在端口分配后发生配额/监听异常时可能未回滚，归 EN-021。ChannelMultiplexer 对满 64 包通道队列的等待会阻塞全局读循环，已有 AdvancedTests.Channel_Backpressure_BlocksWriterUntilConsumed 入口，归 EN-030。
- 验证：首次完整测试为 87/88，通过之外唯一失败是 ControlPlaneTests 注册回执订阅竞态；调整测试时序后，控制面筛选测试 3/3 通过；随后 dotnet test AeterniTunnel.slnx --no-restore 为 88/88 通过，耗时 31 秒。测试在沙箱外运行，因为 MSBuild 命名管道和真实 socket 在沙箱内被拒绝。
- 剩余：性能基线归 EN-001；认证缺口归 EN-002；资源事务归 EN-021；慢消费者隔离归 EN-030。
- 下一动作：EN-002 增加原始协议负面测试，确保未登录消息不分配端口，错误/重复 Hello 关闭会话。
- 提交：81a4655（refactor(engine): establish authenticated session baseline）。

### 2026-09-21 / Asia/Shanghai — EN-002 / Codex

- 状态：TODO → DOING → DONE。
- 已完成：ServerSession 只允许未认证连接发送 Hello；认证前注册/注销/命令回执/心跳返回 Error 401 后关闭连接；错误 token、空 clientId 和重复 Hello 返回失败并关闭；成功 Hello 后才设置认证状态、登记在线会话和下发端口策略。
- 测试：ControlPlaneTests 使用原始控制帧验证未认证消息不会分配/绑定隧道端口，并验证错误 token、空 clientId、重复 Hello 的拒绝及服务端会话清理；同时修正两个既有注册测试的事件订阅竞态。
- 验证：控制面筛选测试 6/6 通过；dotnet test AeterniTunnel.slnx --no-restore 为 91/91 通过，耗时 32 秒。测试在沙箱外运行，因为需要 MSBuild 命名管道和真实本地 socket。
- 兼容影响：合法客户端协议不变；任何在 Hello 成功前发送其他控制消息、发送错误/空身份 Hello 或在同一连接重复 Hello 的客户端现在会被明确拒绝并断开。
- 剩余：稳定设备身份及授权提供者归 EN-012；TLS、凭证和安全迁移归 EN-003；未开始性能测量。
- 下一动作：领取 EN-001，建立可复现的 FrameCodec/ChannelMultiplexer/TCP-TLS 基线和冻结门槛。
- 提交：81a4655（refactor(engine): establish authenticated session baseline）。

### 2026-09-21 / Asia/Shanghai — DOC-001 / Codex

- 状态：DOING → DONE。
- 已完成：核对现有接入、传输、帧/消息和 UDP 路径；重写 [refactor-plan.md](./refactor-plan.md)，建立本台账及 18 项 Engine 任务；同步 [AGENTS.md](../AGENTS.md)、[文档索引](./README.md) 和 [根 README](../README.md)。
- 验证：Python 文档一致性检查通过（5 个文档、本地链接均存在、19 个唯一任务均有验收卡、任务引用有效、依赖无环、18 项 Engine 任务均为 TODO）；git diff --check 通过；git status --short 确认只有上述文档变更。
- 未执行：.NET 测试、性能基准、跨网络验证和 CSS 构建；本轮仅文档，未改变运行代码。
- 剩余：DOC-001 无剩余；实现、数值门槛与 P2P 选型均留待对应任务，不能据此标记代码完成。
- 下一动作：收到实现任务后，先核对 git status --short 和本台账，再将 EN-000 标为 DOING，运行 dotnet test AeterniTunnel.slnx，并记录环境与结果；阅读 ServerSession 和相关认证/控制面测试确定负面测试入口。
- 提交：81a4655（refactor(engine): establish authenticated session baseline）。

### 2026-09-21 / Asia/Shanghai — DOC-002 / Codex

- 状态：DOING → DONE。
- 已完成：新增 docs/project-index.md，覆盖 6 个项目、直接依赖、运行链路、Engine 模块、测试、配置/构建/发布及任务定位；同步 AGENTS、docs/README.md 和根 README 的导航与索引维护规则。
- 核对结果：Desktop 已引用 Engine，AeterniLink 尚无项目引用；现有模块和规划目录分别说明；配置文件位置按宿主代码定位，发布目标按工作流记录。
- 验证：Python 检查通过（6 个文档、151 个本地链接、6 个项目及其引用/目标框架一致、20 个任务验收卡与依赖无环、18 项 Engine 任务仍为 TODO）；git diff --check 通过，变更仅涉及文档。
- 未执行：.NET/CSS 构建及运行测试；本轮只添加源码导航，未修改代码。
- 剩余：DOC-002 无剩余。
- 下一动作：后续实现从 EN-000 开始，按项目索引定位源码与测试，并在任务开始时同步台账。
- 提交：81a4655（refactor(engine): establish authenticated session baseline）。

### 2026-09-21 / Asia/Shanghai — DOC-003 / Codex

- 状态：DOING → DONE。
- 已完成：统一服务端 Blazor Interactive Server、客户端 Tauri + Blazor WebAssembly 目标；记录原生 .NET Engine 宿主与本地桥接边界，增加 DEC-006 和 Engine 完成后再执行的 UI-001～UI-003；同步架构方案、项目索引、AGENTS、README 和文档导航。
- 验证：Python 文档检查通过（6 个文档、151 个本地链接、24 个任务验收卡、依赖无环、18 项 Engine 任务仍为 TODO、3 项 UI 任务为 DEFERRED）；git diff --check 通过；仅文档变更。
- 未执行：.NET/CSS 构建、UI/Engine 实现；现有 Avalonia 项目与发布入口未变更。
- 剩余：DOC-003 无剩余；客户端迁移与 Avalonia 退役待 Engine 完成后执行。
- 下一动作：后续实现主线从 EN-000 开始，领取任务后同步台账并建立回归基线。
- 提交：81a4655（refactor(engine): establish authenticated session baseline）。
