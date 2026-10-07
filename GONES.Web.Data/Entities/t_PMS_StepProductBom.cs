using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_StepProductBom
{
    public int FID { get; set; }

    public int? FPItemID { get; set; }

    public int? FStepId { get; set; }

    public decimal? FLossStandValue { get; set; }

    public string FLossRate { get; set; }

    public decimal? FLossValue { get; set; }

    public string FLossUnit { get; set; }

    public decimal? FBaseNum { get; set; }

    public string FBaseUnit { get; set; }

    public string FCostType { get; set; }

    public int? FBomID { get; set; }

    public int? FPreStepID { get; set; }

    public int? FPrePItemID { get; set; }

    public string FStepName { get; set; }

    public string FRemark { get; set; }

    public int? FDelete { get; set; }

    public bool? FIsProduct { get; set; }
}
