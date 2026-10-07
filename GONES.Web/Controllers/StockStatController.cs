using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;
using GONES.Web.Services;
using Microsoft.EntityFrameworkCore;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// 报表管理 - 生产进销存情况统计（旧 WinForms「生产进销存情况统计」报表的 Web 化）。
    /// 按单据开始/结束时间、仓库名称、商品关键字、产品分类检索对应商品（按产品分类分组）的进销存情况。
    /// 列结构对齐旧报表：期初数量 / 结存数量，以及按单据类型拆分的收发：
    ///   采购入库(FB=0) / 生产退料(FB=3) / 生产入库(FB=1) / 生产领料(FB=8) / 换货入库(FB=6) /
    ///   部门领料(FB=9) / 部门退料(FB=2) / 盘点数量(盘盈FB=7-盘亏FB=10) / 产品报损(FB=5)。
    /// 数量口径与 StockService 一致：(FNum+Ext1+Ext2+Ext3) 整体按 FROB 定向；仅统计已审核单(FState""=true)。
    /// 菜单：t_ERP_Menu 下挂于「生产 - 报表管理」(ParentID=1281)，ClassName=Report.FrmPmsInOutStat → /StockStat。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 标准 MVC 表单重显，跳过 Furion 400 短路
    public class StockStatController : Controller, IMenuGuarded
    {
        // t_ERP_Menu「生产进销存情况统计」(ParentID=1281 生产-报表管理)

        private readonly GonesPgDbContext _db;
        private readonly MenuService _menu;
        public StockStatController(GonesPgDbContext db, MenuService menu)
        {
            _db = db;
            _menu = menu;
        }

        /// <summary>
        /// 入口闸门：admin 旁路恒 true（行为逐字节不变）；非 admin 须由其角色的
        /// t_ERP_RoleMenu 授权覆盖本报表（1346），否则回首页 —— 与 7 个单据模块同一范式。
        /// 注：报表是纯查询页，按既有裁定【允许跨仓查询】，故只做入口闸门、不收窄仓库维度。
        /// </summary>

        // GET /StockStat?warehouseId=&dateFrom=&dateTo=&keyword=&category=&page=&pageSize=
        public IActionResult Index(int? warehouseId, DateTime? dateFrom, DateTime? dateTo,
            string keyword, string category, int page = 1, int pageSize = 20)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);
            keyword = (keyword ?? "").Trim();
            category = (category ?? "").Trim();

            // 开始时间缺省 → 整个历史都算「本期」（期初为 0）；结束时间闭区间：上界取次日 00:00。
            var start = dateFrom?.Date ?? new DateTime(2000, 1, 1);
            var endEx = dateTo.HasValue ? dateTo.Value.Date.AddDays(1) : DateTime.MaxValue.Date;

            // EF 的 {N} 占位符拼接参数；所有筛选条件均走参数绑定，无字符串拼接注入。
            var ps = new List<object> { start, endEx };
            string whCte = "", whOuter = "", kwClause = "", catClause = "";

            if (warehouseId.HasValue)
            {
                int a = ps.Count; ps.Add(warehouseId.Value);
                int b = ps.Count; ps.Add(warehouseId.Value);
                whCte = " AND i2.warehouse_id = {" + a + "} ";
                whOuter = " AND i.warehouse_id = {" + b + "} ";
            }
            if (!string.IsNullOrEmpty(keyword))
            {
                int k = ps.Count; ps.Add("%" + keyword + "%");
                kwClause = " AND (i.item_code LIKE {" + k + "} OR i.item_full_name LIKE {" + k + "} OR i.item_short_name LIKE {" + k + "} OR i.item_spec LIKE {" + k + "} OR i.item_pinyin_code LIKE {" + k + "}) ";
            }
            if (!string.IsNullOrEmpty(category))
            {
                int c = ps.Count; ps.Add(category);
                catClause = " AND i.product_category = {" + c + "} ";
            }

            // 账本数量口径（与 StockService.SqlEntryQty 完全一致）
            const string q = "(COALESCE(e.\"f_num\",0)+COALESCE(e.\"f_num_ext1\",0)+COALESCE(e.\"f_num_ext2\",0)+COALESCE(e.\"f_num_ext3\",0))";
            // 带方向的数量：出库(FROB=-1)取负
            const string signed = "(CASE WHEN e.\"frob\" = -1 THEN -" + q + " ELSE " + q + " END)";
            // 出库类列展示为正数（取 signed 的相反数）
            const string outsigned = "(CASE WHEN e.\"frob\" = -1 THEN " + q + " ELSE -" + q + " END)";

            // ---- 账本聚合列：商品级 / 批次级【共用同一份定义】 ----
            // 两级唯一的差别是分组键（商品 vs 商品+批号），列口径必须逐字相同，
            // 否则「商品行 = Σ 批次行」这条自洽不变量会被破坏。
            // 采购入库金额为**除税** Σ FAfterTaxAmount（用户裁定 2026-09-11）。
            string ledgerCols = @"
    COALESCE(SUM(CASE WHEN h.""f_date"" < {0} THEN " + signed + @" ELSE 0 END),0) AS ""Opening"",
    COALESCE(SUM(CASE WHEN h.""f_date"" < {1} THEN " + signed + @" ELSE 0 END),0) AS ""Closing"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" = 0 THEN " + signed + @" ELSE 0 END),0) AS ""PurchaseInQty"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" = 0 THEN COALESCE(e.""f_after_tax_amount"",0) ELSE 0 END),0) AS ""PurchaseInAmount"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" = 3 THEN " + signed + @" ELSE 0 END),0) AS ""ProdReturnQty"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" = 1 THEN " + signed + @" ELSE 0 END),0) AS ""ProdInQty"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" = 8 THEN " + outsigned + @" ELSE 0 END),0) AS ""ProdPickQty"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" = 6 THEN " + signed + @" ELSE 0 END),0) AS ""ExchangeInQty"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" = 9 THEN " + outsigned + @" ELSE 0 END),0) AS ""DeptPickQty"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" = 2 THEN " + signed + @" ELSE 0 END),0) AS ""DeptReturnQty"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" IN (7,10) THEN " + signed + @" ELSE 0 END),0) AS ""StocktakeQty"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" = 5 THEN " + outsigned + @" ELSE 0 END),0) AS ""LossQty"",
    MAX(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND e.""frob"" = -1 THEN h.""f_date"" ELSE NULL END) AS ""LastOut""";

            // ---- 商品主档列投影（两级共用） ----
            const string outerCols = @"i.item_code AS ""Cpbm"", i.item_full_name AS ""Cpqc"", i.item_spec AS ""Cpgg"",
       i.item_pinyin_code AS ""Cppym"", i.package_unit AS ""Wlbzdw"", COALESCE(i.product_category,'') AS ""Cplb"",
       w.""dep_name"" AS ""WarehouseName""";

            // ---- 商品级查询：一行一个商品；同时算参考单价（批次加权均价） ----
            string sql = @"
