using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_Step
{
    public int FItemID { get; set; }

    public string FName { get; set; }

    public string FType { get; set; }

    public string FAlgo { get; set; }

    public decimal? FHour { get; set; }

    public decimal? FNum { get; set; }

    public decimal? FSingleArti { get; set; }

    public decimal? FHourArti { get; set; }

    public decimal? FPrice { get; set; }

    public string FWorkHourUnit { get; set; }

    public string FPriceUnit { get; set; }

    public decimal? FFixedLoss { get; set; }

    public decimal? FLossStandValue { get; set; }

    public string FLossRate { get; set; }

    public decimal? FLossValue { get; set; }

    public string FLossUnit { get; set; }

    public int? FDelete { get; set; }
}
