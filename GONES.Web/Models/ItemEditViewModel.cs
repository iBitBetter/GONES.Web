using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// 商品编辑模型，对应 t_ERP_ITEM。
    /// 组合商品明细见 GroupItems。
    /// </summary>
    public class ItemEditViewModel
    {
        public int ID { get; set; }

        [Required(ErrorMessage = "商品编码不能为空。")]
        [StringLength(50)]
        [Display(Name = "商品编码")]
        public string Cpbm { get; set; }

        [Required(ErrorMessage = "商品全称不能为空。")]
        [StringLength(250)]
        [Display(Name = "商品全称")]
        public string Cpqc { get; set; }

        [Required(ErrorMessage = "商品简称不能为空。")]
        [StringLength(150)]
        [Display(Name = "商品简称")]
        public string Cpjc { get; set; }

        [Required(ErrorMessage = "商品拼音码不能为空。")]
        [StringLength(50)]
        [Display(Name = "商品拼音码")]
        public string Cppym { get; set; }

        [Required(ErrorMessage = "商品规格不能为空。")]
        [StringLength(50)]
        [Display(Name = "商品规格")]
        public string Cpgg { get; set; }

        /// <summary>商品分类（cpfl_id）。</summary>
        [Required(ErrorMessage = "请选择商品分类。")]
        [Display(Name = "商品分类")]
        public int? ClassId { get; set; }

        // 条形码非全局必填：仅当所选商品分类的一级类别(cplb/lb)为「产成品」时必填，见 PmsItemController.ValidateAndCheck
        [StringLength(50)]
        [Display(Name = "条形码")]
        public string Txm { get; set; }

        [Display(Name = "属性")]
        public string Sx { get; set; }

        [Display(Name = "属性2")]
        public string Sx1 { get; set; }

        [Required(ErrorMessage = "物流包装单位不能为空。")]
        [Display(Name = "包装单位")]
        public string Wlbzdw { get; set; }

        [Required(ErrorMessage = "包装率不能为空。")]
        [Display(Name = "包装率")]
        public int? Wlbzl { get; set; }

        [Required(ErrorMessage = "物流基本单位不能为空。")]
        [Display(Name = "基本单位")]
        public string Wljbdw { get; set; }

        [Required(ErrorMessage = "净含量不能为空。")]
        [Display(Name = "净含量")]
        public decimal? Jhl { get; set; }

        [Required(ErrorMessage = "总重量不能为空。")]
        [Display(Name = "总重量")]
        public decimal? Zzl { get; set; }

        [Required(ErrorMessage = "零售单价不能为空。")]
        [Display(Name = "零售单价")]
        public decimal? Lsdj { get; set; }

        /// <summary>出厂单价，留空时取零售单价。</summary>
        [Display(Name = "出厂单价")]
        public decimal? Ccdj { get; set; }

        /// <summary>加盟单价，留空时取零售单价。</summary>
        [Display(Name = "加盟单价")]
        public decimal? Jmdj { get; set; }

        /// <summary>成本单价，必填。</summary>
        [Required(ErrorMessage = "成本单价不能为空。")]
        [Display(Name = "成本单价")]
        public decimal? Cbdj { get; set; }

        /// <summary>经销价，选填。</summary>
        [Display(Name = "经销价")]
        public decimal? Jxdj { get; set; }

        /// <summary>税率字符串（如 13%），sl_sz 由服务端换算。</summary>
        [Display(Name = "税率")]
        public string Sl { get; set; }

        [StringLength(50)]
        [Display(Name = "外部编码")]
        public string Wbbm { get; set; }

        [StringLength(50)]
        [Display(Name = "产品类别")]
        public string Cplb { get; set; }

        [StringLength(50)]
        [Display(Name = "产品货位")]
        public string Cphw { get; set; }

        /// <summary>仓库 ID，取自 t_ERP_Department.ID。</summary>
        [Display(Name = "仓库")]
        public int? Ckid { get; set; }

        [StringLength(50)]
        [Display(Name = "备注")]
        public string Bz { get; set; }

        [Display(Name = "库存控制")]
        public bool KcZt { get; set; }

        [Display(Name = "发布控制")]
        public bool FbZt { get; set; }

        [Display(Name = "组合商品")]
        public bool ZhZt { get; set; }

        [Display(Name = "礼盒控制")]
        public bool DlbZt { get; set; }

        [Display(Name = "赠品控制")]
        public bool ZpZt { get; set; }

        [Display(Name = "核桃乳商品")]
        public bool KdZt { get; set; }

        /// <summary>启用（对应库 ty_zt：1=启用/0=停用，正向存储）。</summary>
        [Display(Name = "启用")]
        public bool Enabled { get; set; } = true;

        /// <summary>组合商品明细（t_ERP_ITEMGROUP），ZhZt=true 时必填。</summary>
        [Display(Name = "商品组合")]
        public List<ItemGroupInput> GroupItems { get; set; } = new List<ItemGroupInput>();
    }

    /// <summary>组合明细一行：子商品编码 + 基本数量 + 单价（默认取子商品零售价）。</summary>
    public class ItemGroupInput
    {
        [Display(Name = "商品编码")]
        public string Cpbm { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "基本数量必须大于 0。")]
        [Display(Name = "基本数量")]
        public int Jbsl { get; set; } = 1;

        [Display(Name = "单价")]
        public decimal? Dj { get; set; }
    }
}