WITH ""Ledger"" AS (
  SELECT e.""f_item_id"" AS ""ItemId"", CAST('' AS varchar(30)) AS ""BatchNo""," + ledgerCols + @"
  FROM   ""t_pms_stock_bill_entry"" e
  JOIN   ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
  JOIN   ""t_erp_item"" i2 ON i2.""id"" = e.""f_item_id""
  WHERE  COALESCE(h.""f_state"",false) = true AND e.""f_item_id"" IS NOT NULL AND i2.is_enabled = true" + whCte + @"
  GROUP  BY e.""f_item_id""
),
-- 参考单价 = 批次加权均价（用户裁定 2026-09-11：报表与领料/退料/报损统一按【批次口径】）
--   Σ(批次结存 × 批次价) ÷ Σ(批次结存)；批次价 ""f_price"" 本身即**除税**价
--   结存<=0 的批次不计入；全部批次结存均为 0 时该商品无参考价(NULL)
--   不再读商品主档 ""t_erp_item"".factory_price，也不再取「最近一次明细价」
""BatchCost"" AS (
  SELECT b.""f_item_id"" AS ""ItemId"",
         SUM(COALESCE(b.""f_batch_num"",0) * COALESCE(b.""f_price"",0)) AS ""TotalCost"",
         SUM(COALESCE(b.""f_batch_num"",0)) AS ""TotalQty""
  FROM   ""t_pms_batch_no_stock"" b
  WHERE  COALESCE(b.""f_state"",false) = true AND b.""f_item_id"" IS NOT NULL AND COALESCE(b.""f_batch_num"",0) > 0
  GROUP  BY b.""f_item_id""
),
-- 批次剩余 = Σ ""f_batch_num""（批次账本缓存口径；不过滤 >0，零结存批次也是事实的一部分）。
-- 与账面 ""Closing"" 并排展示（用户裁定 2026-09-11）：两数不等 = 缓存漂移，页面红色高亮自行暴露。
""BatchRemain"" AS (
  SELECT b.""f_item_id"" AS ""ItemId"",
         CAST(SUM(COALESCE(b.""f_batch_num"",0)) AS decimal(28,10)) AS ""Qty""
  FROM   ""t_pms_batch_no_stock"" b
  WHERE  COALESCE(b.""f_state"",false) = true AND b.""f_item_id"" IS NOT NULL
  GROUP  BY b.""f_item_id""
)
SELECT i.""id"" AS ""ItemId"", CAST('' AS varchar(30)) AS ""BatchNo"", " + outerCols + @",
       CASE WHEN COALESCE(bc.""TotalQty"",0) > 0 THEN CAST(bc.""TotalCost"" / bc.""TotalQty"" AS decimal(28,10)) ELSE NULL END AS ""RefPrice"",
       COALESCE(l.""Opening"",0) AS ""Opening"", COALESCE(l.""Closing"",0) AS ""Closing"",
       CAST(COALESCE(br.""Qty"",0) AS decimal(28,10)) AS ""BatchClosing"",
       COALESCE(l.""PurchaseInQty"",0) AS ""PurchaseInQty"", COALESCE(l.""PurchaseInAmount"",0) AS ""PurchaseInAmount"",
       COALESCE(l.""ProdReturnQty"",0) AS ""ProdReturnQty"", COALESCE(l.""ProdInQty"",0) AS ""ProdInQty"",
       COALESCE(l.""ProdPickQty"",0) AS ""ProdPickQty"", COALESCE(l.""ExchangeInQty"",0) AS ""ExchangeInQty"",
       COALESCE(l.""DeptPickQty"",0) AS ""DeptPickQty"", COALESCE(l.""DeptReturnQty"",0) AS ""DeptReturnQty"",
       COALESCE(l.""StocktakeQty"",0) AS ""StocktakeQty"", COALESCE(l.""LossQty"",0) AS ""LossQty"",
       l.""LastOut"" AS ""LastOut""
