using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// 列表分页视图模型。控制器填充 Page / PageSize / TotalCount，
    /// 共享局部视图 _Pager.cshtml 据此渲染分页条（页码 + 上一页/下一页 + 每页条数切换）。
    /// </summary>
    public class PagerViewModel
    {
        /// <summary>当前页（从 1 开始）。</summary>
        public int Page { get; set; } = 1;

        /// <summary>每页条数，固定取值 10 / 20 / 50 / 100，非法值回退 20。</summary>
        public int PageSize { get; set; } = 20;

        /// <summary>总记录数。</summary>
        public int TotalCount { get; set; }

        /// <summary>目标 Action 名，默认 Index。</summary>
        public string Action { get; set; } = "Index";

        /// <summary>目标 Controller 名，留空则使用当前 Controller。</summary>
        public string Controller { get; set; } = "";

        /// <summary>
        /// 翻页时需透传的额外查询参数（如人员管理按部门过滤的 depId）。
        /// 为 null 或空字典时不影响现有行为；_Pager 渲染页码链接与每页条数表单时会合并这些参数。
        /// </summary>
        public Dictionary<string, object> ExtraValues { get; set; }

        public int TotalPages => PageSize <= 0 ? 1 : (int)Math.Ceiling((double)TotalCount / PageSize);

        public bool HasPrev => Page > 1;

        public bool HasNext => Page < TotalPages;

        /// <summary>规整每页条数到 10/20/50/100，非法值回退 20。</summary>
        public static int NormalizePageSize(int size)
        {
            return size switch
            {
                10 or 50 or 100 => size,
                _ => 20
            };
        }

        /// <summary>把页码规整到 [1, TotalPages] 有效范围（越界页码落到首/末页）。</summary>
        public static int ClampPage(int page, int total, int pageSize)
        {
            if (pageSize <= 0) pageSize = 20;
            int totalPages = (int)Math.Ceiling((double)total / pageSize);
            if (totalPages < 1) totalPages = 1;
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;
            return page;
        }
    }
}
