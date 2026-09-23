# Engine 通信契约

这是 EN-010 冻结的 Engine 通信边界。接口只描述通信语义，不引入房间、用户、聊天历史、音频设备或编解码依赖。

## 契约

| 接口 | 语义 | 必须保证 |
|---|---|---|
| `IReliableStream` | 有序可靠字节流 | 背压、取消、写方向完成和连接错误可观察；不保证跨重连连续 |
| `IReliableMessageChannel` | 保留边界的可靠消息 | 最大长度、排队消息数/字节数、顺序、取消和关闭明确；不承诺 exactly-once |
| `IRealtimeDatagramChannel` | 实时数据报 | 保留包边界；允许丢失/乱序；每包有大小、有效期和排队消息数/字节数限制 |
| `IPeerSession` | 已授权对端会话 | 双方身份、能力、关闭状态可观察；不包含业务成员模型 |
| `ICommunicationAuthorizationProvider` | 通信授权回调 | 默认拒绝；授权输入绑定双方 Peer 和 serviceId |
| `ITransportFactory` | 传输创建入口 | 能力显式声明；不把不支持的数据报伪装成 Stream |

现有 `ITunnelConnection.Stream` 和 `Channel` 是兼容层。`Channel` 当前已按包数和字节数限制收发窗口，并在协商 `ConnectionIsolation` 后使用窗口更新、通道重置和同入口附加数据连接隔离可靠隧道流量；它仍不是 `IRealtimeDatagramChannel`。后续适配器可以实现其余接口，但在适配完成前不能宣称已有 UDP/P2P 数据报能力。

## 统一边界

- 所有可靠操作都接受 `CancellationToken`，取消必须释放等待和底层资源。
- 发送缓冲的所有权在调用返回前由实现消费或复制；实现不得持有调用方可变缓冲的借用引用。
- 最大帧/消息/数据报、排队包数和排队字节数必须由实现公开或配置限制。
- 半关闭只适用于可靠流；消息和数据报使用显式关闭/过期，不模拟 Stream EOF。
- 连接关闭、超时、授权拒绝、负载超限和不支持能力必须返回可分类错误，不静默重试到另一种语义。
- Peer 会话通过 `CommunicationSessionState` 暴露生命周期；消息/数据报接收在关闭时返回 `CommunicationException(Closed)`，空负载仍是合法消息，不能用它冒充 EOF。
- 业务处理通过宿主回调执行，不能阻塞底层 socket 读循环。

## 当前实现范围

当前 TCP/TLS 与通道复用仍是既有隧道实现；可靠通道已具备有界窗口、公平写入和显式超限错误，同一 ATS 接入端口可绑定受配额限制的数据连接。`IRealtimeDatagramChannel`、`IPeerSession` 和授权提供者目前仍是冻结的宿主契约，P2P 实现留到 EN-040～EN-044。EN-020 的 UDP 来源关联仍经可靠通道转发，不把它升级成 Peer 数据面。

EN-041 已在统一控制连接上加入 Peer 信令消息：`PeerRequest` 创建绑定请求方/目标/`serviceId` 的短租约，服务端通过注入的 `ICommunicationAuthorizationProvider` 对双方方向分别授权后，才转发 `PeerRequestNotice`、`PeerDescription` 和 `PeerCandidate`。租约最多 30 秒，每个 Peer 最多 8 个并发请求，每个租约最多 128 个候选；SDP 和候选长度也有限制。候选转发只是信令，不代表已经建立直连，不新增客户端必需端口。

EN-042 已先冻结直连数据面使用的安全边界：`PeerAuthorizationLease` 以随机凭证绑定双方稳定身份、服务和不超过 30 秒的有效期，支持撤销；统一信令成功时通过 `PeerRequestAck`/`PeerRequestNotice` 向双方传递同一 token；`PeerPathSelector` 明确 `PreferDirect`、`DirectOnly` 和 `RelayOnly`，DirectOnly 在直连不可用时只返回可分类失败，不隐式改走中继；`PeerDataPlaneGate` 在 native adapter 报告标准加密握手完成前拒绝业务包，并拒绝同一连接上的重复 lease 认证。DataChannel 的实际标准握手、证书/密钥绑定和收包循环接入仍待 native adapter 完成，不能把这些契约单元测试当作直连已实现。

EN-043 已先加入 `PeerRelayAdmission` 资源边界：中继会话必须通过已授权 Peer lease，队列包数、字节数和单包大小均受限，reservation 释放幂等，关闭后不接受新数据。该门禁只转发后续安全传输生成的 opaque ciphertext，不解密业务负载；实际中继传输、端到端加密和 DirectOnly 失败路径仍未完成。

EN-044 的恢复边界已冻结为：`AtsControlDisconnected` 只使 Peer 等待控制恢复并要求重新授权，不等同于 Peer 已关闭；网络切换和传输失败使用有上限的指数退避；租约过期、撤销和主动关闭直接终止 Peer，不自动重试。实际 Peer session 生命周期、宿主 API 和 native 重连接入仍未完成。

`PeerSessionLifecycle` 已提供上述状态转换、关闭幂等、`PeerQualitySnapshot` 事件和 lease 过期/撤销检查边界；它只负责宿主可观察生命周期，不宣称底层 WebRTC、流、消息或数据报已经可用。

旧共享 token 会话只产生兼容用 `ClientId`，不产生稳定 Peer 身份。宿主必须注入 `IClientIdentityResolver` 才能得到 `AuthenticatedPeerId`；后续 Peer/中继授权只允许使用该身份，并通过默认拒绝的 `ICommunicationAuthorizationProvider` 检查，不能退回自报 `ClientId`。