FROM   ""t_erp_item"" i
LEFT   JOIN ""t_erp_department"" w ON w.""id"" = i.warehouse_id
LEFT   JOIN ""Ledger"" l ON l.""ItemId"" = i.""id""
LEFT   JOIN ""BatchCost"" bc ON bc.""ItemId"" = i.""id""
LEFT   JOIN ""BatchRemain"" br ON br.""ItemId"" = i.""id""
WHERE  i.is_enabled = true" + whOuter + kwClause + catClause + @"
ORDER  BY COALESCE(i.product_category,''), i.item_code";

            var all = _db.Database.SqlQueryRaw<StockStatRow>(sql, ps.ToArray()).ToList();

            // 采购单价（**除税**口径，用户裁定 2026-09-11）：
            //   本期有采购入库 → 期间除税加权均价 = Σ FAfterTaxAmount ÷ Σ 数量
            //   本期无采购     → 回落 RefPrice = 批次加权均价 Σ(结存×批次价)÷Σ结存（批次价本身即除税）
            // 两个分支同源除税，故整列口径一致；均无值时页面显示 "-"。
            // 该规则对商品行与批次行**同一套**：批次行把聚合范围收窄到单个批号，
            // 「批次价加权」退化为该批次自身价格，公式无需分支。
            decimal? ResolvePrice(StockStatRow r)
            {
                if (r.PurchaseInQty > 0.0001m) return r.PurchaseInAmount / r.PurchaseInQty;
                return (r.RefPrice ?? 0m) > 0.0001m ? r.RefPrice : (decimal?)null;
            }

            foreach (var r in all) r.PurchasePrice = ResolvePrice(r);

            // 汇总：对当前筛选条件下全部商品求和（非仅当页）
            var summary = new StockStatSummary
            {
                Opening = all.Sum(r => r.Opening),
                Closing = all.Sum(r => r.Closing),
                BatchClosing = all.Sum(r => r.BatchClosing ?? 0m),
                PurchaseInQty = all.Sum(r => r.PurchaseInQty),
                PurchaseInAmount = all.Sum(r => r.PurchaseInAmount),
                ProdReturnQty = all.Sum(r => r.ProdReturnQty),
                ProdInQty = all.Sum(r => r.ProdInQty),
                ProdPickQty = all.Sum(r => r.ProdPickQty),
                ExchangeInQty = all.Sum(r => r.ExchangeInQty),
                DeptPickQty = all.Sum(r => r.DeptPickQty),
                DeptReturnQty = all.Sum(r => r.DeptReturnQty),
                StocktakeQty = all.Sum(r => r.StocktakeQty),
                LossQty = all.Sum(r => r.LossQty),
            };

            int total = all.Count;
            page = PagerViewModel.ClampPage(page, total, pageSize);
            var rows = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            // ================= 批次级明细（用户裁定 2026-09-11：报表细化到批次） =================
            // 分组键 = (商品, 批号)，批号取自 t_PMS_StockBillEntry.FBatchNo——
            // 采购入库/生产入库建批次时盖章，领料/退料按 FIFO 扣批时也盖章，
            // 因此「哪个批次被领走多少」在明细行上是可归属的，这正是旧系统读不到的信息。
            // 只查【当前页】商品的批次行（IN 列表参数化），不随总数放大内存。
            foreach (var r in rows) r.Batches = new List<StockStatRow>();
            if (rows.Count > 0)
            {
                var bps = new List<object> { start, endEx };
                string bWhCte = "";
                if (warehouseId.HasValue)
                {
                    int a = bps.Count; bps.Add(warehouseId.Value);
                    bWhCte = " AND i2.warehouse_id = {" + a + "} ";
                }
                // ⚠ EF6 SqlQuery 要求占位符 {0}..{n-1} 严格连续，跳号会直接抛异常（500）。
                //   故必须先取 bps.Count 作为下标，再 Add。
                var idPh = new List<string>();
                foreach (var it in rows)
                {
                    int n = bps.Count; bps.Add(it.ItemId);
                    idPh.Add("{" + n + "}");
                }
                string idIn = string.Join(",", idPh);

                string bsql = @"
WITH ""Ledger"" AS (
  SELECT e.""f_item_id"" AS ""ItemId"", COALESCE(e.""f_batch_no"",'') AS ""BatchNo""," + ledgerCols + @"
  FROM   ""t_pms_stock_bill_entry"" e
  JOIN   ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
  JOIN   ""t_erp_item"" i2 ON i2.""id"" = e.""f_item_id""
  WHERE  COALESCE(h.""f_state"",false) = true AND e.""f_item_id"" IS NOT NULL AND i2.is_enabled = true
         AND e.""f_item_id"" IN (" + idIn + @")" + bWhCte + @"
  GROUP  BY e.""f_item_id"", COALESCE(e.""f_batch_no"",'')
),
-- 批次级参考价：对该批次自身加权。批次价是【批次的属性】，与剩余结存无关，
-- 故此处【不】过滤结存<=0——已领完的批次仍要能显示它当初的批次价。
""BatchCost"" AS (
  SELECT b.""f_item_id"" AS ""ItemId"", COALESCE(b.""f_batch_no"",'') AS ""BatchNo"",
         SUM(COALESCE(b.""f_batch_num"",0) * COALESCE(b.""f_price"",0)) AS ""TotalCost"",
         SUM(COALESCE(b.""f_batch_num"",0)) AS ""TotalQty""
  FROM   ""t_pms_batch_no_stock"" b
  WHERE  COALESCE(b.""f_state"",false) = true AND b.""f_item_id"" IS NOT NULL AND b.""f_item_id"" IN (" + idIn + @")
  GROUP  BY b.""f_item_id"", COALESCE(b.""f_batch_no"",'')
),
-- 批次剩余（批次级）：该批次自身的 Σ ""f_batch_num""；批号在批次表缺失时为 0（本身就是漂移信号）
""BatchRemain"" AS (
  SELECT b.""f_item_id"" AS ""ItemId"", COALESCE(b.""f_batch_no"",'') AS ""BatchNo"",
         CAST(SUM(COALESCE(b.""f_batch_num"",0)) AS decimal(28,10)) AS ""Qty""
  FROM   ""t_pms_batch_no_stock"" b
  WHERE  COALESCE(b.""f_state"",false) = true AND b.""f_item_id"" IS NOT NULL AND b.""f_item_id"" IN (" + idIn + @")
  GROUP  BY b.""f_item_id"", COALESCE(b.""f_batch_no"",'')
)
SELECT i.""id"" AS ""ItemId"", l.""BatchNo"" AS ""BatchNo"", " + outerCols + @",
       CASE WHEN COALESCE(bc.""TotalQty"",0) > 0 THEN CAST(bc.""TotalCost"" / bc.""TotalQty"" AS decimal(28,10)) ELSE NULL END AS ""RefPrice"",
       COALESCE(l.""Opening"",0) AS ""Opening"", COALESCE(l.""Closing"",0) AS ""Closing"",
       CAST(COALESCE(br.""Qty"",0) AS decimal(28,10)) AS ""BatchClosing"",
       COALESCE(l.""PurchaseInQty"",0) AS ""PurchaseInQty"", COALESCE(l.""PurchaseInAmount"",0) AS ""PurchaseInAmount"",
       COALESCE(l.""ProdReturnQty"",0) AS ""ProdReturnQty"", COALESCE(l.""ProdInQty"",0) AS ""ProdInQty"",
       COALESCE(l.""ProdPickQty"",0) AS ""ProdPickQty"", COALESCE(l.""ExchangeInQty"",0) AS ""ExchangeInQty"",
       COALESCE(l.""DeptPickQty"",0) AS ""DeptPickQty"", COALESCE(l.""DeptReturnQty"",0) AS ""DeptReturnQty"",
       COALESCE(l.""StocktakeQty"",0) AS ""StocktakeQty"", COALESCE(l.""LossQty"",0) AS ""LossQty"",
       l.""LastOut"" AS ""LastOut""
FROM   ""t_erp_item"" i
JOIN   ""Ledger"" l ON l.""ItemId"" = i.""id""
LEFT   JOIN ""t_erp_department"" w ON w.""id"" = i.warehouse_id
LEFT   JOIN ""BatchCost"" bc ON bc.""ItemId"" = i.""id"" AND bc.""BatchNo"" = l.""BatchNo""
LEFT   JOIN ""BatchRemain"" br ON br.""ItemId"" = i.""id"" AND br.""BatchNo"" = l.""BatchNo""
WHERE  i.is_enabled = true
ORDER  BY i.""id"", l.""BatchNo""";

                var batchRows = _db.Database.SqlQueryRaw<StockStatRow>(bsql, bps.ToArray()).ToList();

                // 纯休眠批次（本期无发生、期初结存也 0、从未出库）不占行——它们合计为 0，
                // 过滤后「商品行 = Σ 批次行」依然成立。
                bool HasActivity(StockStatRow b) =>
                    b.Opening != 0m || b.Closing != 0m || b.PurchaseInQty != 0m || b.PurchaseInAmount != 0m ||
                    b.ProdReturnQty != 0m || b.ProdInQty != 0m || b.ProdPickQty != 0m || b.ExchangeInQty != 0m ||
                    b.DeptPickQty != 0m || b.DeptReturnQty != 0m || b.StocktakeQty != 0m || b.LossQty != 0m ||
                    b.LastOut.HasValue;

                var byItem = batchRows.GroupBy(b => b.ItemId).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var r in rows)
                {
                    List<StockStatRow> bs;
                    if (!byItem.TryGetValue(r.ItemId, out bs)) bs = new List<StockStatRow>();
                    foreach (var b in bs)
                    {
                        b.PurchasePrice = ResolvePrice(b);
                        if (string.IsNullOrEmpty(b.BatchNo)) b.BatchNo = "（无批次）";
                    }
                    r.Batches = bs.Where(HasActivity).ToList();
                }
            }

            string WhName(int? id) =>
                id.HasValue ? (_db.t_ERP_Department.FirstOrDefault(d => d.ID == id.Value)?.DEPName ?? "") : "";

            ViewBag.Filter = new Dictionary<string, string>
            {
                ["warehouseId"] = warehouseId.HasValue ? warehouseId.Value.ToString() : "",
                ["dateFrom"] = dateFrom?.ToString("yyyy-MM-dd") ?? "",
                ["dateTo"] = dateTo?.ToString("yyyy-MM-dd") ?? "",
                ["keyword"] = keyword ?? "",
                ["category"] = category ?? "",
            };
            ViewBag.WarehouseOptions = _db.t_ERP_Department
                .Where(d => d.IsDEP == 1).OrderBy(d => d.ID).ToList();
            ViewBag.CategoryOptions = _db.t_ERP_ITEM
                .Where(i => i.IsEnabled == true && !string.IsNullOrEmpty(i.ProductCategory))
                .Select(i => i.ProductCategory).Distinct().OrderBy(c => c).ToList();
            ViewBag.WarehouseId = warehouseId;
            ViewBag.WarehouseName = WhName(warehouseId);
            ViewBag.Summary = summary;

            var extra = new Dictionary<string, object>();
            if (warehouseId.HasValue) extra["warehouseId"] = warehouseId.Value;
            if (dateFrom.HasValue) extra["dateFrom"] = dateFrom.Value.ToString("yyyy-MM-dd");
            if (dateTo.HasValue) extra["dateTo"] = dateTo.Value.ToString("yyyy-MM-dd");
            if (!string.IsNullOrEmpty(keyword)) extra["keyword"] = keyword;
            if (!string.IsNullOrEmpty(category)) extra["category"] = category;
            ViewBag.Pager = new PagerViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                Action = "Index",
                ExtraValues = extra.Count > 0 ? extra : null,
            };

            return View(rows);
        }
    }
}
