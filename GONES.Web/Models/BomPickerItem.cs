namespace GONES.Web.Models
{
    /// <summary>BOM \u660e\u7ec6\u91cc\u7684\u5546\u54c1\u5feb\u7167\uff08\u6765\u6e90 t_ERP_ITEM\uff09\uff0c\u4f9b\u4e0b\u62c9\u9009\u62e9\u4e0e\u5df2\u9009\u884c\u5c55\u793a\u5171\u7528\u3002</summary>
    public class BomPickerItem
    {
        public int Id { get; set; }
        public string Cpbm { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }

        /// <summary>\u4ea7\u54c1\u7c7b\u522b cplb\uff08\u53d6\u81ea\u5546\u54c1\u5206\u7c7b t_ERP_ITEMCLASS.lb\uff09\u3002</summary>
        public string Cplb { get; set; }

        /// <summary>\u9ed8\u8ba4\u5355\u4f4d\uff0c\u53d6 t_ERP_ITEM.BaseUnit\uff08\u7269\u6d41\u57fa\u672c\u5355\u4f4d\uff09\u3002</summary>
        public string Unit { get; set; }

        /// <summary>\u662f\u5426\u4ea7\u51fa\u54c1\uff08cplb \u4e3a\u4ea7\u6210\u54c1\u6216\u534a\u6210\u54c1\uff09\u3002</summary>
        public bool IsOutput { get; set; }

        /// <summary>\u662f\u5426\u505c\u7528\uff08ty_zt \u4e3a 0 \u6216\u7a7a\uff09\u3002</summary>
        public bool Stopped { get; set; }
    }
}
