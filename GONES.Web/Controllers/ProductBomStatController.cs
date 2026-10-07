using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using GONES.Model.Pg;
using GONES.Web.Models;
using GONES.Web.Services;
using Microsoft.EntityFrameworkCore;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// 报表管理 - 产品结构图汇总情况（旧 WinForms Report.FrmProduceTaskCount 的 Web 化，
    /// 菜单 t_ERP_Menu.ID=1306，ClassName=Report.FrmProduceTaskCount → /ProductBomStat）。
    /// 口径复刻存储过程 p_PMS_ProductBomSum @DateS,@DateE：按 (商品,工序,BOM) 分组，
    ///   领料数量 = 期间 FB=8/9；退料数量 = 期间 FB=2/3；
    ///   实际出库数量 = 累计(退料-领料)，截至结束日（无起始日过滤，与 SP 一致）；
    ///   完工入库数量 = 期间 FB=1；实际出库金额 = 期间 FB=8/9/2/3 的 FAmount；
    ///   出库单价 = 最近一次 FB=8 的 FAfterTaxPrice(空则 FPrice)，回落 FB=4&FROB=1 的 FPrice；
    ///   产成品行（QtyIn>0 且 BomId>0）单价 = 生产入库单(FB=1)期间加权落库价
    ///     = Σ(FB=1 明细金额) ÷ 完工入库数量，金额 = 完工数量 × 单价（用户裁定 2026-09-11）。
    ///   （旧 SP 要求 FB=8 且 FROB=1，但 Web 端领料出库明细 FROB 恒为 -1、FAfterTaxPrice 恒空 →
    ///    按本库约定改为不限方向并回落 FPrice，否则单价列恒为空。）
    /// 与 SP 的有意差异：
    ///   1. 商品主档 v_PMS_ICItem 在本库为空表 → 改用 t_ERP_ITEM(ty_zt=true)，仓库名 t_Stock → t_ERP_Department(IsDEP=1)。
    ///   2. 仅统计已审核单 COALESCE(FState"",false)=true，与全站账本口径一致（旧 SP 不判审核状态）。
    ///   3. 工序表 INNER JOIN 放宽为 LEFT JOIN，未知工序不再整行吞掉。
    /// 后处理与旧窗体 FrmProduceTaskCount 一致，唯一有意差异：
    ///   a) FQtyIn&gt;0 且 FBomId&gt;0 的行（本结构图的产出行），单价/金额改取【生产入库单(FB=1)落库价】，
    ///      不再沿用旧窗体「金额 = 同 FBomId 包装物金额合计、单价 = 金额 ÷ 完工数量」的单位包装成本口径；
    ///   b) 「只查看包装物」= 仅保留产品分类 ∈ {产成品, 包装物}；
    ///   c) 排序：完工入库数量降序。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 标准 MVC 表单重显，跳过 Furion 400 短路
    public class ProductBomStatController : Controller, IMenuGuarded
    {
        // t_ERP_Menu.ID=1306「产品结构图汇总情况」(ParentID=1281 生产-报表管理)

        private readonly GonesPgDbContext _db;
        private readonly MenuService _menu;
        public ProductBomStatController(GonesPgDbContext db, MenuService menu)
        {
            _db = db;
            _menu = menu;
        }

        /// <summary>
        /// 入口闸门：admin 旁路恒 true（行为逐字节不变）；非 admin 须由其角色的
        /// t_ERP_RoleMenu 授权覆盖本报表（1306），否则回首页 —— 与 7 个单据模块同一范式。
        /// 注：报表是纯查询页，按既有裁定【允许跨仓查询】，故只做入口闸门、不收窄仓库维度。
        /// </summary>

        // GET /ProductBomStat?warehouseId=&stepId=&dateFrom=&dateTo=&onlyPacking=
        public IActionResult Index(int? warehouseId, int? stepId, DateTime? dateFrom, DateTime? dateTo, bool onlyPacking = false)
        {

            var sw = Stopwatch.StartNew();
            var rows = PostProcess(QueryRows(warehouseId, stepId, dateFrom, dateTo), onlyPacking);
            sw.Stop();

            // 排序沿用旧窗体 gvColumnsSort：完工入库数量降序；分组保持首现顺序。
            rows = rows.OrderByDescending(r => r.QtyIn).ThenBy(r => r.ItemName).ToList();
            var groups = new List<ProductBomStatGroup>();
            var total = new ProductBomStatGroup();
            foreach (var r in rows)
            {
                var g = groups.FirstOrDefault(x => x.BomId == r.BomId);
                if (g == null)
                {
                    g = new ProductBomStatGroup { BomId = r.BomId };
                    groups.Add(g);
                }
                g.Rows.Add(r);
                g.QtyGet += r.QtyGet; g.QtyBack += r.QtyBack; g.QtyOut += r.QtyOut;
                g.QtyIn += r.QtyIn; g.OutAmount += r.OutAmount;
                total.QtyGet += r.QtyGet; total.QtyBack += r.QtyBack; total.QtyOut += r.QtyOut;
                total.QtyIn += r.QtyIn; total.OutAmount += r.OutAmount;
            }

            ViewBag.Filter = new Dictionary<string, string>
            {
                ["dateFrom"] = dateFrom?.ToString("yyyy-MM-dd") ?? "",
                ["dateTo"] = dateTo?.ToString("yyyy-MM-dd") ?? "",
            };
            ViewBag.WarehouseId = warehouseId;
            ViewBag.StepId = stepId;
            ViewBag.OnlyPacking = onlyPacking;
            ViewBag.WarehouseOptions = _db.t_ERP_Department
                .Where(d => d.IsDEP == 1).OrderBy(d => d.ID).ToList();
            ViewBag.StepOptions = _db.Database.SqlQueryRaw<ProductBomStatStepOption>("SELECT \"f_item_id\" AS \"Id\", \"f_name\" AS Name FROM \"t_pms_step\" ORDER BY \"f_item_id\"")
                .ToList();
            ViewBag.Total = total;
            ViewBag.Elapsed = sw.Elapsed.TotalSeconds.ToString("0.###");

            return View(groups);
        }

        // GET /ProductBomStat/Export?... —— 导出 .xls（HTML 表格，兼容旧「导出」按钮）
        public IActionResult Export(int? warehouseId, int? stepId, DateTime? dateFrom, DateTime? dateTo, bool onlyPacking = false)
        {

            var rows = PostProcess(QueryRows(warehouseId, stepId, dateFrom, dateTo), onlyPacking);
            rows = rows.OrderByDescending(r => r.QtyIn).ThenBy(r => r.ItemName).ToList();

            string T(decimal v) => v.ToString("0.####");
            string M(decimal? v) => v.HasValue ? v.Value.ToString("0.00") : "";

            var sb = new StringBuilder();
            sb.Append("<html><head><meta charset=\"utf-8\"></head><body>");
            sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"2\">");
            sb.Append("<tr style=\"background:#d9d9d9;font-weight:bold\">"
                      + "<td>BomID</td><td>工序名称</td><td>产品分类</td><td>产品代码</td><td>产品名称</td>"
                      + "<td>规格型号</td><td>单位</td><td>领料数量</td><td>退料数量</td><td>实际出库数量</td>"
                      + "<td>实际出库单价</td><td>实际出库金额</td><td>完工入库数量</td></tr>");
            int lastBom = int.MinValue;
            decimal sGet = 0, sBack = 0, sOut = 0, sIn = 0, sAmt = 0;
            foreach (var r in rows)
            {
                if (r.BomId != lastBom)
                {
                    if (lastBom != int.MinValue)
                        sb.Append("<tr style=\"background:#f2f2f2;font-weight:bold\">"
                                  + $"<td colspan=\"7\">结构图 BomID: {lastBom} 小计</td>"
                                  + $"<td>{T(sGet)}</td><td>{T(sBack)}</td><td>{T(sOut)}</td>"
                                  + $"<td></td><td>{M(sAmt)}</td><td>{T(sIn)}</td></tr>");
                    lastBom = r.BomId; sGet = sBack = sOut = sIn = sAmt = 0;
                }
                sb.Append($"<tr><td>{r.BomId}</td><td>{Esc(r.StepName)}</td><td>{Esc(r.ItemClass)}</td>"
                          + $"<td>{Esc(r.ItemNumber)}</td><td>{Esc(r.ItemName)}</td><td>{Esc(r.ItemModel)}</td>"
                          + $"<td>{Esc(r.ItemUnit)}</td><td>{T(r.QtyGet)}</td><td>{T(r.QtyBack)}</td>"
                          + $"<td>{T(r.QtyOut)}</td><td>{M(r.OutPrice)}</td><td>{M(r.OutAmount)}</td><td>{T(r.QtyIn)}</td></tr>");
                sGet += r.QtyGet; sBack += r.QtyBack; sOut += r.QtyOut; sIn += r.QtyIn; sAmt += r.OutAmount;
            }
            if (lastBom != int.MinValue)
                sb.Append("<tr style=\"background:#f2f2f2;font-weight:bold\">"
                          + $"<td colspan=\"7\">结构图 BomID: {lastBom} 小计</td>"
                          + $"<td>{T(sGet)}</td><td>{T(sBack)}</td><td>{T(sOut)}</td>"
                          + $"<td></td><td>{M(sAmt)}</td><td>{T(sIn)}</td></tr>");
            sb.Append("</table></body></html>");

            var bytes = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            var fileName = "按产品结构任务单汇总情况统计" + DateTime.Now.ToString("yyyyMMdd") + ".xls";
            return File(bytes, "application/vnd.ms-excel", fileName);
        }

        private static string Esc(string s)
            => System.Net.WebUtility.HtmlEncode(s ?? "");

        /// <summary>执行聚合 SQL（口径见类注释）。所有筛选参数化绑定，无字符串拼接注入。</summary>
        private List<ProductBomStatRow> QueryRows(int? warehouseId, int? stepId, DateTime? dateFrom, DateTime? dateTo)
        {
            var start = dateFrom?.Date ?? new DateTime(2000, 1, 1);
            var endEx = dateTo.HasValue ? dateTo.Value.Date.AddDays(1) : DateTime.MaxValue.Date;

            var ps = new List<object> { start, endEx };
            string whClause = "", stepClause = "";
            if (warehouseId.HasValue)
            {
                int a = ps.Count; ps.Add(warehouseId.Value);
                whClause = " AND i.warehouse_id = {" + a + "} ";
            }
            if (stepId.HasValue)
            {
                int b = ps.Count; ps.Add(stepId.Value);
                stepClause = " AND s.\"StepId\" = {" + b + "} ";
            }

            string sql = @"
WITH ""S"" AS (
  SELECT e.""f_item_id"" AS ""ItemId"", COALESCE(e.""f_step_id"",0) AS ""StepId"", COALESCE(e.""f_bom_id"",0) AS ""BomId"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" IN (8,9) THEN e.""f_num"" ELSE 0 END),0) AS ""QtyGet"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" IN (2,3) THEN e.""f_num"" ELSE 0 END),0) AS ""QtyBack"",
    COALESCE(SUM(CASE WHEN h.""f_bill_type"" IN (2,3) THEN e.""f_num"" WHEN h.""f_bill_type"" IN (8,9) THEN -e.""f_num"" ELSE 0 END),0) AS ""QtyNet"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" = 1 THEN e.""f_num"" ELSE 0 END),0) AS ""QtyIn"",
    -- 生产入库(""FB""=1)明细金额：产成品行单价的分子（= 完工数量 × 落库单价）
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" = 1
                    THEN COALESCE(e.""f_amount"", COALESCE(e.""f_num"",0) * COALESCE(e.""f_price"",0)) ELSE 0 END),0) AS ""ProduceInAmount"",
    COALESCE(SUM(CASE WHEN h.""f_date"" >= {0} AND h.""f_date"" < {1} AND h.""f_bill_type"" IN (8,9,2,3) THEN COALESCE(e.""f_amount"",0) ELSE 0 END),0) AS ""OutAmount""
  FROM   ""t_pms_stock_bill_entry"" e
  JOIN   ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
  WHERE  h.""f_date"" < {1} AND COALESCE(h.""f_state"",false) = true AND e.""f_item_id"" IS NOT NULL
  GROUP  BY e.""f_item_id"", e.""f_step_id"", e.""f_bom_id""
)
SELECT s.""BomId"", COALESCE(st.""f_name"",'') AS ""StepName"", COALESCE(i.product_category,'') AS ""ItemClass"",
       COALESCE(i.item_code,'') AS ""ItemNumber"", COALESCE(i.item_full_name,'') AS ""ItemName"", COALESCE(i.item_spec,'') AS ""ItemModel"",
       COALESCE(i.package_unit,'') AS ""ItemUnit"", COALESCE(i.warehouse_id,0) AS ""StockId"", COALESCE(w.""dep_name"",'') AS ""StockName"",
       s.""QtyGet"", s.""QtyBack"", s.""QtyNet"" AS ""QtyOut"", s.""QtyIn"", s.""OutAmount"", s.""ProduceInAmount"",
       -- 兜底价：最近一次 ""FB""=1（生产入库）明细价，仅在期间明细无金额时启用
       (SELECT t.""f_price"" FROM ""t_pms_stock_bill_entry"" t
          JOIN ""t_pms_stock_bill"" s5 ON t.""f_inter_id"" = s5.""f_inter_id""
         WHERE s5.""f_bill_type"" = 1 AND COALESCE(s5.""f_state"",false) = true AND t.""f_item_id"" = s.""ItemId""
         ORDER BY s5.""f_date"" DESC LIMIT 1) AS ""ProduceInLastPrice"",
       COALESCE((SELECT COALESCE(t.""f_after_tax_price"", t.""f_price"") FROM ""t_pms_stock_bill_entry"" t
                 JOIN ""t_pms_stock_bill"" s2 ON t.""f_inter_id"" = s2.""f_inter_id""
                WHERE s2.""f_bill_type"" = 8 AND COALESCE(s2.""f_state"",false) = true AND t.""f_item_id"" = s.""ItemId""
                ORDER BY s2.""f_date"" DESC LIMIT 1),
              (SELECT t.""f_price"" FROM ""t_pms_stock_bill_entry"" t
                 JOIN ""t_pms_stock_bill"" s2 ON t.""f_inter_id"" = s2.""f_inter_id""
                WHERE s2.""f_bill_type"" = 4 AND s2.""frob"" = 1 AND t.""f_item_id"" = s.""ItemId""
                ORDER BY s2.""f_date"" DESC LIMIT 1)) AS ""OutPrice""
