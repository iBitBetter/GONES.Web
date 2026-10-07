namespace GONES.Web.Models
{
    /// <summary>
    /// 用户管理「姓名」下拉的一个候选项 —— 数据来源 t_PMS_Worker（人员管理）。
    ///
    /// ⚠ DepId 是**冒泡后**的部门 ID，不是人员表里的原始 DEPID：
    ///   人员管理允许把人员挂在末级组织（班组 IsDEP=3 / 车间 2 / 仓库 1），
    ///   而用户管理的「部门」下拉与写入校验口径都只有 **IsDEP == 0** 的真部门
    ///   （见 SysUserController.ValidateDepartmentAndRole）。若把原始 DEPID 直接带入，
    ///   下拉显示空白且保存被拒（"所选部门不存在或不是部门"），故这里预先向上找到
    ///   最近的真部门祖先；找不到时为 0，前端留空不覆盖。
    /// </summary>
    public class PersonPickItem
    {
        /// <summary>t_PMS_Worker.ID</summary>
        public int WorkerId { get; set; }

        /// <summary>人员姓名（t_PMS_Worker.FName），同名者只保留一条</summary>
        public string Name { get; set; }

        /// <summary>冒泡后的部门 ID（IsDEP==0）；0 = 未能确定</summary>
        public int DepId { get; set; }

        /// <summary>冒泡后的部门名称，仅用于下拉展示区分同名人员</summary>
        public string DepName { get; set; }
    }
}
