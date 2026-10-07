# 生产单维护模块（PmsProduceOrder）交付说明

> 迁移自旧 WinForms `Production.FrmProductionOrderManager`（菜单 ID=1287）
> Web 路由：`/PmsProduceOrder`　技术栈：.NET 9 + ASP.NET Core MVC + Furion + Tabler

## 一、功能定位
生产链核心单据：**要货计划单 → 生产单 → 领料 → 入库**。
本模块负责「生产单」的建单、编辑、审核/反审核、删除，以及按 BOM 自动展开工序与原材料明细。

## 二、数据模型（4 张表）
| 表 | 角色 | 录入方式 |
|---|---|---|
| `t_PMS_ProduceOrder` | 表头（单号/部门/申请人/来源要货单/状态/备注） | 手工 |
| `t_PMS_ProduceOrderProductEntry` | 产出品明细 | 手工（搜索商品 / 引入要货单） |
| `t_PMS_ProduceOrderStepEntry` | 工序明细 | 保存时由 BOM 自动展开 |
| `t_PMS_ProduceOrderRawMaterialEntry` | 原材料明细 | 保存时由 BOM 自动展开 |

> 旧 `t_PMS_WorkShop` / `FWorkShopID` 已弃用，`FDeptID` 直接指向 `t_ERP_Department`。

## 三、业务规则（新世界，用户 2026-09-06 决策）
- **单号**：`SCD` + 6 位流水 = 当前最大单号 + 1；**FInterID** = 当前最大 ID + 1。
- **产品必须有 BOM**：`t_PMS_StepProductBom` 中须有该商品的输出行（`FIsProduct=1`），否则保存被拒（与要货计划单同规则）。
- **BOM 自动展开**：保存时按 BOM 生成工序行与原材料行，无需手工录入；表单实时预览。
- **不再生成领料单**：`FBillUseFInterID=null`，各状态字段写 0。
- **状态机**：
  - `FStatus`：0 未审核 / 1 已审核
  - `FOrderStatus`：0 未处理 / 1 已领料 / 2 已入库
  - 审核后仅可**反审核**（需 `FOrderStatus=0`）；已领料/入库禁止反审核；已审核禁止编辑/删除。

## 四、页面
- **列表** `/PmsProduceOrder`：按 单据日期 / 单号 / 审核状态 / 生产状态 查询 + 分页；操作列按状态显示 编辑 / 审核 / 删除 / 反审核 / 查看。
- **表单** `/PmsProduceOrder/Create`（或 `/Edit/{id}`）：基本信息 + 产品明细（商品搜索、引入要货计划单）；下方实时预览 BOM 展开的工序与原材料。

## 五、E2E 验证结果（本机 admin/123，localhost:5000）
| 步骤 | 结果 |
|---|---|
| 编译 | 0 错误 |
| 列表页 GET | HTTP 200 |
| 建单（商品3384，BOM=1，部门3） | 落库 FInterID=3852 / SCD003852 / FStatus=0；BOM 展开 **1 工序 + 2 原材料** |
| 审核 | HTTP 302 → FStatus=1 |
| 审核态删除 | 被拒（业务规则生效，反审核成功反证单未被删） |
| 反审核 | HTTP 302 → FStatus=0 |
| 删除 | HTTP 302 → 表头 + 三张明细**全部清空（0 行）** |

测试数据已彻底清理，未触碰任何真实业务数据。

## 六、启动与访问
```bash
cd E:\ghcode\8.HaoWorkbuddy\NewZSERP\GONES.Web
dotnet run --project "GONES.Web\GONES.Web\GONES.Web.csproj"
# 浏览器打开 http://localhost:5000/PmsProduceOrder  （账号 admin / 123）
```

## 七、交付文件
- `GONES.Web/Models/ProduceOrderViewModels.cs`
- `GONES.Web/Controllers/PmsProduceOrderController.cs`
- `GONES.Web/Views/PmsProduceOrder/Index.cshtml`
- `GONES.Web/Views/PmsProduceOrder/Form.cshtml`
- `GONES.Web/Services/MenuService.cs`（已加路由映射：菜单 1287 → `/PmsProduceOrder`）
