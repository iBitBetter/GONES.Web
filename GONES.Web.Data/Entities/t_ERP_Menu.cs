using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_ERP_Menu
{
    public int ID { get; set; }

    public string MenuName { get; set; }

    public int? ParentID { get; set; }

    public int? MenuType { get; set; }

    public string DllName { get; set; }

    public string ClassName { get; set; }

    public int? OpenType { get; set; }

    public bool Flag { get; set; }

    public string Memo { get; set; }

    /// <summary>Web 路由（如 /PmsItem）：菜单与控制器权限绑定的唯一真相源。</summary>
    public string Route { get; set; }

    public int? Operating { get; set; }
}
