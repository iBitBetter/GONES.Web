using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_ProduceOrderRawMaterialEntry
{
    public int FEntryID { get; set; }

    public int FInterID { get; set; }

    public int? FStepID { get; set; }

    public decimal? FNum { get; set; }

    public string FWorkHourUnit { get; set; }

    public string FPriceUnit { get; set; }

    public decimal? FFixedLoss { get; set; }

    public decimal? FLossStandValue { get; set; }

    public string FLossRate { get; set; }

    public decimal? FLossValue { get; set; }

    public string FLossUnit { get; set; }

    public decimal? FNeedNum { get; set; }

    public int? FBomId { get; set; }

    public string FRemark { get; set; }

    public int? FItemID { get; set; }
}
