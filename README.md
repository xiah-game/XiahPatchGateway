# XiahPatchGateway

Xiah Oriental Fantasy 资源补丁与网关服务程序 (C# .NET)。

## 功能特性
- **资源补丁分发**：支持客户端自动更新、文件 MD5 校验及清单文件（manifest.json）自动生成与增量下载。
- **账号与网关路由**：提供客户端版本验证、公告分发及服务器列表路由等服务。

## 目录结构
- `Program.cs`: 网关核心逻辑与 HTTP / Socket 监听处理
- `XiahPatchGateway.csproj`: C# 工程项目文件
- `Properties/AssemblyInfo.cs`: 程序集属性
- `bin/config.ini`: 网关运行配置文件
- `bin/patch_dir/`: 补丁资源目录及清单
