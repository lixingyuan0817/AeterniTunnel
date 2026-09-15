# AGENTS.md

## 项目概览

Aeterni Tunnel 是一个轻量级内网穿透工具，包含以下角色：

- ATS：公网服务端，负责端口监听、vhost 域名路由、多客户端管理、隧道管理和流量统计
- ATC：内网客户端，负责连接 ATS、注册隧道、健康检查和自动重连
- Web 管理台：基于 Blazor Web App 的管理界面，内嵌在服务端进程中

本仓库为 .NET 10 的跨平台 C# 项目，使用单仓库多项目结构：

- Aeterni.Tunnel.Engine：核心引擎，包含协议、传输、服务端/客户端实现、配置和宿主 API
- Aeterni.Tunnel.Web：Blazor Web 管理台，包含 Tailwind CSS 与前端交互
- Aeterni.Tunnel.Desktop：桌面客户端（Avalonia）
- Aeterni.Tunnel.Common：通用组件
- Aeterni.Tunnel.Engine.Tests：xUnit 测试项目

## 关键约束

- 运行环境要求：.NET 10 SDK，Node.js >= 20
- 解决方案入口：AeterniTunnel.slnx
- Web 管理台入口：Aeterni.Tunnel.Web
- 若修改 `Aeterni.Tunnel.Web/wwwroot/css/app.css`，需同步执行 Tailwind 构建：
  - `npm run css:build`
  - 或开发时 `npm run css:watch`
- `dotnet watch run` 只会重编译 .NET 代码，不会自动生成 Tailwind CSS
- 本项目使用 Blazor Interactive Server，NativeAOT 不适用于管理台场景；不要将 Web 端改成 AOT 方案而无充分理由

## 常用命令

### 安装与还原

```bash
dotnet restore AeterniTunnel.slnx
cd Aeterni.Tunnel.Web
npm install
```

### 测试

```bash
dotnet test AeterniTunnel.slnx
```

### 运行 Web 管理台

```bash
cd Aeterni.Tunnel.Web
dotnet watch run
```

### Tailwind 编译

```bash
cd Aeterni.Tunnel.Web
npm run css:build
npm run css:watch
```

## 代码修改建议

- 优先修改最相关的项目和文件，保持补丁小而精确
- 引擎层改动应尽量保持跨平台兼容性，避免依赖特定 OS 行为
- Web 端改动要注意与 Blazor/SSR/Interactive Server 的生命周期和认证逻辑兼容
- 配置项和启动参数变更应同步更新 README 中的说明与相关文档
- 对于协议、传输、配置和端口管理等核心逻辑，优先增加/更新单元测试

## 验证要求

在提交变更前，建议执行：

```bash
dotnet test AeterniTunnel.slnx
```

如果修改了 Web 前端样式或构建链：

```bash
cd Aeterni.Tunnel.Web
npm run css:build
```

## 设计原则

- 使用现有代码风格与命名习惯，不要引入无关重构
- 保持简洁、可维护、可测试，优先稳妥改动
- 对共享协议和配置格式的变更要特别谨慎，必须评估兼容性
- 新增功能应优先符合项目现有的 ATS/ATC/管理台架构，不要破坏现有运行模型

## 额外说明

- README.md 是项目的核心文档，变更功能/启动方式时优先更新 README
- `docs/README.md` 是补充文档索引；处理文档、架构、协议、配置或运行方式相关任务时，应先读取该索引，并按需读取其中列出的文档
- 新增或修改 `docs/` 下的文档时，必须同步维护 `docs/README.md` 中的索引
- 若需要处理安全相关配置（如 token、webToken、端口允许列表），注意不要在代码中硬编码敏感信息
- 本仓库是实际工程项目，优先遵循现有实现而不是引入新框架或大规模重写
