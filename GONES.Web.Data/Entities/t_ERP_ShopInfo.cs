using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_ERP_ShopInfo
{
    public int ID { get; set; }

    public int? ShopNumber { get; set; }

    public string ShopName { get; set; }

    public string Phone { get; set; }

    public string QQ { get; set; }

    public string Address { get; set; }

    public int? OrderCount { get; set; }

    public string ShopType { get; set; }

    public string Appkey { get; set; }

    public string AppSecrect { get; set; }

    public string SessionKey { get; set; }

    public DateTime? SessionEndDate { get; set; }

    public bool? IsAutoDown { get; set; }

    public string ConfigData { get; set; }

    public bool? IsMessage { get; set; }

    public string Message { get; set; }

    public bool? IsMerge { get; set; }

    public string SendAisle { get; set; }

    public bool? IsRFM { get; set; }

    public bool? IsImport { get; set; }

    public int? ShopSort { get; set; }

    public bool? IsHandMerge { get; set; }

    public bool? IsEnable { get; set; }

    public bool? IsAgent { get; set; }
}
