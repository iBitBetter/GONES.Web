using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_ERP_ITEMCLASS
{
    public int ID { get; set; }

    /// <summary>上级类别代码（存父分类 ID 串，与旧 lbdm 一致）。</summary>
    public string ParentClassCode { get; set; }

    /// <summary>类别名称（与旧 lbmc 一致）。</summary>
    public string ClassName { get; set; }

    /// <summary>类别全称（路径名，如 产成品_包装物，与旧 qmmc 一致）。</summary>
    public string FullName { get; set; }

    /// <summary>类别全码（点分路径，如 1.2.10，与旧 qmdm 一致）。</summary>
    public string FullCode { get; set; }

    /// <summary>类别级别（1=根 2=大类 3=末级，与旧 lbjb 一致）。</summary>
    public int? ClassLevel { get; set; }

    /// <summary>使用状态（1=启用 0=停用，与旧 sy_zt 一致）。</summary>
    public int? UseStatus { get; set; }

    /// <summary>一级类别名（从全称第一段派生，与旧 lb 一致）。</summary>
    public string TopClassName { get; set; }

    /// <summary>父分类 ID（项目通用父 ID 字段，保持不变）。</summary>
    public int? FParentID { get; set; }
}
