# GONES.Web

GONES ERP 系统的 **Web 层**，基于 **ASP.NET Core (.NET 9) + Furion 框架 + PostgreSQL (Npgsql)** 构建，
覆盖商品基础资料、仓储收发货、生产工单与领退料、权限与菜单管理等业务模块。


## 技术栈

- ASP.NET Core 9 / Razor / Bootstrap / Tabler
- Furion 框架
- Entity Framework Core（仅作映射层，`SnakeCaseConvention` 桥接 PascalCase 实体 ↔ snake_case 存储列）
- PostgreSQL 18 + Npgsql
- 前端：ECharts（报表）、Tabler / Bootstrap（UI）

## 目录结构

```
GONES.Web/            # Web 主项目（Controllers / Views / Services / wwwroot ...）
GONES.Web.Data/       # 数据层：Entities / Infrastructure / sql(schema) / Migrations
GONES.Web.sln         # 解决方案
docs/                 # 交付说明、自检报告、开发进度梳理
```

## 本地运行

1. 准备 PostgreSQL 数据库（默认 `127.0.0.1:5432`，库名 `GONESERP`）。
2. 复制配置模板并填入本地连接串：

   ```bash
   cp GONES.Web/appsettings.example.json GONES.Web/appsettings.json
   # 编辑 GONES.Web/appsettings.json，将 YOUR_PASSWORD 改为你的数据库口令
   ```

3. 还原并运行：

   ```bash
   dotnet restore
   dotnet build
   cd GONES.Web/bin/Debug/net9.0 && dotnet GONES.Web.dll --urls http://localhost:53530
   ```

4. 浏览器打开 `http://localhost:53530`，默认管理员账号 `admin / 123`。

## 数据库结构

- `GONES.Web.Data/sql/schema.sql`：表结构 DDL（已提交）。
- 数据初始化脚本（`seed.sql`）含账号口令哈希，**未随本仓库公开**，请按需内部提供。

## 许可证

仅供学习与内部使用。
