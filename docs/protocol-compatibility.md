# Engine 协议兼容与能力矩阵

本文是 EN-011 冻结的控制协议演进规则。产品版本、控制协议版本和能力位分别管理；当前帧格式仍为 v1，不因增加 JSON 可选字段升级帧版本。

## Hello 协商

- `Hello.Version` 是控制协议版本，当前仅接受 `1`；不支持的版本返回失败 `HelloAck` 后关闭连接。
- `Hello.Capabilities` 和 `HelloAck.Capabilities` 是 `ulong` 位集，缺失时按 `0` 处理。
- 服务端只回传客户端请求与服务端支持能力的交集；未知位不回显。
- 未协商能力时不得发送依赖该能力的新封装或消息。
- JSON 未知可选属性由新端忽略；未知消息 `type` 在当前 v1 仍抛出协议错误，不能作为可选扩展发送给旧端。

## 当前能力

| 能力 | 状态 | 降级行为 |
|---|---|---|
| `ReliableStream` | 现有 TCP/TLS 隧道 | v1 基础能力 |
| `ReliableMessage` | 控制 JSON 消息 | v1 基础能力 |
| `UdpSourceAssociation` | 新 ATS/ATC 支持 | 未协商时保留旧单来源 UDP 封装 |
| `ConnectionIsolation` | 新 ATS/ATC 支持 | 未协商时不发送绑定/窗口扩展，隧道继续使用控制连接及旧背压路径 |
| `RealtimeDatagram` | 契约已定义，数据面未实现 | 不协商、不发送 |
| `PeerSession` | 契约已定义，P2P 未实现 | 不协商、不发送 |

## 互操作矩阵

| 客户端 | 服务端 | 结果 |
|---|---|---|
| 旧 v1 | 新 v1 | 缺失能力按 0；TCP/控制兼容，UDP 保留旧单来源限制 |
| 新 v1 | 旧 v1 | 旧端忽略 Hello 新字段；新端把缺失的 Ack 能力按 0，不发送新封装 |
| 新 v1 | 新 v1 | 使用能力交集；当前可启用 UDP 来源关联 |
| 任意 v1 | 非 v1 | 明确拒绝，不猜测兼容、不切换帧格式 |

`UdpSourceAssociation` 的新封装为网络序 `SourceId:uint32 + Payload`。服务端按来源端点复用关联 ID，默认最多保留 1024 个来源，空闲满 2 分钟后在下一次访问时清理；Agent 为每个来源使用独立本地 UDP socket，同样限制为 1024 个，空闲满 2 分钟后由最长 30 秒一次的扫描回收。连接或通道关闭时清空全部映射。它只在双方协商后使用；不能把这个经可靠通道转发的 UDP 隧道描述为 P2P 实时数据报。

`ConnectionIsolation` 协商后，控制会话可请求 15 秒有效、一次性使用的数据连接凭证；附加连接仍连接同一个 ATS `bindPort`，其首条控制消息必须是绑定请求。凭证绑定原认证会话，过期、重放、伪造和超配额请求均拒绝。可靠通道同时启用按包数/字节数计算的接收窗口；`WindowUpdate` 帧使用大端 `Packets:int32 + Bytes:int64`，超限或不遵守窗口的通道收到 `Reset`，其他通道和控制连接继续工作。`RequestDataConnection`、`DataConnectionToken`、`BindDataConnection`、`BindDataConnectionAck`、`Reset` 和 `WindowUpdate` 均不得发送给未协商该能力的旧端。

EN-041 的 `PeerRequest`、`PeerRequestNotice`、`PeerRequestAck`、`PeerDescription`、`PeerCandidate` 和 `PeerSignalAck` 同样只走已认证的统一控制连接。服务端先确认稳定 Peer 身份、目标在线、双向 `ICommunicationAuthorizationProvider` 授权和短期租约，再转发 SDP/ICE；租约过期、目标离线、方向不匹配或候选数量/长度超限均拒绝。它们不是 `PeerSession` 能力位的实现，旧端不会收到这些新消息；真正的数据面仍归 EN-042。

可靠通道的 `Close` 表示发送方向完成。收到 `Close` 的一端读取完已排队数据后得到 EOF，但仍可发送剩余响应，直至自身也发送 `Close`；这保持 TCP 半关闭语义。
