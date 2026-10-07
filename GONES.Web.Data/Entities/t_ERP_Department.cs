using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_ERP_Department
{
    public int ID { get; set; }

    public int? ParentID { get; set; }

    public string DEPName { get; set; }

    public int DEPLevel { get; set; }

    public byte IsDEP { get; set; }

    public int Status { get; set; }
}
