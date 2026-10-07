using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_BillUse
{
    public int FInterID { get; set; }

    public string FBillNo { get; set; }

    public string FBillType { get; set; }

    public DateTime? FDate { get; set; }

    public int? FDeptID { get; set; }

    public int? FCreaterID { get; set; }

    public string FRemark { get; set; }

    public int? FWorkShopID { get; set; }

    public int? FWorkerID { get; set; }

    public int? FGroupID { get; set; }

    public string FWorkPeople { get; set; }
}
