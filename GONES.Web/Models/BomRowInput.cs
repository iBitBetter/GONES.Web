namespace GONES.Web.Models
{
    /// <summary>
    /// BOM \u660e\u7ec6\u884c\uff08\u8868\u5355\u63d0\u4ea4\u7684\u4e00\u884c\uff09\u3002\u5bf9\u5e94 t_PMS_StepProductBom \u7684\u4e00\u884c\u8bb0\u5f55\u3002
    /// \u5355\u4f4d Unit \u540c\u65f6\u5199\u5165 FBaseUnit \u4e0e FLossUnit \u2014\u2014 \u4e0e\u65e7\u7cfb\u7edf\u4e00\u81f4
    /// \uff08\u65e7 FrmProductRelationBomEdit \u4e2d\u4e24\u8005\u5747\u53d6\u5546\u54c1\u7684 FItemUnit\uff09\u3002
    /// </summary>
    public class BomRowInput
    {
        /// <summary>
        /// \u5546\u54c1 ID\uff08t_ERP_ITEM.ID\uff0c\u65e7\u7cfb\u7edf\u4e3a ICItem \u7684 FItemID\uff09\u3002
        /// </summary>
        public int ItemId { get; set; }

        /// <summary>
        /// \u65b9\u5411\uff1atrue = \u5165\u5e93\u884c FIsProduct=1\uff0cfalse = \u51fa\u5e93\u6295\u5165\u884c FIsProduct=0\u3002
        /// \u7531\u7528\u6237\u52fe\u9009\u51b3\u5b9a\uff08\u4e0d\u518d\u6309\u5546\u54c1\u7c7b\u522b\u81ea\u52a8\u5224\u5b9a\uff09\u3002
        /// </summary>
        public bool IsProduct { get; set; }

        /// <summary>\u57fa\u672c\u7528\u91cf FBaseNum\u3002</summary>
        public decimal? BaseNum { get; set; }

        /// <summary>\u5355\u4f4d\uff0c\u5199\u5165 FBaseUnit \u4e0e FLossUnit\u3002</summary>
        public string Unit { get; set; }

        /// <summary>\u6807\u51c6\u635f\u8017 FLossStandValue\u3002</summary>
        public decimal? LossStandValue { get; set; }

        /// <summary>\u635f\u8017\u7387 FLossRate\uff08\u5982 0%\u30013%\uff09\u3002</summary>
        public string LossRate { get; set; }

        /// <summary>\u635f\u8017\u503c FLossValue\u3002</summary>
        public decimal? LossValue { get; set; }

        /// <summary>\u5907\u6ce8 FRemark\u3002</summary>
        public string Remark { get; set; }
    }
}
