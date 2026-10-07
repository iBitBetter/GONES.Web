using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_ProduceOrder
{
    public int FInterID { get; set; }

    public string FBillNo { get; set; }

    public DateTime? FDate { get; set; }

    public int? FDeptID { get; set; }

    public int? FCreaterID { get; set; }

    public int? FApplyerID { get; set; }

    public string FApplyNo { get; set; }

    public DateTime? FApplyDate { get; set; }

    public string FComment { get; set; }

    public short FStatus { get; set; }

    public int? FAuditorID { get; set; }

    public DateTime? FAuditDate { get; set; }

    public int? FModifyerID { get; set; }

    public DateTime? FModifyDate { get; set; }

    public string FRemark { get; set; }

    public string FScheduleNo { get; set; }

    public string FPickNo { get; set; }

    public short? FOrderStatus { get; set; }

    public int? FWorkShopID { get; set; }

    public int? FScheduleNoID { get; set; }
}
