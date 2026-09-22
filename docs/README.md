# 文档索引

本目录存放 Aeterni Tunnel 的补充文档、设计资料和可视化内容。目标为服务端 Blazor Interactive Server、客户端 Tauri + Blazor WebAssembly，后续替换 Avalonia。当前先完成 Engine 通信层重构，再迁移客户端；业务房间/语音和 UI 迁移不作为前置工作。

## 接手顺序与信息来源

1. 阅读根目录 [AGENTS.md](../AGENTS.md)，确认协作及端口约束。
2. 阅读 [todolist](./todolist.md) 的交接快照、任务状态、验收卡和最新记录，再领取工作。
3. 阅读 [重构方案](./refactor-plan.md) 的范围、模块、安全、兼容和决策。
4. 使用 [项目索引](./project-index.md) 定位当前项目、依赖、模块和测试入口。
5. 核对任务相关源码/测试与根目录 [README.md](../README.md) 的实际使用方式。

README 描述已实现功能；重构方案描述目标与约束；todolist 是唯一进度台账。用户新指令优先，并须同步文档。计划列出的 API、配置和 P2P 能力尚不能作为现成功能使用。

## 文档列表

| 文档 | 说明 |
|---|---|
| [项目索引](./project-index.md) | 当前解决方案、依赖、模块、测试/构建入口，以及现有 Avalonia 与目标 Blazor 客户端的区别 |
| [Engine 重构 todolist](./todolist.md) | 唯一进度台账：Engine 优先级/依赖/验收及证据，后续 UI 迁移与 Avalonia 退役任务 |
| [Engine 通信层重构方案](./refactor-plan.md) | 通信范围、单一接入端口、安全/性能、P2P，以及 Engine 完成后的双端 Blazor 架构 |
| [Engine 性能基线](./performance-baseline.md) | EN-001 的可重复运行方式、环境和已记录的编解码/通道/TCP/TLS 基线结果 |
| [ATS 模式可视化](./ats-modes-visual.html) | ATS 模式与流量路径辅助说明；不作为本轮实现状态或单端口约束的依据 |

## 维护规则

- 新增文档后，应在本索引中补充链接和简要说明。
- 文档中的命令、配置字段和架构说明应与 `README.md` 及实际代码保持一致。
- 修改核心协议、配置格式或运行方式时，应同步检查相关文档。
- 优先使用相对路径链接，确保文档可在仓库和 GitHub 页面中访问。
- 每个可验证步骤和交接点同步 todolist，保留未验证范围；不要在多个文档重复维护任务状态。
- 新增测试/性能报告须加入上表，并从相关任务执行记录链接。
- 项目、依赖、关键路径或启动/测试/发布入口变化时，同步 project-index.md；该文档仅导航源码，不维护第二份进度。
