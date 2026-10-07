using System;
using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// 店铺编辑视图模型 —— 复刻旧 WinForms SysManager.FrmShopInfoEdit。
    /// </summary>
    public class ShopInfoEditViewModel
    {
        public int ID { get; set; }

        [Display(Name = "店铺编号")]
        [Required(ErrorMessage = "店铺编号不能为空")]
        [Range(1, int.MaxValue, ErrorMessage = "店铺编号必须为正整数")]
        public int ShopNumber { get; set; }

        [Display(Name = "店铺名称")]
        [Required(ErrorMessage = "店铺名称不能为空")]
        [StringLength(100, ErrorMessage = "店铺名称最多 100 个字符")]
        public string ShopName { get; set; }

        [Display(Name = "联系电话")]
        [StringLength(50, ErrorMessage = "联系电话最多 50 个字符")]
        public string Phone { get; set; }

        [Display(Name = "QQ号码")]
        [StringLength(50, ErrorMessage = "QQ号码最多 50 个字符")]
        public string QQ { get; set; }

        [Display(Name = "店铺类型")]
        [StringLength(50, ErrorMessage = "店铺类型最多 50 个字符")]
        public string ShopType { get; set; }

        [Display(Name = "自动下载")]
        public bool IsAutoDown { get; set; }

        [Display(Name = "联系地址")]
        [StringLength(200, ErrorMessage = "联系地址最多 200 个字符")]
        public string Address { get; set; }

        [Display(Name = "Appkey")]
        [StringLength(200, ErrorMessage = "Appkey 最多 200 个字符")]
        public string Appkey { get; set; }

        [Display(Name = "AppSecrect")]
        [StringLength(500, ErrorMessage = "AppSecrect 最多 500 个字符")]
        public string AppSecrect { get; set; }

        [Display(Name = "SessionKey")]
        [StringLength(500, ErrorMessage = "SessionKey 最多 500 个字符")]
        public string SessionKey { get; set; }

        [Display(Name = "Key 到期时间")]
        [DataType(DataType.Date)]
        public DateTime? SessionEndDate { get; set; }

        [Display(Name = "订单每次下载数量")]
        [Range(1, int.MaxValue, ErrorMessage = "订单每次下载数量必须为正整数")]
        public int? OrderCount { get; set; } = 100;

        [Display(Name = "短信通道")]
        [StringLength(50, ErrorMessage = "短信通道最多 50 个字符")]
        public string SendAisle { get; set; }

        [Display(Name = "是否发送短信")]
        public bool IsMessage { get; set; }

        [Display(Name = "店铺序号")]
        [Required(ErrorMessage = "店铺序号不能为空")]
        [Range(1, int.MaxValue, ErrorMessage = "店铺序号必须为正整数")]
        public int ShopSort { get; set; }

        [Display(Name = "催付短信模板")]
        [DataType(DataType.MultilineText)]
        public string ConfigData { get; set; }

        [Display(Name = "发货短信模板")]
        [DataType(DataType.MultilineText)]
        public string Message { get; set; }

        [Display(Name = "启用")]
        public bool IsEnable { get; set; } = true;

        /// <summary>表单辅助：是否发送短信的下拉文本（旧系统三态：是/否/请选择）。</summary>
        public static string[] YesNoOptions => new[] { "是", "否" };

        /// <summary>店铺类型下拉（与旧系统实际取值对齐）。</summary>
        public static string[] ShopTypeOptions => new[] { "TM", "JD", "YHD", "1688", "OTHERS" };

        /// <summary>短信通道下拉（常见通道，兜底本地库无字典表）。</summary>
        public static (string Value, string Text)[] SendAisleOptions => new[]
        {
            ("", "请选择"),
            ("阿里大鱼", "阿里大鱼"),
            ("腾讯云短信", "腾讯云短信"),
            ("创蓝253", "创蓝253"),
            ("梦网云", "梦网云"),
            ("亿美软通", "亿美软通"),
        };
    }
}
