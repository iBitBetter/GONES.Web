using System;
using System.Collections.Generic;
using System.Linq;
using Furion.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using GONES.Model.Pg;

namespace GONES.Web.Services
{
    /// <summary>
    /// 仓库（depot）维度的数据范围，按请求生效。
    ///
    /// 权限源：<c>t_ERP_UserInfo.Depot</c> —— 以逗号分隔的 <c>t_ERP_Department.ID</c> 集合，
    /// 且这些行必须满足 <c>IsDEP = 1</c>（仓库）。首次使用时读库、请求内缓存。
    /// <b>刻意不做成 Claim</b>：<c>Depot</c> 运行期可被管理员修改，而现有的唯一 Cookie 失效机制
    /// <c>PwdStamp</c> 只跟踪口令——把 Depot 塞进凭据里就只有两个选择：再造一套失效逻辑，
    /// 或接受"改了权限必须重新登录"。<c>t_ERP_UserInfo</c> 是单行小表，每请求一次的代价可忽略。
    ///
    /// 语义（2026-09-12 裁定）：
    ///  - <b>IsAdmin 旁路</b>——由登录时写入的 <c>IsAdmin</c> claim 判定。这是<b>必须</b>而非特权：
    ///    admin 的 Depot 现值若不覆盖全部仓库，不旁路会让 admin 立刻看不到某些仓库的商品。
    ///  - <b>幽灵 ID 丢弃</b>——不属于 <c>IsDEP = 1</c> 的值（历史上出现过 63 这类已删仓库）直接丢弃并记日志。
    ///  - <b>fail-closed</b>——非 admin 且 Depot 为空/缺失 ⇒ 可见集合为空（不是"全部可见"）。
    ///  - 未认证/身份缺失一律 fail-closed；<b>不用 "id &gt; 0" 判存在</b>，因为本库 ID 非自增、可手工指定。
    /// </summary>
    public class DepotScope : IScoped
    {
        private readonly GonesPgDbContext _db;
        private readonly IHttpContextAccessor _http;
        private readonly ILogger<DepotScope> _logger;

        private bool _loaded;
        private bool _isAdmin;
        private HashSet<int> _ids;
        private List<int> _dropped;

        public DepotScope(GonesPgDbContext db, IHttpContextAccessor http, ILogger<DepotScope> logger)
        {
            _db = db;
            _http = http;
            _logger = logger;
        }

        // ---------------------------------------------------------------- resolution

        private void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _ids = new HashSet<int>();
            _dropped = new List<int>();

            var principal = _http?.HttpContext?.User;
            if (principal?.Identity == null || !principal.Identity.IsAuthenticated) return;

            // admin 旁路：IsAdmin claim 在登录时由 MenuService.IsAdmin 写入，是权威判据。
            if (principal.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1"))
            {
                _isAdmin = true;
                return;
            }

            // 身份判存在必须用 HasClaim，不能用 "userId > 0" —— 本库 ID 非自增，0 亦为合法值（曾出现过）。
            var rawUserId = principal.FindFirst("UserId")?.Value;
            if (rawUserId == null) return;                       // 无身份 claim ⇒ fail-closed
            int userId;
            if (!int.TryParse(rawUserId, out userId)) return;

            var user = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == userId);
            if (user == null) return;                            // 用户已删但 Cookie 仍有效 ⇒ fail-closed

            // 只有 IsDEP=1 才是仓库（0 部门 / 2 车间 / 3 班组 都不是）。
            var valid = new HashSet<int>(_db.t_ERP_Department
                .Where(d => d.IsDEP == 1)
                .Select(d => d.ID));

            foreach (var part in (user.Depot ?? "").Split(','))
            {
                int id;
                if (!int.TryParse(part.Trim(), out id)) continue;
                if (valid.Contains(id)) _ids.Add(id);
                else _dropped.Add(id);
            }

            if (_dropped.Count > 0)
            {
                _logger.LogWarning("DepotScope: user {Account} (ID={UserId}) has non-warehouse Depot id(s) dropped: {Dropped}",
                    user.Account, user.ID, string.Join(",", _dropped));
            }
        }

        // ---------------------------------------------------------------- primitives

        /// <summary>true = 该请求受仓库限制（非 admin）。admin 恒 false，等价于"不限制"。</summary>
        public bool IsRestricted { get { Load(); return !_isAdmin; } }

        /// <summary>当前的仓库集合。admin 返回全量仓库，便于调用方统一处理"仅可见行"口径。</summary>
        public IReadOnlyCollection<int> DepotIds
        {
            get
            {
                Load();
                if (_isAdmin) return _db.t_ERP_Department.Where(d => d.IsDEP == 1).Select(d => d.ID).ToList();
                return _ids.ToList();
            }
        }

        /// <summary>非 admin 且未配置任何仓库 ⇒ 界面须显示"未配置仓库"提示（fail-closed）。</summary>
        public bool HasNoDepot { get { Load(); return !_isAdmin && _ids.Count == 0; } }

        /// <summary>被丢弃的幽灵仓库 ID（诊断用）。</summary>
        public IReadOnlyCollection<int> DroppedIds { get { Load(); return _dropped.ToList(); } }

        public bool Allows(int? ckid)
        {
            Load();
            if (_isAdmin) return true;
            return ckid.HasValue && _ids.Contains(ckid.Value);
        }

