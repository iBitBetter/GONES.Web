using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// Change-password form. Shared by the voluntary change (user menu) and by the
    /// forced change right after the first login with a generated initial password.
    /// </summary>
    public class ChangePasswordViewModel
    {
        [Required(ErrorMessage = "\u8bf7\u8f93\u5165\u5f53\u524d\u5bc6\u7801")]
        [DataType(DataType.Password)]
        [Display(Name = "\u5f53\u524d\u5bc6\u7801")]
        public string OldPassword { get; set; }

        [Required(ErrorMessage = "\u8bf7\u8f93\u5165\u65b0\u5bc6\u7801")]
        // 与 PasswordHelper.GenerateInitialPassword 的 >=8 位下限对齐：初始口令本身就是 8 位以上，
        // 改密却允许 6 位会让用户用更弱的口令覆盖掉系统分配的强口令。
        [StringLength(32, ErrorMessage = "\u65b0\u5bc6\u7801\u957f\u5ea6\u4e3a 8-32 \u4f4d", MinimumLength = 8)]
        [DataType(DataType.Password)]
        [Display(Name = "\u65b0\u5bc6\u7801")]
        public string NewPassword { get; set; }

        [Required(ErrorMessage = "\u8bf7\u518d\u6b21\u8f93\u5165\u65b0\u5bc6\u7801")]
        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "\u4e24\u6b21\u8f93\u5165\u7684\u65b0\u5bc6\u7801\u4e0d\u4e00\u81f4")]
        [Display(Name = "\u786e\u8ba4\u65b0\u5bc6\u7801")]
        public string ConfirmPassword { get; set; }
    }
}
