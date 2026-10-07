using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Text;

namespace GONES.Web.Services
{
    /// <summary>
    /// 通用 Excel 导出：把表头 + 字符串化数据行拼成 HTML 表格，以 application/vnd.ms-excel 返回 .xls。
    /// 与 ProductInOutStockController.Export 既有模式一致（Excel 可直接打开的 HTML 表格，零第三方依赖）。
    /// 各列表页的 Export action 负责按自己的筛选查询数据、格式化为字符串单元格，再调用本方法。
    /// </summary>
    public static class ExcelExport
    {
        public static FileResult HtmlTable(string fileName, List<string> headers, List<List<string>> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<html><head><meta charset=\"utf-8\"></head><body>");
            sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"2\">");
            // 表头
            sb.Append("<tr style=\"background:#d9d9d9;font-weight:bold\">");
            foreach (var h in headers) sb.Append("<td>" + System.Net.WebUtility.HtmlEncode(h ?? "") + "</td>");
            sb.Append("</tr>");
            // 数据
            foreach (var r in rows)
            {
                sb.Append("<tr>");
                foreach (var c in r) sb.Append("<td>" + System.Net.WebUtility.HtmlEncode(c ?? "") + "</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table></body></html>");

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return new FileContentResult(bytes, "application/vnd.ms-excel") { FileDownloadName = fileName };
        }
    }
}
