using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// 用户编辑视图模型 —— 对应 t_ERP_UserInfo。
    /// 姓名映射到 UserName；登录账号为 Account；密码新建默认 123，编辑可重置。
    /// </summary>
    public class UserEditViewModel
    {
        public int ID { get; set; }

        [Required(ErrorMessage = "账号不能为空")]
        public string Account { get; set; }

        [Required(ErrorMessage = "姓名不能为空")]
        public string UserName { get; set; }

        [Required(ErrorMessage = "请选择部门")]
        public int? DEPID { get; set; }

        [Required(ErrorMessage = "请选择角色")]
        public int? RoleID { get; set; }

        /// <summary>是否启用（Status==1）</summary>
        public bool Enabled { get; set; }

        /// <summary>是否允许登录（IsLogin==1）</summary>
        public bool CanLogin { get; set; }

        /// <summary>店铺权限，逗号分隔的店铺 ID 列表</summary>
        public List<int> ShopIds { get; set; }

        /// <summary>
        /// 仓库权限（数据权限）：逗号分隔的仓库 ID 列表，落库到 t_ERP_UserInfo.Depot。
        /// 口径：只有 t_ERP_Department 中 IsDEP=1 的部门算仓库（IsDEP=2 是车间、3 是班组，不算）。
        /// 留空表示该账号看不到任何仓库数据（RoleID 1..3 的管理员不受限）。
        /// </summary>
        public List<int> DepotIds { get; set; }

        /// <summary>仅编辑时有效：勾选则把密码重置为默认 123</summary>
        public bool ResetPassword { get; set; }
    }
}
