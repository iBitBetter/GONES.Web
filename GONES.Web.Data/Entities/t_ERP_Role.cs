using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_ERP_Role
{
    public string RoleName { get; set; }

    public string RoleMemo { get; set; }

    public byte Iselect { get; set; }

    public byte IsReadonly { get; set; }

    public int ID { get; set; }
}