FROM   ""S"" s
JOIN   ""t_erp_item"" i ON i.""id"" = s.""ItemId"" AND i.is_enabled = true
LEFT   JOIN ""t_erp_department"" w ON w.""id"" = i.warehouse_id
LEFT   JOIN ""t_pms_step"" st ON st.""f_item_id"" = s.""StepId""
WHERE  (s.""QtyGet"" <> 0 OR s.""QtyBack"" <> 0 OR s.""QtyIn"" <> 0)" + whClause + stepClause;

            return _db.Database.SqlQueryRaw<ProductBomStatRow>(sql, ps.ToArray()).ToList();
        }

        /// <summary>旧窗体 GetFormatReportInfo：包装物金额汇总赋给产成品行的实际出库金额 +「只查看包装物」过滤。
        /// 产成品行（本结构图的产出行，FQtyIn&gt;0 且 FBomId&gt;0）的单价与金额改为取【生产入库单(FB=1)落库价】
        /// （用户裁定 2026-09-11）：单价 = 期间 FB=1 明细金额 ÷ 完工入库数量（即期间加权落库价），
        /// 金额 = 完工数量 × 单价 = 期间 FB=1 明细金额合计。期间明细无金额时回落最近一次 FB=1 明细价；
        /// 两者皆无则单价留空、金额 0。
        /// 注意：旧实现把产成品行金额替换为「同 FBomId 包装物金额合计」，再算 金额÷完工数量，
        /// 得到的是单位包装成本（且单价与 0 出库数量自相矛盾），故废弃。</summary>
        private static List<ProductBomStatRow> PostProcess(List<ProductBomStatRow> rows, bool onlyPacking)
        {
            foreach (var r in rows)
            {
                if (r.QtyIn > 0 && r.BomId > 0)
                {
                    if (r.ProduceInAmount != 0m)
                    {
                        r.OutPrice = r.ProduceInAmount / r.QtyIn;
                        r.OutAmount = r.ProduceInAmount;
                    }
                    else if (r.ProduceInLastPrice.HasValue)
                    {
                        r.OutPrice = r.ProduceInLastPrice.Value;
                        r.OutAmount = r.QtyIn * r.ProduceInLastPrice.Value;
                    }
                    else
                    {
                        r.OutPrice = null;
                        r.OutAmount = 0m;
                    }
                }
            }
            if (onlyPacking)
            {
                rows = rows.Where(r =>
                {
                    var c = (r.ItemClass ?? "").Trim();
                    return c == "产成品" || c == "包装物";
                }).ToList();
            }
            return rows;
        }
    }
}
