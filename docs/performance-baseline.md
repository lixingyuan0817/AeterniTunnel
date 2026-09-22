# Engine 性能基线（EN-001）

本文记录 EN-001 第一阶段建立的可重复测量入口和本机结果。数值用于后续优化前后对比，尚未冻结为跨设备发布门槛；任务状态仍以 [todolist](./todolist.md) 为准。

## 运行方式

在仓库根目录执行：

```bash
dotnet restore Aeterni.Tunnel.Engine.Benchmarks/Aeterni.Tunnel.Engine.Benchmarks.csproj
dotnet run --project Aeterni.Tunnel.Engine.Benchmarks/Aeterni.Tunnel.Engine.Benchmarks.csproj -c Release --no-restore
```

快速冒烟使用 `--quick`，会减少通道、控制帧和传输样本，但仍覆盖所有场景：

```bash
dotnet run --project Aeterni.Tunnel.Engine.Benchmarks/Aeterni.Tunnel.Engine.Benchmarks.csproj -c Release --no-restore -- --quick
```

程序输出为逗号分隔的 `key=value` 行，便于其他 Agent 保存或转换为表格。TCP/TLS 测量绑定回环地址并使用自签名证书；客户端仅在基准程序中关闭证书信任校验，TLS 协议仍启用 TLS 1.2/1.3。这个例外不能复制到生产配置。

## 环境与负载

- 运行日期：2026-09-22（Asia/Shanghai）。
- 系统：macOS 27.0.0，Darwin，Arm64，8 个逻辑处理器。
- SDK：.NET SDK 10.0.300（global.json 允许 10.0.100 的 latestFeature）；运行时 .NET 10.0.8。
- 构建：Release；Server GC 为 false；无外部 BenchmarkDotNet 依赖。
- 预热：各场景 3 次。帧编解码重复 5 轮；控制帧 500 个样本；ChannelMultiplexer 发送 5,000 个 1 KiB 消息；单连接传输发送 500 个 4 KiB 回显消息；并发传输使用 4 条连接，每条发送 250 个 1 KiB 回显消息。
- 重复：完整命令连续执行 2 次；以下表格保留两次结果的范围。系统负载、CPU 调频、套接字缓冲和本机安全策略会影响结果。

## 两次 Release 结果

| 场景 | 结果指标 | 第一次 | 第二次 | 单位 |
|---|---:|---:|---:|---|
| FrameCodec，64 B | 吞吐 | 3,135,661 | 3,078,192 | ops/s |
| FrameCodec，64 B | 分配 | 376.0 | 376.0 | B/op |
| FrameCodec，1 KiB | 吞吐 | 2,611,416 | 3,411,968 | ops/s |
| FrameCodec，1 KiB | 分配 | 2,296.0 | 2,296.0 | B/op |
| FrameCodec，64 KiB | 吞吐 | 152,242 | 209,428 | ops/s |
| FrameCodec，64 KiB | 分配 | 131,320.0 | 131,320.0 | B/op |
| ChannelMultiplexer，1 KiB | 吞吐 | 246.73 | 330.98 | MiB/s |
| 控制帧往返，TCP | p50 / p95 / p99 | 51.9 / 81.1 / 111.5 | 54.2 / 65.9 / 92.2 | µs |
| 控制帧往返，TCP+TLS | p50 / p95 / p99 | 53.2 / 75.8 / 113.2 | 57.9 / 83.6 / 117.0 | µs |
| 单连接回显，TCP，4 KiB | 建连 / 吞吐 | 0.5 / 109.99 | 0.6 / 81.69 | ms / MiB/s |
| 单连接回显，TCP+TLS，4 KiB | 建连 / 吞吐 | 40.2 / 63.03 | 31.5 / 57.66 | ms / MiB/s |
| 4 连接回显，TCP，1 KiB | 建连 / 吞吐 | 1.0 / 66.73 | 1.3 / 50.84 | ms / MiB/s |
| 4 连接回显，TCP+TLS，1 KiB | 建连 / 吞吐 | 77.0 / 38.01 | 118.4 / 32.42 | ms / MiB/s |

FrameCodec 的分配值来自同一线程上的 `GC.GetAllocatedBytesForCurrentThread()`；并发网络场景没有把线程分配量冒充为操作分配值。所有传输均为本机回环，不能推断公网、NAT 或跨平台结果。

## 覆盖范围与剩余工作

当前入口已覆盖小包、大包、通道吞吐、控制帧 p50/p95/p99、TCP/TLS 建连、单连接和 4 连接混合负载。慢消费者行为仍由现有 [背压测试](../Aeterni.Tunnel.Engine.Tests/AdvancedTests.cs) 验证，没有在基准中加入会改变队列状态的长时间场景。

EN-001 仍保持 `DOING`，原因是绝对/相对门槛尚未冻结，且本入口下一步还要补充进程 CPU 时间、峰值托管内存、慢消费者和更多并发档位的统一采样规则。完成这些测量并在相同安全模式下复测后，才能把表格中的范围转成 EN-031 的优化验收门槛。
