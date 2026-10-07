using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_ERP_UserInfo
{
    public int ID { get; set; }

    public string Account { get; set; }

    public string PassWord { get; set; }

    public string UserName { get; set; }

    public int DEPID { get; set; }

    public string Depot { get; set; }

    public int RoleID { get; set; }

    public byte Status { get; set; }

    public string Shop { get; set; }

    public byte? IsLogin { get; set; }

    public string MacAddress { get; set; }

    public bool PwdMustChange { get; set; }
}
