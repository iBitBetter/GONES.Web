using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_ProduceOrderProductEntry
{
    public int FEntryID { get; set; }

    public int FInterID { get; set; }

    public decimal? FPlanNum { get; set; }

    public DateTime FPlanDate { get; set; }

    public int FBomId { get; set; }

    public string FBatchNumber { get; set; }

    public string FProduceType { get; set; }

    public string FOutFactory { get; set; }

    public decimal? FOutPrice { get; set; }

    public decimal? FOutAmount { get; set; }

    public int? FItemID { get; set; }

    public byte? FPickingStatus { get; set; }

    public byte? FProductionPickingStatus { get; set; }

    public byte? FStockStatus { get; set; }

    public int? FBillUseFInterID { get; set; }

    public int? FBillApplyEntryID { get; set; }

    // 来源要货单 FInterID（与 FBillApplyEntryID 组成复合键，精确区分跨单同号要货行，
    // 根治 UsedPlanByApplyEntryIds 按 FEntryID 跨单据误判）。
    public int? FBillApplyInterID { get; set; }
}
