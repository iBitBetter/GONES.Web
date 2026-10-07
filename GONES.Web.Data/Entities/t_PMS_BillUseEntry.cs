using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_BillUseEntry
{
    public int FEntryID { get; set; }

    public int FInterID { get; set; }

    public int FStepID { get; set; }

    public int FBomId { get; set; }

    public decimal? FUseNum { get; set; }

    public string FProduceNo { get; set; }

    public int? FProduceId { get; set; }

    public int? FItemID { get; set; }

    public decimal? FCurrentUseNum { get; set; }

    public decimal? FLastUseNum { get; set; }

    public string FScheduleNo { get; set; }

    public int? FScheduleNoID { get; set; }

    public bool? FIsProduct { get; set; }

    public bool? FIsUse { get; set; }
}
