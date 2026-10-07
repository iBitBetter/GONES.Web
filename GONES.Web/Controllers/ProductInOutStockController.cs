using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Data;
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
    /// 报表管理 - 生产产品收发存统计（旧 WinForms Report.FrmReceivedSendStockCount 的 Web 化，
    /// 菜单 t_ERP_Menu.ID=1313，ClassName=Report.FrmReceivedSendStockCount → /ProductInOutStock）。
    /// 取数口径复刻存储过程 p_PMS_ReceivedSendStock_New，按 (商品, 仓库) 分组；
    /// 期初/结存 = 期初(入库FB∈{0,1,2,3,4,6} − 出库FB∈{5,8,9}) + 本期同类；
    /// 采购入库=FB=0、生产入库=FB=1（本期）；工序列 = 各 FStepID 的「出库(FB∈{5,8,9}) − 入库(FB∈{2,3,4,6})」净额。
    /// 工序列完全由 t_PMS_Step 动态驱动（不硬编码），新增/删除工序无需改代码。
    /// 与旧 SP 的有意差异：统一仅统计已审核单(COALESCE(FState,false)=true)，改用条件聚合(单次扫描)。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 标准 MVC 表单重显，跳过 Furion 400 短路
    public class ProductInOutStockController : Controller, IMenuGuarded
    {
        // t_ERP_Menu.ID=1313「生产产品收发存统计」(ParentID=1281 生产-报表管理)

        private readonly GonesPgDbContext _db;
        private readonly MenuService _menu;
        public ProductInOutStockController(GonesPgDbContext db, MenuService menu)
        {
            _db = db;
            _menu = menu;
        }

        /// <summary>入口闸门：admin 旁路恒 true；非 admin 须由其角色授权覆盖本报表(1313)，否则回首页。</summary>

        private enum Ck { Name, Model, Unit, Class, Wh, Opening, Closing, Purchase, Prod, Step, ItemId, StockId }
        private struct Col { public string H; public Ck K; public int Key; }

        /// <summary>
        /// 动态工序列：从 t_PMS_Step 读取生效工序（FDelete=0、非测试桩 E2E_），按 FItemID 排序。
        /// 返回 (显示名, FStepID)；新增/删除工序会自动反映在报表列中。
        /// </summary>
        private List<(string Name, int FStepId)> GetSteps()
        {
            try
            {
                // 先物化再转元组：EF 表达式树不支持元组字面量投影；StartsWith 也放在内存侧执行，避免翻译限制
                var list = _db.t_PMS_Step
                    .Where(s => (s.FDelete ?? 0) == 0 && s.FName != null)
                    .OrderBy(s => s.FItemID)
                    .Select(s => new { s.FName, s.FItemID })
                    .ToList();
                return list
                    .Where(x => !x.FName.StartsWith("E2E_"))
                    .Select(x => (x.FName, x.FItemID))
                    .ToList();
            }
            catch { return new List<(string, int)>(); }
        }

        /// <summary>
        /// 列布局：与导出 xls 一致。步列 Key=该工序在 steps 中的 1-based 序号（=SQL 列别名 S{序号} 下标）。
        /// 保留旧 xls 的穿插顺序：采购入库 → 首道工序 → 生产入库 → 其余工序 → 存放仓库 → FItemID/FStockID。
        /// </summary>
        private List<Col> BuildLayout(List<(string Name, int FStepId)> steps)
        {
            var cols = new List<Col> {
                new Col{ H="产品名称", K=Ck.Name }, new Col{ H="规格型号", K=Ck.Model },
                new Col{ H="单位", K=Ck.Unit }, new Col{ H="产品分类", K=Ck.Class },
                new Col{ H="期初数量", K=Ck.Opening }, new Col{ H="结存数量", K=Ck.Closing },
                new Col{ H="采购入库", K=Ck.Purchase },
            };
            if (steps.Count > 0)
            {
                cols.Add(new Col { H = steps[0].Name, K = Ck.Step, Key = 1 });
                cols.Add(new Col { H = "生产入库", K = Ck.Prod });
                for (int i = 1; i < steps.Count; i++)
                    cols.Add(new Col { H = steps[i].Name, K = Ck.Step, Key = i + 1 });
            }
            else
            {
                cols.Add(new Col { H = "生产入库", K = Ck.Prod });
            }
            cols.Add(new Col { H = "存放仓库", K = Ck.Wh });
            cols.Add(new Col { H = "FItem ID", K = Ck.ItemId });
            cols.Add(new Col { H = "FStock ID", K = Ck.StockId });
            return cols;
        }

        // GET /ProductInOutStock?warehouseId=&category=&dateFrom=&dateTo=&keyword=&page=&pageSize=
        public IActionResult Index(int? warehouseId, string category, DateTime? dateFrom, DateTime? dateTo,
            string keyword, int page = 1, int pageSize = 50)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);
            keyword = (keyword ?? "").Trim();
            category = (category ?? "").Trim();

            var steps = GetSteps();
            var rows = QueryRows(steps, warehouseId, category, dateFrom, dateTo, keyword, out var total);
            var paged = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            var layout = BuildLayout(steps);

            // 预生成单元格
            foreach (var r in paged) r.Cells = layout.Select(c => Cell(r, c)).ToList();

            // 合计（按全部筛选结果，不受分页影响）
            var sum = new ProductInOutStockSummary { StepVals = new decimal[steps.Count + 1] };
            foreach (var r in rows)
            {
                sum.Opening += r.Opening; sum.Closing += r.Closing;
                sum.PurchaseIn += r.PurchaseIn; sum.ProdIn += r.ProdIn;
                for (int i = 1; i <= steps.Count; i++) sum.StepVals[i] += r.Step(i);
            }
            sum.Cells = layout.Select(c => Cell(sum, c)).ToList();

            var vm = new ProductInOutStockViewModel
            {
                Columns = layout.Select(c => c.H).ToList(),
                Numeric = layout.Select(c => c.K != Ck.Name && c.K != Ck.Model && c.K != Ck.Unit && c.K != Ck.Class && c.K != Ck.Wh).ToList(),
                Rows = paged,
                SummaryCells = sum.Cells,
            };

            ViewBag.Filter = new Dictionary<string, string>
            {
                ["dateFrom"] = dateFrom?.ToString("yyyy-MM-dd") ?? "",
                ["dateTo"] = dateTo?.ToString("yyyy-MM-dd") ?? "",
                ["keyword"] = keyword,
                ["category"] = category,
            };
            ViewBag.WarehouseId = warehouseId;
            ViewBag.Category = category;
            ViewBag.WarehouseOptions = _db.t_ERP_Department
                .Where(d => d.IsDEP == 1).OrderBy(d => d.ID).ToList();
            ViewBag.CategoryOptions = _db.t_ERP_ITEM
                .Where(i => i.IsEnabled == true && !string.IsNullOrEmpty(i.ProductCategory))
                .Select(i => i.ProductCategory).Distinct().OrderBy(x => x).ToList();

            // 翻页时透传筛选条件（_Pager 据此生成带条件的页码链接）
            var extra = new Dictionary<string, object>();
            if (warehouseId.HasValue) extra["warehouseId"] = warehouseId.Value;
            if (!string.IsNullOrEmpty(category)) extra["category"] = category;
            if (!string.IsNullOrEmpty(keyword)) extra["keyword"] = keyword;
            if (dateFrom.HasValue) extra["dateFrom"] = dateFrom.Value.ToString("yyyy-MM-dd");
            if (dateTo.HasValue) extra["dateTo"] = dateTo.Value.ToString("yyyy-MM-dd");
            ViewBag.Pager = new PagerViewModel { Page = page, PageSize = pageSize, TotalCount = total, ExtraValues = extra };
            ViewBag.Total = total;
            ViewBag.Elapsed = "";
            return View(vm);
        }

        // GET /ProductInOutStock/Export?... —— 导出 .xls（HTML 表格，兼容旧「导出」按钮），导出全部筛选结果（不分页）
        public IActionResult Export(int? warehouseId, string category, DateTime? dateFrom, DateTime? dateTo, string keyword)
        {

            keyword = (keyword ?? "").Trim();
            category = (category ?? "").Trim();
            var steps = GetSteps();
            var rows = QueryRows(steps, warehouseId, category, dateFrom, dateTo, keyword, out _);
            var layout = BuildLayout(steps);
            foreach (var r in rows) r.Cells = layout.Select(c => Cell(r, c)).ToList();

            string T(object v) => v is decimal d ? d.ToString("0.####") : (v?.ToString() ?? "");

            var sb = new StringBuilder();
            sb.Append("<html><head><meta charset=\"utf-8\"></head><body>");
            sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"2\">");
            // 表头
            sb.Append("<tr style=\"background:#d9d9d9;font-weight:bold\">");
            foreach (var h in layout) sb.Append($"<td>{Esc(h.H)}</td>");
            sb.Append("</tr>");
            // 数据
            foreach (var r in rows)
            {
                sb.Append("<tr>");
                foreach (var v in r.Cells) sb.Append($"<td>{Esc(T(v))}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table></body></html>");

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            var fileName = "产品收发存统计" + DateTime.Now.ToString("yyyyMMdd") + ".xls";
            return File(bytes, "application/vnd.ms-excel", fileName);
        }

        /// <summary>执行聚合 SQL（口径见类注释）。工序列随 steps 动态生成；所有筛选参数化绑定，无字符串拼接注入。</summary>
        private List<ProductInOutStockRow> QueryRows(List<(string Name, int FStepId)> steps, int? warehouseId, string category,
            DateTime? dateFrom, DateTime? dateTo, string keyword, out int total)
        {
            var start = dateFrom?.Date ?? new DateTime(2000, 1, 1);
            var endEx = dateTo.HasValue ? dateTo.Value.Date.AddDays(1) : DateTime.MaxValue.Date;
            var ps = new List<object> { start, endEx };

            string wh = " WHERE COALESCE(h.\"f_state\",false) = true AND i.\"is_enabled\" = true ";
            if (warehouseId.HasValue)
            {
                int a = ps.Count; ps.Add(warehouseId.Value);
                wh += $" AND i.\"warehouse_id\" = @p{a} ";
            }
            if (!string.IsNullOrEmpty(category))
            {
                int c = ps.Count; ps.Add(category);
                wh += $" AND i.\"product_category\" = @p{c} ";
            }
            if (!string.IsNullOrEmpty(keyword))
            {
                int k = ps.Count; ps.Add("%" + keyword + "%");
                wh += $" AND (i.\"item_code\" LIKE @p{k} OR i.\"item_full_name\" LIKE @p{k} OR i.\"item_spec\" LIKE @p{k} OR i.\"item_pinyin_code\" LIKE @p{k}) ";
            }

            const string q = "COALESCE(e.\"f_num\",0)+COALESCE(e.\"f_num_ext1\",0)+COALESCE(e.\"f_num_ext2\",0)+COALESCE(e.\"f_num_ext3\",0)";

            // 工序列：按展示序号 1..n 生成 S1..Sn（别名序号 = steps 1-based 序号），FStepId 来自工序主档（受信任整数，直接内联）。
            // FB=7（盘盈）/10（盘亏）由盘点审核派生、与账本同向（7 入库 / 10 出库），必须计入，
            // 否则做过盘点后本报表期初/结存与 StockStat、批次账本口径出现无法解释的差额。
            var stepCols = new StringBuilder();
            for (int idx = 0; idx < steps.Count; idx++)
            {
                int pos = idx + 1;
                int fid = steps[idx].FStepId;
                stepCols.Append($@"
   ,COALESCE(SUM(CASE WHEN h.""f_date"">=@p0 AND h.""f_date""<@p1 AND h.""f_bill_type"" IN (5,8,9,10) AND e.""f_step_id""={fid} THEN ({q}) ELSE 0 END),0)
    -COALESCE(SUM(CASE WHEN h.""f_date"">=@p0 AND h.""f_date""<@p1 AND h.""f_bill_type"" IN (2,3,4,6,7) AND e.""f_step_id""={fid} THEN ({q}) ELSE 0 END),0) AS ""S{pos}""");
            }

            string sql = $@"
SELECT
    i.""item_full_name"" AS ""ItemName"", i.""item_spec"" AS ""ItemModel"", i.""package_unit"" AS ""ItemUnit"", COALESCE(i.""product_category"",'') AS ""ItemClass"",
    COALESCE(d.""dep_name"",'') AS ""WarehouseName"", i.""id"" AS ""ItemId"", COALESCE(i.""warehouse_id"",0) AS ""StockId"",
    COALESCE(SUM(CASE WHEN h.""f_date""<@p0 AND h.""f_bill_type"" IN (0,1,2,3,4,6,7) THEN ({q}) ELSE 0 END),0)
   -COALESCE(SUM(CASE WHEN h.""f_date""<@p0 AND h.""f_bill_type"" IN (5,8,9,10) THEN ({q}) ELSE 0 END),0) AS ""Opening"",
    COALESCE(SUM(CASE WHEN h.""f_date""<@p1 AND h.""f_bill_type"" IN (0,1,2,3,4,6,7) THEN ({q}) ELSE 0 END),0)
   -COALESCE(SUM(CASE WHEN h.""f_date""<@p1 AND h.""f_bill_type"" IN (5,8,9,10) THEN ({q}) ELSE 0 END),0) AS ""Closing"",
    COALESCE(SUM(CASE WHEN h.""f_date"">=@p0 AND h.""f_date""<@p1 AND h.""f_bill_type""=0 THEN ({q}) ELSE 0 END),0) AS ""PurchaseIn"",
    COALESCE(SUM(CASE WHEN h.""f_date"">=@p0 AND h.""f_date""<@p1 AND h.""f_bill_type""=1 THEN ({q}) ELSE 0 END),0) AS ""ProdIn""
    {stepCols}
FROM ""t_pms_stock_bill_entry"" e
JOIN ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
JOIN ""t_erp_item"" i ON i.""id"" = e.""f_item_id""
LEFT JOIN ""t_erp_department"" d ON d.""id"" = i.""warehouse_id"" AND d.""is_dep"" = 1
{wh}
GROUP BY i.""item_full_name"", i.""item_spec"", i.""package_unit"", i.""product_category"", d.""dep_name"", i.""id"", i.""warehouse_id""";

            var rows = new List<ProductInOutStockRow>();
            var conn = _db.Database.GetDbConnection();
            var wasClosed = conn.State == ConnectionState.Closed;
            if (wasClosed) conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                for (int i = 0; i < ps.Count; i++)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@p" + i;
                    p.Value = ps[i];
                    cmd.Parameters.Add(p);
                }
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var r = new ProductInOutStockRow
                    {
                        ItemName = GetStr(rdr, "ItemName"),
                        ItemModel = GetStr(rdr, "ItemModel"),
                        ItemUnit = GetStr(rdr, "ItemUnit"),
                        ItemClass = GetStr(rdr, "ItemClass"),
                        WarehouseName = GetStr(rdr, "WarehouseName"),
                        ItemId = GetInt(rdr, "ItemId"),
                        StockId = GetInt(rdr, "StockId"),
                        Opening = GetDec(rdr, "Opening"),
                        Closing = GetDec(rdr, "Closing"),
                        PurchaseIn = GetDec(rdr, "PurchaseIn"),
                        ProdIn = GetDec(rdr, "ProdIn"),
                        StepVals = new decimal[steps.Count + 1],
                    };
                    for (int idx = 0; idx < steps.Count; idx++)
                        r.StepVals[idx + 1] = GetDec(rdr, "S" + (idx + 1));
                    rows.Add(r);
                }
            }
            finally
            {
                if (wasClosed) conn.Close();
            }

            total = rows.Count;
            return rows;
        }

        private static decimal GetDec(System.Data.Common.DbDataReader r, string col)
        {
            int o = r.GetOrdinal(col);
            return r.IsDBNull(o) ? 0m : r.GetDecimal(o);
        }
        private static string GetStr(System.Data.Common.DbDataReader r, string col)
        {
            int o = r.GetOrdinal(col);
            return r.IsDBNull(o) ? "" : r.GetString(o);
        }
        private static int GetInt(System.Data.Common.DbDataReader r, string col)
        {
            int o = r.GetOrdinal(col);
            return r.IsDBNull(o) ? 0 : r.GetInt32(o);
        }

        private static object Cell(ProductInOutStockRow r, Col c) => c.K switch
        {
            Ck.Name => r.ItemName,
            Ck.Model => r.ItemModel,
            Ck.Unit => r.ItemUnit,
            Ck.Class => r.ItemClass,
            Ck.Wh => r.WarehouseName,
            Ck.Opening => r.Opening,
            Ck.Closing => r.Closing,
            Ck.Purchase => r.PurchaseIn,
            Ck.Prod => r.ProdIn,
            Ck.Step => r.Step(c.Key),
            Ck.ItemId => r.ItemId,
            Ck.StockId => r.StockId,
            _ => ""
        };

        private static object Cell(ProductInOutStockSummary s, Col c) => c.K switch
        {
            Ck.Name => "合计",
            Ck.Model => "", Ck.Unit => "", Ck.Class => "", Ck.Wh => "",
            Ck.Opening => s.Opening,
            Ck.Closing => s.Closing,
            Ck.Purchase => s.PurchaseIn,
            Ck.Prod => s.ProdIn,
            Ck.Step => s.Step(c.Key),
            Ck.ItemId => "", Ck.StockId => "",
            _ => ""
        };

        private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
    }
}
