using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// \u7801\u8868\uff08t_ERP_DataDict\uff09\u7f16\u8f91\u6a21\u578b\u3002\u7801\u8868\u4e3a\u4e24\u7ea7\u7ed3\u6784\uff1a\u9876\u7ea7\u4e3a\u5206\u7c7b(FParentID=0)\uff0c\u5176\u4e0b\u4e3a\u7f16\u7801\u9879\u3002
    /// </summary>
    public class DataDictEditViewModel
    {
        public int ID { get; set; }

        [Required(ErrorMessage = "\u7f16\u7801\u4e0d\u80fd\u4e3a\u7a7a\u3002")]
        [StringLength(50)]
        [Display(Name = "\u7f16\u7801")]
        public string DictNo { get; set; }

        [Required(ErrorMessage = "\u540d\u79f0\u4e0d\u80fd\u4e3a\u7a7a\u3002")]
        [StringLength(100)]
        [Display(Name = "\u540d\u79f0")]
        public string DictName { get; set; }

        /// <summary>\u4e0a\u7ea7\uff1a0=\u9876\u7ea7\u5206\u7c7b\uff1b\u5426\u5219\u5fc5\u987b\u662f\u4e00\u4e2a\u5206\u7c7b(FParentID=0)\u7684 ID\u3002</summary>
        [Display(Name = "\u4e0a\u7ea7")]
        public int ParentId { get; set; }

        [Display(Name = "\u5907\u6ce8")]
        public string Remark { get; set; }

        [Display(Name = "\u663e\u793a")]
        public bool IsShow { get; set; } = true;
    }
}
