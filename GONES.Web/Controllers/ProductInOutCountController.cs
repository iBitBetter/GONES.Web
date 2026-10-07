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
    /// 报表管理 - 产品入出库统计（旧 WinForms Report.FrmInOutCount 的 Web 化，
    /// 菜单 t_ERP_Menu.ID=1315，ClassName=Report.FrmInOutCount → /ProductInOutCount）。
    /// 统一查看仓库的所有入库单 / 出库单，分 Tab 展现；每 Tab 含明细表 + 统计汇总（合计 + 按单据类型小计）。
    /// 取数复刻旧 SP p_PMS_StockBillInfoByWhere / p_PMS_ProductInOutStock 的口径：
    /// 明细 = StockBill 表头 JOIN StockBillEntry 明细 JOIN 商品/仓库/部门/工序/制单人；
    /// 汇总 = 按 (单据类型) 聚合 数量/扩展数量/金额。
    /// 与全站账本一致的有意差异：仅统计已审核单(COALESCE(FState,false)=true)。
    /// 仓库维度沿用 t_ERP_ITEM.WarehouseId（指向 t_ERP_Department(IsDEP=1)），与收发存页一致。
    /// 方向全集：入库 = {采购入库0, 生产入库1, 部门退料2, 仓库退料3, 仓库盘点4, 换货入库6, 盘点盈余7}；
    ///           出库 = {产品报损5, 仓库领料8, 部门领料9, 盘点亏损10}。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 标准 MVC 表单重显，跳过 Furion 400 短路
    public class ProductInOutCountController : Controller, IMenuGuarded
    {
        // t_ERP_Menu.ID=1315「产品入出库统计」(ParentID=1281 生产-报表管理)

        private static readonly int[] InBillTypes = { 0, 1, 2, 3, 4, 6, 7 };
        private static readonly int[] OutBillTypes = { 5, 8, 9, 10 };

        // FBillType → 标准单据类型名（与 t_PMS_StockBill.FBillTypeEx 落库口径一致）
        private static readonly Dictionary<int, string> BillTypeName = new Dictionary<int, string>
        {
            [0] = "采购入库", [1] = "生产入库", [2] = "部门退料", [3] = "仓库退料",
            [4] = "仓库盘点", [5] = "产品报损", [6] = "换货入库", [7] = "盘点盈余",
            [8] = "仓库领料", [9] = "部门领料", [10] = "盘点亏损",
        };

        private readonly GonesPgDbContext _db;
        private readonly MenuService _menu;
        public ProductInOutCountController(GonesPgDbContext db, MenuService menu)
        {
            _db = db;
            _menu = menu;
        }


        private static bool IsIn(string dir) => dir != "out";

        private List<(int FBillType, string Name)> BillTypeOptions(string dir)
        {
            var set = IsIn(dir) ? InBillTypes : OutBillTypes;
            return set.Select(t => (t, BillTypeName[t])).ToList();
        }

        // GET /ProductInOutCount?tab=in|out&dateFrom=&dateTo=&warehouseId=&keyword=&batchNo=&createrName=&billType=&page=&pageSize=
        public IActionResult Index(string tab, int? warehouseId, string keyword, string batchNo, string createrName,
            int billType = -1, DateTime? dateFrom = null, DateTime? dateTo = null, int page = 1, int pageSize = 50)
        {

            string dir = IsIn(tab) ? "in" : "out";
            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);
            keyword = (keyword ?? "").Trim();
            batchNo = (batchNo ?? "").Trim();
            createrName = (createrName ?? "").Trim();

            var rows = QueryRows(dir, warehouseId, keyword, batchNo, createrName, billType, dateFrom, dateTo, out var total);

            // 统计汇总（基于全部筛选结果，不受分页影响）
            var summary = BuildSummary(rows);

            var paged = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            var vm = new ProductInOutCountViewModel
            {
                Direction = dir,
                Rows = paged,
                Summary = summary,
                BillTypeOptions = BillTypeOptions(dir),
            };

            ViewBag.Filter = new Dictionary<string, string>
            {
                ["dateFrom"] = dateFrom?.ToString("yyyy-MM-dd") ?? "",
                ["dateTo"] = dateTo?.ToString("yyyy-MM-dd") ?? "",
                ["keyword"] = keyword,
                ["batchNo"] = batchNo,
                ["createrName"] = createrName,
            };
            ViewBag.Tab = dir;
            ViewBag.WarehouseId = warehouseId;
            ViewBag.BillType = billType;
            ViewBag.WarehouseOptions = _db.t_ERP_Department
                .Where(d => d.IsDEP == 1).OrderBy(d => d.ID).ToList();

            var extra = new Dictionary<string, object> { ["tab"] = dir };
            if (warehouseId.HasValue) extra["warehouseId"] = warehouseId.Value;
            if (!string.IsNullOrEmpty(keyword)) extra["keyword"] = keyword;
            if (!string.IsNullOrEmpty(batchNo)) extra["batchNo"] = batchNo;
            if (!string.IsNullOrEmpty(createrName)) extra["createrName"] = createrName;
            if (billType >= 0) extra["billType"] = billType;
            if (dateFrom.HasValue) extra["dateFrom"] = dateFrom.Value.ToString("yyyy-MM-dd");
            if (dateTo.HasValue) extra["dateTo"] = dateTo.Value.ToString("yyyy-MM-dd");
            ViewBag.Pager = new PagerViewModel { Page = page, PageSize = pageSize, TotalCount = total, ExtraValues = extra };
            ViewBag.Total = total;
            return View(vm);
        }

        // GET /ProductInOutCount/Export?... —— 导出 .xls（HTML 表格，兼容旧「导出」按钮），导出全部筛选结果（不分页）
        public IActionResult Export(string tab, int? warehouseId, string keyword, string batchNo, string createrName,
            int billType = -1, DateTime? dateFrom = null, DateTime? dateTo = null)
        {

            keyword = (keyword ?? "").Trim();
            batchNo = (batchNo ?? "").Trim();
            createrName = (createrName ?? "").Trim();
            string dir = IsIn(tab) ? "in" : "out";

            var rows = QueryRows(dir, warehouseId, keyword, batchNo, createrName, billType, dateFrom, dateTo, out _);
            var summary = BuildSummary(rows);

            string T(object v) => FormatVal(v);

            var sb = new StringBuilder();
            sb.Append("<html><head><meta charset=\"utf-8\"></head><body>");
            sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"2\">");
            // 表头
            sb.Append("<tr style=\"background:#d9d9d9;font-weight:bold\">");
            foreach (var h in new[] { "单号", "单据类型", "日期", "仓库", "部门/车间", "商品名称", "商品编码", "规格型号", "单位", "分类", "工序", "数量", "数量扩展1", "数量扩展2", "数量扩展3", "单价", "金额", "批号", "制单人", "备注" })
                sb.Append($"<td>{Esc(h)}</td>");
            sb.Append("</tr>");
            // 数据
            foreach (var r in rows)
            {
                sb.Append("<tr>");
                foreach (var v in new object[] { r.FBillNo, r.FBillTypeEx, r.FDate?.ToString("yyyy-MM-dd"), r.WarehouseName, r.DeptName, r.ItemName, r.ItemNumber, r.ItemModel, r.ItemUnit, r.ItemClass, r.StepName, r.FNum, r.FNumExt1, r.FNumExt2, r.FNumExt3, r.FPrice, r.FAmount, r.FBatchNo, r.CreaterName, r.FRemark })
                    sb.Append($"<td>{Esc(T(v))}</td>");
                sb.Append("</tr>");
            }
            // 合计行
            sb.Append("<tr style=\"font-weight:bold;background:#f0f0f0\">");
            foreach (var v in new object[] { "合计", "", "", "", "", "", "", "", "", "", "", summary.FNum, summary.FNumExt1, summary.FNumExt2, summary.FNumExt3, "", summary.FAmount, "", "", "" })
                sb.Append($"<td>{Esc(T(v))}</td>");
            sb.Append("</tr>");
            sb.Append("</table>");

            // 三套分组小计（二次分组：单据类型 / 仓库 / 商品）
            AppendSubtotalTable(sb, "按单据类型小计", summary.ByType, "单据类型");
            AppendSubtotalTable(sb, "按仓库小计", summary.ByWarehouse, "仓库");
            AppendSubtotalTable(sb, "按商品小计", summary.ByProduct, "商品");
            sb.Append("</body></html>");

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            var fileName = (dir == "in" ? "产品入库统计" : "产品出库统计") + DateTime.Now.ToString("yyyyMMdd") + ".xls";
            return File(bytes, "application/vnd.ms-excel", fileName);
        }

        private static void AppendSubtotalTable(StringBuilder sb, string title, List<ProductInOutCountSubtotal> rows, string dimHeader)
        {
            sb.Append("<br/><table border=\"1\" cellspacing=\"0\" cellpadding=\"2\">");
            sb.Append($"<tr style=\"background:#cfe2ff;font-weight:bold\"><td colspan=\"7\">{Esc(title)}</td></tr>");
            sb.Append("<tr style=\"background:#d9d9d9;font-weight:bold\">");
            foreach (var h in new[] { dimHeader, "单数", "数量", "数量扩展1", "数量扩展2", "数量扩展3", "金额" })
                sb.Append($"<td>{Esc(h)}</td>");
            sb.Append("</tr>");
            foreach (var r in rows)
            {
                sb.Append("<tr>");
                foreach (var v in new object[] { r.GroupName, r.Count, r.FNum, r.FNumExt1, r.FNumExt2, r.FNumExt3, r.FAmount })
                    sb.Append($"<td>{Esc(FormatVal(v))}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }

        private static ProductInOutCountSummary BuildSummary(List<ProductInOutCountRow> rows)
        {
            var s = new ProductInOutCountSummary();
            var byType = new Dictionary<string, ProductInOutCountSubtotal>();
            var byWh = new Dictionary<string, ProductInOutCountSubtotal>();
            var byItem = new Dictionary<string, ProductInOutCountSubtotal>();
            foreach (var r in rows)
            {
                s.Count++;
                s.FNum += r.FNum; s.FNumExt1 += r.FNumExt1; s.FNumExt2 += r.FNumExt2; s.FNumExt3 += r.FNumExt3; s.FAmount += r.FAmount;

                string label = r.FBillTypeEx ?? "";
                GroupAdd(byType, label, r);
                GroupAdd(byWh, r.WarehouseName ?? "", r);
                // 商品维度：优先用编码(稳定)作分组键，显示名带编码避免重名混淆
                string itemKey = string.IsNullOrEmpty(r.ItemNumber) ? ("#" + (r.ItemName ?? "")) : r.ItemNumber;
                string itemLabel = string.IsNullOrEmpty(r.ItemNumber)
                    ? (r.ItemName ?? "") + (string.IsNullOrEmpty(r.ItemName) ? "未知商品" : "")
                    : (string.IsNullOrEmpty(r.ItemName) ? r.ItemNumber : r.ItemName + " (" + r.ItemNumber + ")");
                GroupAdd(byItem, itemKey, r, itemLabel);
            }
            s.ByType = byType.Values.OrderBy(x => x.GroupName).ToList();
            s.ByWarehouse = byWh.Values.OrderBy(x => x.GroupName).ToList();
            s.ByProduct = byItem.Values.OrderBy(x => x.GroupName).ToList();
            return s;
        }

        private static void GroupAdd(Dictionary<string, ProductInOutCountSubtotal> dict, string key, ProductInOutCountRow r, string label = null)
        {
            key = key ?? "";
            if (!dict.TryGetValue(key, out var sub))
            {
                sub = new ProductInOutCountSubtotal { GroupName = label ?? key };
                dict[key] = sub;
            }
            sub.Count++;
            sub.FNum += r.FNum; sub.FNumExt1 += r.FNumExt1; sub.FNumExt2 += r.FNumExt2; sub.FNumExt3 += r.FNumExt3; sub.FAmount += r.FAmount;
        }

        /// <summary>执行明细 SQL（口径见类注释）。所有筛选参数化绑定，FB 方向集为受信任整数常量内联。</summary>
        private List<ProductInOutCountRow> QueryRows(string dir, int? warehouseId, string keyword, string batchNo,
            string createrName, int billType, DateTime? dateFrom, DateTime? dateTo, out int total)
        {
            var set = IsIn(dir) ? InBillTypes : OutBillTypes;
            var start = dateFrom?.Date ?? new DateTime(2000, 1, 1);
            var endEx = dateTo.HasValue ? dateTo.Value.Date.AddDays(1) : DateTime.MaxValue.Date;
            var ps = new List<object> { start, endEx };

            // 单据日期区间：@p0/@p1 即上面 ps 的前两项（缺省 2000-01-01 ~ 无穷大），
            // 必须显式写进 SQL —— 只 ps.Add 而不引用会让「单据日期」筛选静默失效
            //（页面照显用户输入、结果却全量返回，属最难发现的错数据类缺陷）。
            string wh = " WHERE COALESCE(h.\"f_state\",false) = true ";
            wh += " AND h.\"f_date\" >= @p0 AND h.\"f_date\" < @p1 ";
            wh += $" AND h.\"f_bill_type\" IN ({string.Join(",", set)}) ";
            if (warehouseId.HasValue)
            {
                int a = ps.Count; ps.Add(warehouseId.Value);
                wh += $" AND i.\"warehouse_id\" = @p{a} ";
            }
            if (!string.IsNullOrEmpty(keyword))
            {
                int k = ps.Count; ps.Add("%" + keyword + "%");
                wh += $" AND (i.\"item_code\" LIKE @p{k} OR i.\"item_full_name\" LIKE @p{k} OR i.\"item_spec\" LIKE @p{k} OR i.\"item_pinyin_code\" LIKE @p{k}) ";
            }
            if (!string.IsNullOrEmpty(batchNo))
            {
                int b = ps.Count; ps.Add("%" + batchNo + "%");
                wh += $" AND e.\"f_batch_no\" LIKE @p{b} ";
            }
            if (!string.IsNullOrEmpty(createrName))
            {
                int c = ps.Count; ps.Add("%" + createrName + "%");
                wh += $" AND u.\"user_name\" LIKE @p{c} ";
            }
            // 单据类型过滤：仅当所选类型属于当前方向时生效
            if (billType >= 0 && set.Contains(billType))
            {
                int t = ps.Count; ps.Add(billType);
                wh += $" AND h.\"f_bill_type\" = @p{t} ";
            }

            string sql = $@"
SELECT
    h.""f_bill_no"" AS ""FBillNo"", h.""f_bill_type_ex"" AS ""FBillTypeEx"", h.""f_date"" AS ""FDate"",
    COALESCE(i.""item_full_name"",'') AS ""ItemName"", COALESCE(i.""item_code"",'') AS ""ItemNumber"",
    COALESCE(i.""item_spec"",'') AS ""ItemModel"", COALESCE(i.""package_unit"",'') AS ""ItemUnit"", COALESCE(i.""product_category"",'') AS ""ItemClass"",
    COALESCE(dwh.""dep_name"",'') AS ""WarehouseName"", COALESCE(dws.""dep_name"",'') AS ""DeptName"",
    COALESCE(st.""f_name"",'') AS ""StepName"", COALESCE(u.""user_name"",'') AS ""CreaterName"",
    COALESCE(e.""f_batch_no"",'') AS ""FBatchNo"", COALESCE(h.""f_remark"",'') AS ""FRemark"",
    COALESCE(e.""f_num"",0) AS ""FNum"", COALESCE(e.""f_num_ext1"",0) AS ""FNumExt1"",
    COALESCE(e.""f_num_ext2"",0) AS ""FNumExt2"", COALESCE(e.""f_num_ext3"",0) AS ""FNumExt3"",
    COALESCE(e.""f_price"",0) AS ""FPrice"", COALESCE(e.""f_amount"",0) AS ""FAmount""
FROM ""t_pms_stock_bill_entry"" e
JOIN ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
JOIN ""t_erp_item"" i ON i.""id"" = e.""f_item_id""
LEFT JOIN ""t_erp_department"" dwh ON dwh.""id"" = i.""warehouse_id"" AND dwh.""is_dep"" = 1
LEFT JOIN ""t_erp_department"" dws ON dws.""id"" = h.""f_work_shop_id""
LEFT JOIN ""t_erp_user_info"" u ON u.""id"" = h.""f_creater_id""
LEFT JOIN ""t_pms_step"" st ON st.""f_item_id"" = e.""f_step_id""
{wh}
ORDER BY h.""f_date"" DESC, e.""f_entry_id""";

            var rows = new List<ProductInOutCountRow>();
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
                    rows.Add(new ProductInOutCountRow
                    {
                        FBillNo = GetStr(rdr, "FBillNo"),
                        FBillTypeEx = GetStr(rdr, "FBillTypeEx"),
                        FDate = rdr.IsDBNull(rdr.GetOrdinal("FDate")) ? (DateTime?)null : rdr.GetDateTime(rdr.GetOrdinal("FDate")),
                        ItemName = GetStr(rdr, "ItemName"),
                        ItemNumber = GetStr(rdr, "ItemNumber"),
                        ItemModel = GetStr(rdr, "ItemModel"),
                        ItemUnit = GetStr(rdr, "ItemUnit"),
                        ItemClass = GetStr(rdr, "ItemClass"),
                        WarehouseName = GetStr(rdr, "WarehouseName"),
                        DeptName = GetStr(rdr, "DeptName"),
                        StepName = GetStr(rdr, "StepName"),
                        CreaterName = GetStr(rdr, "CreaterName"),
                        FBatchNo = GetStr(rdr, "FBatchNo"),
                        FRemark = GetStr(rdr, "FRemark"),
                        FNum = GetDec(rdr, "FNum"),
                        FNumExt1 = GetDec(rdr, "FNumExt1"),
                        FNumExt2 = GetDec(rdr, "FNumExt2"),
                        FNumExt3 = GetDec(rdr, "FNumExt3"),
                        FPrice = GetDec(rdr, "FPrice"),
                        FAmount = GetDec(rdr, "FAmount"),
                    });
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

        private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        private static string FormatVal(object v) => v is decimal d ? d.ToString("0.####") : (v?.ToString() ?? "");
    }
}