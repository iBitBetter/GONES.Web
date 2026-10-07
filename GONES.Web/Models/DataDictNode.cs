using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;

namespace GONES.Web.Models
{
    /// <summary>
    /// \u7801\u8868\u8f85\u52a9\u6a21\u578b\uff08t_ERP_DataDict\uff09\u3002
    /// \u5173\u952e\u8bed\u4e49\uff1a\u5b50\u9879\u7684 FParentID \u5b58\u7684\u662f\u7236\u5206\u7c7b\u7684 DictNo\uff08\u7f16\u7801\uff09\uff0c\u4e0d\u662f ID\u3002
    /// </summary>
    public class DataDictNode
    {
        /// <summary>\u6784\u5efa\u201c\u4e0a\u7ea7\u5206\u7c7b\u201d\u4e0b\u62c9\u9009\u9879\uff1a\u9009\u9879\u503c = \u5206\u7c7b\u7684 DictNo\uff08\u5b50\u9879 FParentID \u5b58\u7684\u5c31\u662f\u5b83\uff09\u3002</summary>
        /// <param name="categories">FParentID==0 \u7684\u5206\u7c7b\u884c</param>
        /// <param name="excludeNo">\u7f16\u8f91\u5206\u7c7b\u65f6\u6392\u9664\u81ea\u8eab DictNo\uff0c\u907f\u514d\u81ea\u5f15\u7528</param>
        public static List<SelectOption> BuildCategoryOptions(List<t_ERP_DataDict> categories, int excludeNo)
        {
            var options = new List<SelectOption>();
            foreach (var c in categories.OrderBy(c => c.ID))
            {
                int no;
                if (!int.TryParse(c.DictNo, out no)) continue;
                if (no == excludeNo) continue;
                options.Add(new SelectOption
                {
                    Id = no,
                    Text = c.DictName + " (" + c.DictNo + ")",
                    Indent = 0,
                });
            }
            return options;
        }
    }
}
