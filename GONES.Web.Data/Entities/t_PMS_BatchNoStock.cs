using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_BatchNoStock
{
    public int FInterID { get; set; }

    public int? FItemID { get; set; }

    public decimal? FBatchNum { get; set; }

    public string FBatchNo { get; set; }

    public int? FBillType { get; set; }

    public decimal? FPrice { get; set; }

    public DateTime? FBegDate { get; set; }

    public DateTime? FEndDate { get; set; }

    public bool? FState { get; set; }

    public string FName { get; set; }
}
