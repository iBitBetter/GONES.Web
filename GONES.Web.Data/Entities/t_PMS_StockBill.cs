using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_StockBill
{
    public int FInterID { get; set; }

    public string FBillNo { get; set; }

    public int? FBillType { get; set; }

    public DateTime? FDate { get; set; }

    public int? FDeptID { get; set; }

    public int? FCreaterID { get; set; }

    public string FRemark { get; set; }

    public int? FWorkShopID { get; set; }

    public int? FManagerID { get; set; }

    public int? FModifyID { get; set; }

    public DateTime? FModifyTime { get; set; }

    public string FBillTypeEx { get; set; }

    public string FBuyingUnit { get; set; }

    public int? FBuyerID { get; set; }

    public int? FGroupID { get; set; }

    public string FInspectors { get; set; }

    public short? FROB { get; set; }

    public bool? FState { get; set; }

    public string oaid { get; set; }

    public int? FSourceInterID { get; set; }
}
