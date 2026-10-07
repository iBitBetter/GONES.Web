using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_GoodsApply
{
    public int FInterID { get; set; }

    public string FBillNo { get; set; }

    public short? FTranType { get; set; }

    public DateTime? FDate { get; set; }

    public int? FStockID { get; set; }

    public int? FEmpID { get; set; }

    public int? FCheckerID { get; set; }

    public int? FBillerID { get; set; }

    public int? FDeptID { get; set; }

    public short FStatus { get; set; }

    public bool FCancellation { get; set; }

    public DateTime? FCheckDate { get; set; }

    public string FExplanation { get; set; }

    public string FFetchAdd { get; set; }

    public int? FWorkShopID { get; set; }

    public int? FModifyerID { get; set; }

    public DateTime? FModifyDate { get; set; }

    public int? FUserID { get; set; }
}
