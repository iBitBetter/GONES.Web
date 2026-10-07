using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_StockBillEntry
{
    public int FEntryID { get; set; }

    public int FInterID { get; set; }

    public int FStepID { get; set; }

    public int FBomId { get; set; }

    public string FProduceNo { get; set; }

    public int? FProduceID { get; set; }

    public int? FItemID { get; set; }

    public DateTime? FPlanDate { get; set; }

    public decimal? FPlanNum { get; set; }

    public decimal? FNum { get; set; }

    public decimal? FPrice { get; set; }

    public decimal? FAmount { get; set; }

    public decimal? FNumExt1 { get; set; }

    public decimal? FNumExt2 { get; set; }

    public decimal? FNumExt3 { get; set; }

    public string FScheduleNo { get; set; }

    public string FSEOutNo { get; set; }

    public string FBillUseNo { get; set; }

    public string FBuyingNo { get; set; }

    public string FNote { get; set; }

    public string FBatchNo { get; set; }

    public string FTaxRate { get; set; }

    public decimal? FTaxRateValue { get; set; }

    public decimal? FAfterTaxPrice { get; set; }

    public decimal? FAfterTaxAmount { get; set; }

    public decimal? FTaxAmount { get; set; }

    public string FBomIDBatchNo { get; set; }

    public int? FBillUseEntryID { get; set; }

    public int? FScheduleNoID { get; set; }

    public DateTime? FBegDate { get; set; }

    public DateTime? FEndDate { get; set; }

    public int? FBatchNoID { get; set; }

    public short FROB { get; set; }

    public int? FPickBillID { get; set; }

    public int? FPickEntryID { get; set; }
}
