using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// 登录表单绑定模型。登录名对应 t_Base_User.FName。
    /// </summary>
    public class LoginViewModel
    {
        [Required(ErrorMessage = "请输入用户名")]
        [Display(Name = "用户名")]
        public string UserName { get; set; }

        [Required(ErrorMessage = "请输入密码")]
        [DataType(DataType.Password)]
        [Display(Name = "密码")]
        public string Password { get; set; }
    }
}
