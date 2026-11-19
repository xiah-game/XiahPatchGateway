# XiahPatchGateway

Xiah Oriental Fantasy 资源补丁与网关服务程序 (C# .NET)。

## 功能特性
- **资源补丁分发**：支持客户端自动更新、文件 MD5 校验及清单文件（manifest.json）自动生成与增量下载。
- **动态补丁根路径下发（`/patch_config`）**：支持在 `bin/config.ini` 的 `[Patch]` 节配置 `DownloadBaseUrl` 指向外部对象存储或 CDN 根路径，修改保存后**免重启网关即实时生效**；留空时自动回退至本机 `patch_dir/` 直接分发（接口仅返回 `baseUrl`，不透传任何客户端防盗链凭据）。
- **账号与网关路由**：提供客户端版本验证、账号查重（`/check_account`）、账号注册（`/register`，含 IP 封禁拦截）及公告分发服务。

## 目录结构
- `Program.cs`: 网关核心逻辑、HTTP 监听路由（`/patch_config`、`/register`、`/check_account`、`manifest.json`）及静态文件分发处理
- `XiahPatchGateway.csproj`: C# 工程项目文件
- `Properties/AssemblyInfo.cs`: 程序集属性
- `bin/config.ini`: 网关运行配置文件（含网络端口、数据库连接及 `[Patch] DownloadBaseUrl` 分发源配置）
- `bin/patch_dir/`: 补丁资源目录及清单
