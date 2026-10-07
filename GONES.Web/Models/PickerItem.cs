namespace GONES.Web.Models
{
    /// <summary>组合明细选品/行展示用的商品精简信息。</summary>
    public class PickerItem
    {
        public string Cpbm { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string Txm { get; set; }
        public string Sx { get; set; }
        public string Sx1 { get; set; }
        public decimal? Lsdj { get; set; }   // 零售价，组合明细单价默认值
    }
}
