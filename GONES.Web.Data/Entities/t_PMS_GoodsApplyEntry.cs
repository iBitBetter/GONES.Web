using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_GoodsApplyEntry
{
    public int FEntryID { get; set; }

    public int FInterID { get; set; }

    public int? FItemID { get; set; }

    public decimal? FQty { get; set; }

    public decimal? FPrice { get; set; }

    public decimal? FAmount { get; set; }

    public string FNote { get; set; }

    public int? FUnitID { get; set; }

    public DateTime? FFetchDate { get; set; }

    public decimal? FSecCoefficient { get; set; }

    public decimal? FSecQty { get; set; }

    public int? FStockID { get; set; }

    public int FBomId { get; set; }

    /// <summary>
    /// 0 未导入  1  部分导入 2 已导入
    /// </summary>
    public byte? FStatus { get; set; }

    /// <summary>
    /// 完成数量
    /// </summary>
    public decimal? FFetchNum { get; set; }
}
