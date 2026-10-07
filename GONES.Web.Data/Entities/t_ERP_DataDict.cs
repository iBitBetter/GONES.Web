using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_ERP_DataDict
{
    public string DictNo { get; set; }

    public string DictName { get; set; }

    public int FParentID { get; set; }

    public string Remark { get; set; }

    public int ID { get; set; }

    public bool? IsShow { get; set; }
}
