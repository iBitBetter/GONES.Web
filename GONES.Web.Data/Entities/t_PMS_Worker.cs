using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_PMS_Worker
{
    public string FName { get; set; }

    public int DEPID { get; set; }

    public bool? Status { get; set; }

    public int ID { get; set; }

    public string FMobile { get; set; }

    public string FIDNumber { get; set; }
}
