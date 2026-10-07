using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace GONES.Model.Pg;

// 单据类型名（t_PMS_StockBill.FBillTypeEx）落库统一口径：
//   含「单」字 → 去掉「单」字，并只保留前 4 个字；不含「单」字 → 原样保留。
// 收口在 SaveChanges（对 Added / Modified 实体均生效），因此 9 个单据控制器
// （采购入库 / 生产入库 / 部门退料 / 仓库退料 / 产品报损 / 换货入库 / 仓库领料 /
// 部门领料 / 仓库盘点）以及盘点派生的盘盈、盘亏单等所有写入路径自动一致，
// 未来新增单据类型也无需逐点改。
// 注意：只在「落库」这一层规整，常量与界面展示的原文不受影响。
public partial class GonesPgDbContext
{
    /// <summary>「单」字（U+5355）。</summary>
    private const char Dan = '\u5355';

    /// <summary>
    /// 规整单据类型名：含「单」字 → 去掉「单」并截前 4 字；没有「单」字则不改动。
    /// 幂等：规整结果不再含「单」字，重复调用不变。
    /// </summary>
    public static string NormalizeBillTypeEx(string ex)
    {
        if (string.IsNullOrEmpty(ex)) return ex;
        if (ex.IndexOf(Dan) < 0) return ex;                                  // 没有「单」字 → 不改动
        var stripped = ex.Replace(Dan.ToString(), string.Empty);             // 去掉「单」字
        return stripped.Length > 4 ? stripped.Substring(0, 4) : stripped;    // 只要前 4 个字
    }

    private void ApplyBillTypeExNormalize()
    {
        foreach (var entry in ChangeTracker.Entries<t_PMS_StockBill>())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified) continue;
            var entity = entry.Entity;
            if (entity == null) continue;
            var normalized = NormalizeBillTypeEx(entity.FBillTypeEx);
            if (normalized != entity.FBillTypeEx)
                entity.FBillTypeEx = normalized;
        }
    }

    // 参数化重载是 EF Core 的「真正入口」：SaveChanges() / SaveChangesAsync() 内部
    // 都会转发到这两个重载，所以只需重写这两个即可覆盖全部落库调用。
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyBillTypeExNormalize();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyBillTypeExNormalize();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