        public bool AllowsItem(int itemId)
        {
            Load();
            if (_isAdmin) return true;
            var ids = _ids.ToList();
            return _db.t_ERP_ITEM.Any(i => i.ID == itemId && i.WarehouseId != null && ids.Contains(i.WarehouseId.Value));
        }

        /// <summary>提交的一组商品是否全部落在范围内（新建/编辑表单校验用）。空集合视为"无约束"。</summary>
        public bool AllowsAllItems(IEnumerable<int> itemIds)
        {
            Load();
            if (_isAdmin) return true;
            var wanted = (itemIds ?? Enumerable.Empty<int>()).Where(i => i > 0).Distinct().ToList();
            if (wanted.Count == 0) return true;
            // ckid 在模型里可空：归仓为空的行无法证明归属 ⇒ 视为越权（fail-closed）。
            var ckids = _db.t_ERP_ITEM.Where(i => wanted.Contains(i.ID)).Select(i => i.WarehouseId).Distinct().ToList();
            return ckids.Count > 0 && ckids.All(c => c.HasValue && _ids.Contains(c.Value));
        }

        /// <summary>
        /// 整单级（W1）判定：整单明细全部落在范围内才可编辑/审核/反审核/删除。
        /// 空单（无明细）放行——那是用户新建的草稿。FItemID 为 NULL 的行不参与判定（无仓库可主张）。
        /// </summary>
        public bool BillFullyInScope(int interId)
        {
            Load();
            if (_isAdmin) return true;

            var rows = _db.t_PMS_StockBillEntry
                .Where(e => e.FInterID == interId)
                .Select(e => e.FItemID)
                .ToList();
            var itemIds = rows.Where(x => x.HasValue).Select(x => x.Value).Distinct().ToList();
            if (itemIds.Count == 0) return true;

            var ckids = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).Select(i => i.WarehouseId).Distinct().ToList();
            return ckids.Count > 0 && ckids.All(c => c.HasValue && _ids.Contains(c.Value));
        }

        /// <summary>
        /// 批量版 W1：返回「明细未全部落在范围内」的单据 id 集合，供列表页灰掉修改/审核/删除按钮。
        /// 与服务端硬拦共用同一条规则——列表的状态与服务端的判定必须同源，否则就会出现
        /// "按钮可点但一提交就被拒"或更糟的"按钮灰着却能被构造 POST 绕过"。
        /// 口径逐条对齐 <see cref="BillFullyInScope"/>：无明细/无商品行 ⇒ 可动；有商品但主档查不到 ⇒ 不可动。
        /// </summary>
        public HashSet<int> BillsNotFullyInScope(IEnumerable<int> interIds)
        {
            var notOk = new HashSet<int>();
            Load();
            if (_isAdmin) return notOk;

            var ids = (interIds ?? Enumerable.Empty<int>()).Distinct().ToList();
            if (ids.Count == 0) return notOk;

            var rows = _db.t_PMS_StockBillEntry
                .Where(e => ids.Contains(e.FInterID))
                .Select(e => new { e.FInterID, e.FItemID })
                .ToList();

            var itemIds = rows.Where(r => r.FItemID.HasValue).Select(r => r.FItemID.Value).Distinct().ToList();
            var ckidOf = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID))
                .Select(i => new { i.ID, i.WarehouseId }).ToDictionary(x => x.ID, x => x.WarehouseId);

            foreach (var g in rows.GroupBy(r => r.FInterID))
            {
                var its = g.Where(r => r.FItemID.HasValue).Select(r => r.FItemID.Value).Distinct().ToList();
                if (its.Count == 0) continue;                      // 无商品行 ⇒ 可动
                if (its.Any(iid => !ckidOf.ContainsKey(iid)))       // 有商品却查不到主档 ⇒ 不可动（fail-closed）
                {
                    notOk.Add(g.Key);
                    continue;
                }
                if (!its.All(iid => ckidOf[iid].HasValue && _ids.Contains(ckidOf[iid].Value)))
                    notOk.Add(g.Key);
            }
            return notOk;
        }

        // ---------------------------------------------------------------- query composers

        /// <summary>把商品查询收窄到本仓库。</summary>
        public IQueryable<t_ERP_ITEM> FilterItems(IQueryable<t_ERP_ITEM> q)
        {
            Load();
            if (_isAdmin) return q;
            var ids = _ids.ToList();
            return q.Where(i => i.WarehouseId != null && ids.Contains(i.WarehouseId.Value));
        }

        /// <summary>把单据明细查询收窄到本仓库的商品行。</summary>
        public IQueryable<t_PMS_StockBillEntry> FilterEntries(IQueryable<t_PMS_StockBillEntry> q)
        {
            Load();
            if (_isAdmin) return q;
            var ids = _ids.ToList();
            return q.Where(e => e.FItemID != null
                && _db.t_ERP_ITEM.Any(i => i.ID == e.FItemID.Value && i.WarehouseId != null && ids.Contains(i.WarehouseId.Value)));
        }

        /// <summary>
        /// 把单据表头查询收窄到"至少有一行明细属于本仓库"的单据（行级读的口径：
        /// 一张单只要我有它的某一行，就该看得到它——否则那行将无处可显）。
        /// </summary>
        public IQueryable<t_PMS_StockBill> FilterBills(IQueryable<t_PMS_StockBill> q)
        {
            Load();
            if (_isAdmin) return q;
            var visible = FilterEntries(_db.t_PMS_StockBillEntry).Select(e => e.FInterID);
            return q.Where(h => visible.Contains(h.FInterID));
        }
    }
}
