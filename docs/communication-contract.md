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

现有 `ITunnelConnection.Stream` 和 `Channel` 是兼容层。后续适配器可以实现这些接口，但在适配完成前不能宣称已有 UDP/P2P 数据报能力。

## 统一边界

- 所有可靠操作都接受 `CancellationToken`，取消必须释放等待和底层资源。
- 发送缓冲的所有权在调用返回前由实现消费或复制；实现不得持有调用方可变缓冲的借用引用。
- 最大帧/消息/数据报、排队包数和排队字节数必须由实现公开或配置限制。
- 半关闭只适用于可靠流；消息和数据报使用显式关闭/过期，不模拟 Stream EOF。
- 连接关闭、超时、授权拒绝、负载超限和不支持能力必须返回可分类错误，不静默重试到另一种语义。
- Peer 会话通过 `CommunicationSessionState` 暴露生命周期；消息/数据报接收在关闭时返回 `CommunicationException(Closed)`，空负载仍是合法消息，不能用它冒充 EOF。
- 业务处理通过宿主回调执行，不能阻塞底层 socket 读循环。

## 当前实现范围

当前 TCP/TLS 与通道复用仍是既有隧道实现；`IRealtimeDatagramChannel`、`IPeerSession` 和授权提供者目前是冻结的宿主契约，P2P 实现留到 EN-040～EN-044。EN-020 只修复现有 UDP 隧道的来源关联，不把它升级成 Peer 数据面。

旧共享 token 会话只产生兼容用 `ClientId`，不产生稳定 Peer 身份。宿主必须注入 `IClientIdentityResolver` 才能得到 `AuthenticatedPeerId`；后续 Peer/中继授权只允许使用该身份，并通过默认拒绝的 `ICommunicationAuthorizationProvider` 检查，不能退回自报 `ClientId`。
