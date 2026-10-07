using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_ERP_ITEMGROUP
{
    public int ID { get; set; }

    public string ComponentItemGuid { get; set; }   // 旧 ysp_guid：子商品 guid

    public string ComponentItemCode { get; set; }    // 旧 ycpbm：子商品编码

    public string ComboItemGuid { get; set; }        // 旧 xsp_guid：组合商品 guid（本商品）

    public string ComboItemCode { get; set; }        // 旧 xcpbm：组合商品编码

    public int? BaseQuantity { get; set; }           // 基本数量

    public decimal? UnitPrice { get; set; }          // 子商品单价，默认取子商品零售价

    public int? DepartmentId { get; set; }           // 旧 dpid：部门 ID

    public bool? IsRepairStatus { get; set; }        // 旧 is_wx_zt：维修状态
}
