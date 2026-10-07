using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;
using GONES.Web.Services;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// Goods apply plan (requisition) - old WinForms Stock.FrmGetGoodsApplyManager / FrmGetGoodsApplyEdit.
    /// Header t_PMS_GoodsApply + entries t_PMS_GoodsApplyEntry.
    ///
    /// New-world rules (user decisions 2026-09-06):
    ///   - Items come from t_ERP_ITEM; an item can be requested ONLY if it already has a BOM,
    ///     i.e. it is an output row (FIsProduct=1) in t_PMS_StepProductBom -> entry.FBomId.
    ///   - Three-level cascade (user decision 2026-09-08): applicant department (FDeptID,
    ///     IsDEP=0) -> workshop/sub-department (FWorkShopID, IsDEP!=0, child of the dept)
    ///     -> applicant (FEmpID from t_PMS_Worker under that workshop). Shipping warehouse
    ///     (FStockID) was removed from the form: null on create, preserved on edit.
    ///   - FPrice defaults to item cbdj; FAmount = Qty * Price (server-side computed).
    ///   - FUnitID resolved from unit dict (t_ERP_DataDict FParentID=5) by item wljbdw name.
    ///   - FSecCoefficient / FSecQty = 0 (no conversion-rate source in new item table).
    ///
    /// Old rules kept: FTranType=83; BillNo "JHD"+8 digits (max+1); FInterID max+1;
    /// FStatus 0=draft 1=audited 2=done; audit sets FCheckerID+FCheckDate; entries FEntryID=1..n;
    /// qty must be > 0; FetchDate >= today; duplicate items rejected.
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // standard MVC form redisplay, skip Furion 400 short-circuit
    public class GoodsApplyController : Controller, IMenuGuarded
    {
        private const short StatusDraft = 0;
        private const short StatusAudited = 1;
        private const short StatusDone = 2;
        private const short TranType = 83;


        private readonly GonesPgDbContext _db;
        private readonly MenuService _menu;
        private readonly DepotScope _depot;
        public GoodsApplyController(GonesPgDbContext db, MenuService menu, DepotScope depot)
        {
            _db = db;
            _menu = menu;
            _depot = depot;
        }


        /// <summary>整单明细未全部落在当前用户管理的仓库内时的提示语（与 8 个单据模块同源）。</summary>
        private const string OutOfScopeMsg = "\u8be5\u5355\u636e\u5305\u542b\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u4ed3\u5e93\u7684\u660e\u7ec6\uff0c\u7981\u6b62\u64cd\u4f5c\uff01";   // 该单据包含不属于您管理仓库的明细，禁止操作！

        /// <summary>
        /// 整单是否全部落在当前用户的仓库范围内（admin 恒 true）。
        /// 要货计划用的是 t_PMS_GoodsApplyEntry（不是 t_PMS_StockBillEntry），
        /// 故不能用 DepotScope.BillFullyInScope，改为按明细商品 ckid 判定。
        /// </summary>
        private bool InDepotScope(int id)
        {
            var itemIds = _db.t_PMS_GoodsApplyEntry
                .Where(e => e.FInterID == id && e.FItemID != null)
                .Select(e => e.FItemID.Value)
                .ToList();
            return _depot.AllowsAllItems(itemIds);
        }

        private int CurrentUserId
        {
            get
            {
                int v;
                int.TryParse(User.FindFirst("UserId")?.Value, out v);
                return v;
            }
        }

        // ------------------------------------------------------------ list

        // GET /GoodsApply?dateFrom=&dateTo=&billNo=&page=&pageSize=
        public IActionResult Index(DateTime? dateFrom, DateTime? dateTo, string billNo,
            int page = 1, int pageSize = 20)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);

            billNo = billNo?.Trim();

            // Filter on the HEADER only, then page by BILL (not by entry row):
            // the grid is flat (one row per entry) but a bill must never be split
            // across two pages, otherwise its header (bill no / date / applicant)
            // would be repeated or lost.
            var hq = _db.t_PMS_GoodsApply.AsQueryable();
            if (dateFrom.HasValue)
            {
                var d = dateFrom.Value.Date;
                hq = hq.Where(h => h.FDate >= d);
            }
            if (dateTo.HasValue)
            {
                var d = dateTo.Value.Date.AddDays(1);   // inclusive upper bound
                hq = hq.Where(h => h.FDate < d);
            }
            if (!string.IsNullOrEmpty(billNo)) hq = hq.Where(h => h.FBillNo.Contains(billNo));

            // 仓库数据权限（读收窄，W1）：非 admin 只能看到"至少有一行明细属于本仓库"的单据。
            // 行级读放得宽（那行否则无处可显），写操作由下面的 depotLocked + 服务端硬拦整套锁死。
            if (_depot.IsRestricted)
            {
                var depotIds = _depot.DepotIds;
                var visible = _db.t_PMS_GoodsApplyEntry
                    .Where(e => e.FItemID != null
                        && _db.t_ERP_ITEM.Any(i => i.ID == e.FItemID.Value && i.WarehouseId != null && depotIds.Contains(i.WarehouseId.Value)))
                    .Select(e => e.FInterID);
                hq = hq.Where(h => visible.Contains(h.FInterID));
            }

            int total = hq.Count();                     // number of BILLS, not rows
            page = PagerViewModel.ClampPage(page, total, pageSize);
            var headers = hq.OrderByDescending(h => h.FInterID)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();
            var headerIds = headers.Select(h => h.FInterID).ToList();

            var entries = _db.t_PMS_GoodsApplyEntry
                .Where(e => headerIds.Contains(e.FInterID))
                .OrderBy(e => e.FInterID)
                .ThenBy(e => e.FEntryID)
                .ToList();
            var entriesByBill = entries.GroupBy(e => e.FInterID)
                .ToDictionary(g => g.Key, g => g.ToList());

            // 已审核单据是否已有产品被生产单引入（计划占用口径，与剩余量/状态回写同源）。
            // 决定列表「反审核」按钮是否置灰——避免用户点击才被服务端拦截。
            var auditedKeys = entriesByBill
                .Where(kv => headers.Any(h => h.FInterID == kv.Key && h.FStatus == StatusAudited))
                .SelectMany(kv => kv.Value.Select(e => (e.FInterID, e.FEntryID)))
                .ToList();
            var auditedUsedMap = StockService.UsedPlanByApplyEntryIds(_db, auditedKeys, null);
            var headerCanUnAudit = new Dictionary<int, bool>();
            foreach (var h in headers.Where(x => x.FStatus == StatusAudited))
            {
                var hid = h.FInterID;
                var eids = entriesByBill.ContainsKey(hid)
                    ? entriesByBill[hid].Select(e => e.FEntryID)
                    : Enumerable.Empty<int>();
                headerCanUnAudit[hid] = !eids.Any(id => auditedUsedMap.ContainsKey((hid, id)) && auditedUsedMap[(hid, id)] > 0m);
            }

            // 仓库数据权限③-b（按钮灰化）：与服务端 InDepotScope 硬拦同源。
            // 一次查询拿到全部商品的 ckid，避免逐单往返。
            var depotLocked = new HashSet<int>();
            if (_depot.IsRestricted)
            {
                var scoped = entriesByBill.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList());
                var allScopedIds = scoped.Values.SelectMany(x => x).Distinct().ToList();
                var ckidOf = _db.t_ERP_ITEM.Where(i => allScopedIds.Contains(i.ID))
                    .Select(i => new { i.ID, i.WarehouseId })
                    .ToDictionary(x => x.ID, x => x.WarehouseId);
                var depotIds = _depot.DepotIds.ToHashSet();
                foreach (var kv in scoped)
                {
                    if (kv.Value.Count == 0) continue;                       // 无商品行 ⇒ 可动
                    // 商品查不到主档、或 ckid 为空、或不在本仓集合 ⇒ 不可动（fail-closed）
                    if (!kv.Value.All(iid => ckidOf.ContainsKey(iid)
                                             && ckidOf[iid].HasValue
                                             && depotIds.Contains(ckidOf[iid].Value)))
                        depotLocked.Add(kv.Key);
                }
            }

            var itemIds = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM
                .Where(i => itemIds.Contains(i.ID))
                .ToDictionary(i => i.ID);
            var workers = _db.t_PMS_Worker.ToDictionary(w => w.ID, w => w.FName);
            var depts = _db.t_ERP_Department.ToDictionary(d => d.ID, d => d.DEPName);
            var units = _db.t_ERP_DataDict
                .Where(d => d.FParentID == 5)
                .ToDictionary(d => d.ID, d => d.DictName);

            string DeptName(int? id) => (id != null && depts.ContainsKey(id.Value)) ? depts[id.Value] : "";
            string UnitName(int? id) => (id != null && units.ContainsKey(id.Value)) ? units[id.Value] : "";

            var rows = new List<GoodsApplyListRow>();
            foreach (var h in headers)
            {
                var hid = h.FInterID;
                var billEntries = entriesByBill.ContainsKey(hid) ? entriesByBill[hid] : new List<t_PMS_GoodsApplyEntry>();

                var first = true;
                if (billEntries.Count == 0)
                {
                    // defensive: bill without entries still shows one header-only row
                    rows.Add(new GoodsApplyListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate,
                        DeptName = DeptName(h.FDeptID), EmpName = "", Status = h.FStatus,
                        Cancelled = h.FCancellation, Explanation = h.FExplanation, FirstOfBill = true,
                        CanUnAudit = true,
                        CanModify = !depotLocked.Contains(hid),
                    });
                    continue;
                }
                foreach (var e in billEntries)
                {
                    t_ERP_ITEM it = null;
                    if (e.FItemID != null && items.ContainsKey(e.FItemID.Value)) it = items[e.FItemID.Value];
                    rows.Add(new GoodsApplyListRow
                    {
                        InterId = h.FInterID,
                        BillNo = h.FBillNo,
                        Date = h.FDate,
                        DeptName = DeptName(h.FDeptID),
                        EmpName = (h.FEmpID != null && workers.ContainsKey(h.FEmpID.Value)) ? workers[h.FEmpID.Value] : "",
                        Status = h.FStatus,
                        Cancelled = h.FCancellation,
                        Explanation = h.FExplanation,
                        FirstOfBill = first,
                        CanUnAudit = (h.FStatus == StatusAudited)
                            ? (headerCanUnAudit.ContainsKey(hid) ? headerCanUnAudit[hid] : true)
                            : true,
                        CanModify = !depotLocked.Contains(hid),
                        EntryId = e.FEntryID,
                        BomId = e.FBomId,
                        Cplb = it?.ProductCategory ?? "",
                        Cpjc = it?.ItemShortName ?? "",
                        Cpgg = it?.ItemSpec ?? "",
                        UnitName = UnitName(e.FUnitID),
                        Qty = e.FQty,
                        FetchNum = e.FFetchNum,
                        FetchDate = e.FFetchDate,
                        Note = e.FNote,
                    });
                    first = false;
                }
            }

            ViewBag.Filter = new Dictionary<string, string>
            {
                ["dateFrom"] = dateFrom?.ToString("yyyy-MM-dd") ?? "",
                ["dateTo"] = dateTo?.ToString("yyyy-MM-dd") ?? "",
                ["billNo"] = billNo ?? "",
            };

            var extra = new Dictionary<string, object>();
            if (dateFrom.HasValue) extra["dateFrom"] = dateFrom.Value.ToString("yyyy-MM-dd");
            if (dateTo.HasValue) extra["dateTo"] = dateTo.Value.ToString("yyyy-MM-dd");
            if (!string.IsNullOrEmpty(billNo)) extra["billNo"] = billNo;
            ViewBag.Pager = new PagerViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                Action = "Index",
                ExtraValues = extra.Count > 0 ? extra : null,
            };

            return View(rows);
        }

        // GET /GoodsApply/BomItems —— all active BOM-output products, for the
        // "from BOM" button that bulk-lists finished products in the entry grid.
        // Each item carries its BOM's step (FStepId/FStepName) so the modal can
        // default-filter to the packaging step client-side.
        [HttpGet]
        public IActionResult BomItems()
        {

            var bomRows = _db.t_PMS_StepProductBom
                .Where(b => b.FIsProduct == true && b.FDelete == 0 && b.FPItemID != null)
                .Select(b => new { b.FPItemID, b.FBomID, b.FStepId, b.FStepName })
                .ToList();
            // item -> latest BOM row (same Max(FBomID) semantics as LoadBomMap)
            var rowMap = bomRows
                .GroupBy(b => b.FPItemID.Value)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.FBomID).First());

            // step id -> name (FStepName may be blank on legacy rows)
            // ⚠ PG/Npgsql 修复（2026-09-13）：GroupBy 直接接 ToDictionary(g => g.First()...) 会让
            //   Npgsql 在 ApplyProjection 里 AddRange(null) 崩 500（提供程序缺陷）。改为先取数、
            //   客户端分组 —— t_PMS_Step 是小表，语义与原「GroupBy-then-First 去重遗留行」一致。
            var stepRows = _db.t_PMS_Step
                .Where(s => s.FDelete == 0)
                .Select(s => new { s.FItemID, s.FName })
                .ToList();
            var stepNames = stepRows
                .GroupBy(s => s.FItemID)
                .ToDictionary(g => g.Key, g => g.First().FName ?? "");

            // Fetch ONLY the items that actually appear as BOM outputs: the item table
            // is far larger than the BOM output set, and pulling it whole just to filter
            // it in memory (previous version) would not survive real data volumes.
            var itemIds = rowMap.Keys.ToList();
            if (itemIds.Count == 0) return Json(new List<object>());

            // Chunked: SQL Server caps a single statement at ~2100 parameters, so one
            // giant Contains() would start failing (error 8003) once the product range grows.
            var items = new List<t_ERP_ITEM>();
            for (int skip = 0; skip < itemIds.Count; skip += 1000)
            {
                var chunk = itemIds.Skip(skip).Take(1000).ToList();
                // 数据闸门②-c（选择器）：BOM 产出弹窗只能选到本仓库的商品。
                items.AddRange(_depot.FilterItems(_db.t_ERP_ITEM).Where(i => i.IsEnabled == true && chunk.Contains(i.ID)));
            }

            var list = items
                .OrderBy(i => i.ItemCode)
                .Select(i =>
                {
                    var row = rowMap[i.ID];
                    var stepName = (row.FStepName ?? "");
                    if (stepName == "" && row.FStepId != null && stepNames.ContainsKey(row.FStepId.Value))
                        stepName = stepNames[row.FStepId.Value];
                    return new
                    {
                        id = i.ID,
                        cpbm = i.ItemCode,
                        cpjc = i.ItemShortName,
                        cpgg = i.ItemSpec,
                        cplb = i.ProductCategory,
                        unit = i.BaseUnit,
                        bomId = row.FBomID ?? 0,
                        price = i.CostPrice ?? 0m,
                        stepId = row.FStepId ?? 0,
                        stepName = stepName,
                    };
                })
                .ToList();
            return Json(list);
        }

        // ------------------------------------------------------------ create

        // GET /GoodsApply/Create
        [HttpGet]
        public IActionResult Create()
        {

            BindFormExtras();
            return View("Form", new GoodsApplyEditViewModel
            {
                BillNo = NextBillNo(),
                Date = DateTime.Today,
                Rows = new List<GoodsApplyRowInput>(),
            });
        }

        // POST /GoodsApply/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(GoodsApplyEditViewModel vm)
        {

            var worker = FindWorker(vm.EmpId);
            if (worker == null) ModelState.AddModelError(string.Empty, "\u8bf7\u9009\u62e9\u7533\u8bf7\u4eba\uff01");   // choose applicant
            ValidateCascade(vm, worker);   // dept -> workshop -> applicant chain

            var items = LoadItems(vm);
            var bomMap = LoadBomMap();
            var billOk = ValidateBill(vm, items, bomMap);
            if (!ModelState.IsValid || !billOk)
            {
                BindFormExtras();
                EnsureDeptOption(vm.DeptId);
                BindRowInfo(vm);
                return View("Form", vm);
            }

            // Generate bill no + inter id AND persist under one lock so concurrent
            // creates cannot read the same max and collide on the primary key.
            lock (BillNoLock)
            {
                var header = new t_PMS_GoodsApply
                {
                    FInterID = NextInterId(),
                    FBillNo = NextBillNo(),          // regenerate on save (old system behavior)
                    FTranType = TranType,
                    FDate = vm.Date,
                    FStockID = null,                 // shipping warehouse field removed (user decision 2026-09-08)
                    FEmpID = vm.EmpId,
                    FDeptID = vm.DeptId,             // level-1 applicant department (cascade select)
                    FCheckerID = 0,
                    FBillerID = CurrentUserId,
                    FStatus = StatusDraft,
                    FCancellation = false,
                    FExplanation = vm.Explanation ?? "",   // csdl marks FExplanation/FFetchAdd NonNullable
                    FFetchAdd = "",
                    FWorkShopID = vm.WorkShopId,     // level-2 sub-department (workshop/warehouse/team)
                    FModifyerID = null,
                    FModifyDate = null,
                    FUserID = null,
                };
                SaveBill(vm, header, items, bomMap);
            }

            TempData["Success"] = "\u8981\u8d27\u8ba1\u5212\u5355\u4fdd\u5b58\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ edit

        // GET /GoodsApply/Edit/5 (FInterID)
        [HttpGet]
        public IActionResult Edit(int id)
        {

            var header = _db.t_PMS_GoodsApply.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            if (header.FStatus != StatusDraft)
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\u6216\u5df2\u5b8c\u6210\uff0c\u7981\u6b62\u4fee\u6539\uff01";
                return RedirectToAction(nameof(Index));
            }
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }

            var rows = _db.t_PMS_GoodsApplyEntry
                .Where(e => e.FInterID == id)
                .OrderBy(e => e.FEntryID)
                .ToList()
                .Select(e => new GoodsApplyRowInput
                {
                    ItemId = e.FItemID ?? 0,
                    Qty = e.FQty,
                    Price = e.FPrice,
                    FetchDate = e.FFetchDate,
                    Note = e.FNote,
                })
                .Where(r => r.ItemId > 0)
                .ToList();
            var vm = new GoodsApplyEditViewModel
            {
                InterId = header.FInterID,
                BillNo = header.FBillNo,
                Date = header.FDate ?? DateTime.Today,
                EmpId = header.FEmpID,
                DeptId = header.FDeptID,
                StockId = header.FStockID,
                WorkShopId = header.FWorkShopID,
                Explanation = header.FExplanation,
                Status = header.FStatus,
                Rows = rows,
            };
            // Legacy drafts may carry FDeptID = worker.DEPID (a workshop/warehouse id,
            // not a level-1 dept). Derive the dept from the workshop's parent so the
            // three-level cascade preselects correctly -- but ONLY when the stored
            // FDeptID is not already a valid level-1 department: a mismatched workshop
            // (dept=production but workshop=finished-goods warehouse, as in legacy row 1)
            // must not silently rewrite the department the operator actually saved.
            var deptValid = vm.DeptId != null && vm.DeptId.Value > 0
                && _db.t_ERP_Department.Any(d => d.ID == vm.DeptId.Value && d.IsDEP == 0 && d.Status == 1);
            if (!deptValid && header.FWorkShopID != null)
            {
                var ws = _db.t_ERP_Department.FirstOrDefault(d => d.ID == header.FWorkShopID.Value);
                if (ws != null && ws.ParentID > 0) vm.DeptId = ws.ParentID;
            }
            BindFormExtras();
            EnsureDeptOption(vm.DeptId);
            BindRowInfo(vm);
            return View("Form", vm);
        }

        // POST /GoodsApply/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, GoodsApplyEditViewModel vm)
        {

            var header = _db.t_PMS_GoodsApply.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            if (header.FStatus != StatusDraft)
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\u6216\u5df2\u5b8c\u6210\uff0c\u7981\u6b62\u4fee\u6539\uff01";
                return RedirectToAction(nameof(Index));
            }
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }

            vm.InterId = id;
            vm.BillNo = header.FBillNo;   // bill no is immutable

            var worker = FindWorker(vm.EmpId);
            if (worker == null) ModelState.AddModelError(string.Empty, "\u8bf7\u9009\u62e9\u7533\u8bf7\u4eba\uff01");
            ValidateCascade(vm, worker);   // dept -> workshop -> applicant chain

            var items = LoadItems(vm);
            var bomMap = LoadBomMap();
            var billOk = ValidateBill(vm, items, bomMap);
            if (!ModelState.IsValid || !billOk)
            {
                BindFormExtras();
                EnsureDeptOption(vm.DeptId);
                BindRowInfo(vm);
                return View("Form", vm);
            }

            header.FDate = vm.Date;
            // header.FStockID intentionally NOT updated: shipping warehouse was removed
            // from the form (user decision 2026-09-08), keep the stored value.
            header.FWorkShopID = vm.WorkShopId;
            header.FEmpID = vm.EmpId;
            header.FDeptID = vm.DeptId;
            header.FExplanation = vm.Explanation ?? "";
            header.FModifyerID = CurrentUserId;
            header.FModifyDate = DateTime.Now;
            SaveBill(vm, header, items, bomMap);

            TempData["Success"] = "\u8981\u8d27\u8ba1\u5212\u5355\u4fee\u6539\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index));
        }

        // GET /GoodsApply/Details/5  (read-only detail page for audited/completed bills)
        public IActionResult Details(int id)
        {

            var header = _db.t_PMS_GoodsApply.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            // 只读查看同样受仓库闸门约束：否则"列表里看不到"的单据可被直接拼 URL 看全。
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }

            var rows = _db.t_PMS_GoodsApplyEntry
                .Where(e => e.FInterID == id)
                .OrderBy(e => e.FEntryID)
                .ToList()
                .Select(e => new GoodsApplyRowInput
                {
                    ItemId = e.FItemID ?? 0,
                    Qty = e.FQty,
                    Price = e.FPrice,
                    FetchDate = e.FFetchDate,
                    Note = e.FNote,
                })
                .Where(r => r.ItemId > 0)
                .ToList();
            var vm = new GoodsApplyEditViewModel
            {
                InterId = header.FInterID,
                BillNo = header.FBillNo,
                Date = header.FDate ?? DateTime.Today,
                EmpId = header.FEmpID,
                DeptId = header.FDeptID,
                StockId = header.FStockID,
                WorkShopId = header.FWorkShopID,
                Explanation = header.FExplanation,
                Status = header.FStatus,
                Rows = rows,
            };
            // Same legacy-dept fix as Edit so the cascade preselects correctly.
            var deptValid = vm.DeptId != null && vm.DeptId.Value > 0
                && _db.t_ERP_Department.Any(d => d.ID == vm.DeptId.Value && d.IsDEP == 0 && d.Status == 1);
            if (!deptValid && header.FWorkShopID != null)
            {
                var ws = _db.t_ERP_Department.FirstOrDefault(d => d.ID == header.FWorkShopID.Value);
                if (ws != null && ws.ParentID > 0) vm.DeptId = ws.ParentID;
            }
            ViewBag.ReadOnly = true;
            BindFormExtras();
            EnsureDeptOption(vm.DeptId);
            BindRowInfo(vm);
            return View("Form", vm);
        }

        // ------------------------------------------------------------ delete (whole bill)

        // POST /GoodsApply/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {

            var header = _db.t_PMS_GoodsApply.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            if (header.FStatus != StatusDraft)
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\u6216\u5df2\u5b8c\u6210\uff0c\u7981\u6b62\u5220\u9664\uff01";
                return RedirectToAction(nameof(Index));
            }
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            // Defense in depth (GoodsApply_DEL trigger dropped 2026-09-09, these checks are
            // now the only audited/voided guard): they key on FCheckerID / FCancellation,
            // not FStatus -- a draft row that still carries a checker id would pass the
            // check above and must still be refused.
            if (header.FCheckerID != null && header.FCheckerID.Value != 0)
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff08\u5b58\u5728\u5ba1\u6838\u4eba\uff09\uff0c\u7981\u6b62\u5220\u9664\uff01";   // audited (has a checker), delete forbidden
                return RedirectToAction(nameof(Index));
            }
            if (header.FCancellation)
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u4f5c\u5e9f\uff0c\u7981\u6b62\u5220\u9664\uff01";   // voided, delete forbidden
                return RedirectToAction(nameof(Index));
            }

            // GoodsApply_DEL trigger dropped (2026-09-09): its audited/voided guard now lives
            // ONLY in the checks above, and entry cleanup is app-owned -- so entries + header
            // go in a single SaveChanges inside the transaction.
            using (var tx = _db.Database.BeginTransaction())
            {
                var entries = _db.t_PMS_GoodsApplyEntry.Where(e => e.FInterID == id).ToList();
                _db.t_PMS_GoodsApplyEntry.RemoveRange(entries);
                _db.t_PMS_GoodsApply.Remove(header);
                _db.SaveChanges();
                tx.Commit();
            }

            TempData["Success"] = "\u8981\u8d27\u8ba1\u5212\u5355\u5220\u9664\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ audit / unaudit

        // POST /GoodsApply/Audit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Audit(int id)
        {

            var header = _db.t_PMS_GoodsApply.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            if (header.FStatus == StatusDone)
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5b8c\u6210\uff0c\u7981\u6b62\u64cd\u4f5c\uff01";
                return RedirectToAction(nameof(Index));
            }
            if (header.FStatus != StatusDraft)
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff0c\u65e0\u9700\u91cd\u590d\u5ba1\u6838\uff01";
                return RedirectToAction(nameof(Index));
            }
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }

            // An empty bill must never reach the audited state: the production-order
            // module imports audited requisitions (FStatus=1) and would build empty orders.
            if (!_db.t_PMS_GoodsApplyEntry.Any(e => e.FInterID == id))
            {
                TempData["Error"] = "\u5355\u636e\u6ca1\u6709\u660e\u7ec6\u884c\uff0c\u4e0d\u80fd\u5ba1\u6838\uff01";   // bill has no entries, cannot audit
                return RedirectToAction(nameof(Index));
            }

            header.FStatus = StatusAudited;
            header.FCheckerID = CurrentUserId;
            header.FCheckDate = DateTime.Now;
            _db.SaveChanges();

            TempData["Success"] = "\u5ba1\u6838\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index));
        }

        // POST /GoodsApply/UnAudit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UnAudit(int id)
        {

            var header = _db.t_PMS_GoodsApply.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            if (header.FStatus != StatusAudited)
            {
                TempData["Error"] = "\u5355\u636e\u672a\u5ba1\u6838\uff0c\u65e0\u9700\u53cd\u5ba1\u6838\uff01";
                return RedirectToAction(nameof(Index));
            }
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }

            // 下游占用守卫（用户裁定 2026-09-10）：只要有一款产品已被生产单引入，就禁止反审核，
            // 避免生产单引用的要货行变成悬空。复用 StockService「计划占用」单一真相源（含草稿）。
            var entryKeys = _db.t_PMS_GoodsApplyEntry
                .Where(e => e.FInterID == id)
                .Select(e => new { e.FInterID, e.FEntryID })
                .ToList()
                .Select(e => (e.FInterID, e.FEntryID))
                .ToList();
            if (entryKeys.Count > 0)
            {
                var usedMap = StockService.UsedPlanByApplyEntryIds(_db, entryKeys, null);
                if (entryKeys.Any(k => usedMap.ContainsKey(k) && usedMap[k] > 0m))
                {
                    TempData["Error"] = "\u8981\u8d27\u5355\u5b58\u5728\u5df2\u5bfc\u5165\u751f\u4ea7\u5355\u7684\u4ea7\u54c1\uff0c\u7981\u6b62\u53cd\u5ba1\u6838\uff01";
                    return RedirectToAction(nameof(Index));
                }
            }

            header.FStatus = StatusDraft;
            header.FCheckerID = 0;
            header.FCheckDate = null;
            _db.SaveChanges();

            TempData["Success"] = "\u53cd\u5ba1\u6838\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ helpers

        /// <summary>itemId -> BomId, from BOM output rows (FIsProduct=1, FDelete=0); duplicated outputs take max BomId.</summary>
        private Dictionary<int, int> LoadBomMap()
        {
            return _db.t_PMS_StepProductBom
                .Where(b => b.FIsProduct == true && b.FDelete == 0 && b.FPItemID != null)
                .GroupBy(b => b.FPItemID.Value)
                .Select(g => new { ItemId = g.Key, BomId = g.Max(x => x.FBomID) ?? 0 })
                .ToDictionary(x => x.ItemId, x => x.BomId);
        }

        /// <summary>unit name -> dict id (t_ERP_DataDict FParentID=5).</summary>
        private Dictionary<string, int> LoadUnitMap()
        {
            return _db.t_ERP_DataDict
                .Where(d => d.FParentID == 5 && d.DictName != null && d.DictName != "")
                .ToList()
                .GroupBy(d => d.DictName.Trim())
                .ToDictionary(g => g.Key, g => g.First().ID);
        }

        private t_PMS_Worker FindWorker(int? empId)
        {
            if (empId == null || empId.Value <= 0) return null;
            return _db.t_PMS_Worker.FirstOrDefault(w => w.ID == empId.Value && w.Status == true);
        }

        /// <summary>Load posted entry items from t_ERP_ITEM.</summary>
        private Dictionary<int, t_ERP_ITEM> LoadItems(GoodsApplyEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<GoodsApplyRowInput>())
                .Where(r => r.ItemId > 0)
                .Select(r => r.ItemId)
                .Distinct()
                .ToList();
            return _db.t_ERP_ITEM.Where(i => ids.Contains(i.ID)).ToDictionary(i => i.ID);
        }

        /// <summary>Business validation, aligned with the old form + BOM-required rule.</summary>
        private bool ValidateBill(GoodsApplyEditViewModel vm, Dictionary<int, t_ERP_ITEM> items, Dictionary<int, int> bomMap)
        {
            var rows = (vm.Rows ?? new List<GoodsApplyRowInput>()).Where(r => r.ItemId > 0).ToList();

            if (rows.Count == 0)
            {
                ModelState.AddModelError("Rows", "\u8bf7\u81f3\u5c11\u6dfb\u52a0\u4e00\u4e2a\u5546\u54c1\uff01");
                return false;
            }

            if (rows.Select(r => r.ItemId).Distinct().Count() != rows.Count)
            {
                ModelState.AddModelError("Rows", "\u8981\u8d27\u660e\u7ec6\u4e2d\u5b58\u5728\u91cd\u590d\u5546\u54c1\uff01");
                return false;
            }

            // 仓库数据权限（W1）：整单商品必须全部落在当前用户管理的仓库内，
            // 否则一张"别仓商品"的要货单就能被审核并进而被生产单引入。
            if (!_depot.AllowsAllItems(rows.Select(r => r.ItemId)))
            {
                ModelState.AddModelError("Rows", OutOfScopeMsg);
                return false;
            }

            foreach (var r in rows)
            {
                if (!items.ContainsKey(r.ItemId))
                {
                    ModelState.AddModelError("Rows", "\u6240\u9009\u5546\u54c1\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664\uff0c\u8bf7\u91cd\u65b0\u9009\u62e9\u3002");
                    return false;
                }
                if (!bomMap.ContainsKey(r.ItemId))
                {
                    ModelState.AddModelError("Rows",
                        "\u5546\u54c1\u201c" + items[r.ItemId].ItemShortName + "\u201d\u672a\u914d\u7f6e BOM \u6e05\u5355\uff0c\u8bf7\u5148\u5728\u3010BOM\u6e05\u5355\u8bbe\u7f6e\u3011\u4e2d\u5efa\u7acb\uff01");
                    return false;
                }
                if ((r.Qty ?? 0) <= 0)
                {
                    ModelState.AddModelError("Rows", "\u8ba1\u5212\u6570\u91cf\u5fc5\u987b\u5927\u4e8e 0\uff01");
                    return false;
                }
                if (r.FetchDate == null || r.FetchDate.Value.Date < DateTime.Today)
                {
                    ModelState.AddModelError("Rows", "\u5b8c\u6210\u65e5\u671f\u4e0d\u80fd\u65e9\u4e8e\u4eca\u5929\uff01");
                    return false;
                }
                // Price is operator input, but it must never be negative: FAmount = qty * price
                // is what downstream amount reports are built on.
                if ((r.Price ?? 0m) < 0m)
                {
                    ModelState.AddModelError("Rows", "\u5355\u4ef7\u4e0d\u80fd\u4e3a\u8d1f\u6570\uff01");   // 单价不能为负数！
                    return false;
                }
            }

            return true;
        }

        /// <summary>Replace-all save: delete old entries then insert new ones, inside a transaction.</summary>
        private void SaveBill(GoodsApplyEditViewModel vm, t_PMS_GoodsApply header,
            Dictionary<int, t_ERP_ITEM> items, Dictionary<int, int> bomMap)
        {
            var unitMap = LoadUnitMap();

            using (var tx = _db.Database.BeginTransaction())
            {
                var old = _db.t_PMS_GoodsApplyEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (old.Count > 0) _db.t_PMS_GoodsApplyEntry.RemoveRange(old);

                if (header.FInterID == 0 || !_db.t_PMS_GoodsApply.Any(h => h.FInterID == header.FInterID))
                    _db.t_PMS_GoodsApply.Add(header);

                foreach (var r in (vm.Rows ?? new List<GoodsApplyRowInput>()).Where(r => r.ItemId > 0))
                {
                    var it = items[r.ItemId];
                    var qty = r.Qty ?? 0m;
                    var price = r.Price ?? 0m;
                    var unitName = (it.BaseUnit ?? string.Empty).Trim();
                    var unitId = unitMap.ContainsKey(unitName) ? unitMap[unitName] : 0;

                    _db.t_PMS_GoodsApplyEntry.Add(new t_PMS_GoodsApplyEntry
                    {
                        FInterID = header.FInterID,
                        FItemID = r.ItemId,
                        FQty = qty,
                        FPrice = price,
                        FAmount = qty * price,
                        FNote = string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note.Trim(),
                        FUnitID = unitId,
                        FFetchDate = r.FetchDate,
                        FSecCoefficient = 0m,
                        FSecQty = 0m,
                        FStockID = header.FStockID,
                        FBomId = bomMap[r.ItemId],
                        FStatus = 0,
                        FFetchNum = 0m,
                    });
                }

                _db.SaveChanges();
                tx.Commit();
            }
        }

        /// <summary>Item snapshots + BOM ids for rows already in the form (edit / validation redisplay).</summary>
        private void BindRowInfo(GoodsApplyEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<GoodsApplyRowInput>())
                .Where(r => r.ItemId > 0)
                .Select(r => r.ItemId)
                .Distinct()
                .ToList();
            ViewBag.RowItems = _db.t_ERP_ITEM
                .Where(i => ids.Contains(i.ID))
                .ToDictionary(i => i.ID, i => i);

            var bomMap = LoadBomMap();
            ViewBag.RowBom = (vm.Rows ?? new List<GoodsApplyRowInput>())
                .Where(r => r.ItemId > 0)
                .GroupBy(r => r.ItemId)
                .ToDictionary(g => g.Key, g => bomMap.ContainsKey(g.Key) ? bomMap[g.Key] : 0);
        }

        // Process-wide lock serializes "read max + 1" so concurrent creates cannot
        // yield the same bill no / inter id (single Kestrel process per deploy).
        private static readonly object BillNoLock = new object();

        /// <summary>"JHD" + 8-digit sequence = max(FBillNo)+1 (old GetGoodsApplyBillNo).</summary>
        private string NextBillNo()
        {
            lock (BillNoLock)
            {
                // Only consider well-formed "JHD"+8-digit bill numbers; dirty/legacy
                // values are ignored so they cannot poison the sequence or collide.
                int maxNo = 0;
                foreach (var bn in _db.t_PMS_GoodsApply.Select(h => h.FBillNo))
                {
                    if (string.IsNullOrEmpty(bn) || bn.Length != 11 || !bn.StartsWith("JHD")) continue;
                    int no;
                    if (int.TryParse(bn.Substring(3), out no) && no > maxNo) maxNo = no;
                }
                return "JHD" + (maxNo + 1).ToString("00000000");
            }
        }

        private int NextInterId()
        {
            lock (BillNoLock)
            {
                var max = _db.t_PMS_GoodsApply.Max(h => (int?)h.FInterID) ?? 0;
                return max + 1;
            }
        }

        /// <summary>
        /// Form data for the three-level cascade (user decision 2026-09-08):
        /// level-1 applicant departments (IsDEP=0) -> level-2 sub-departments
        /// (workshop/warehouse/team, IsDEP!=0, filtered client-side by ParentID)
        /// -> level-3 workers (filtered client-side by DEPID or DEPID's parent).
        /// All options are rendered once and filtered in JS; the Razor view also
        /// marks the current selections so edit/redisplay round-trips safely.
        /// </summary>
        private void BindFormExtras()
        {
            var depts = _db.t_ERP_Department.OrderBy(d => d.ID).ToList();

            // The organisation root (「所有部门」, ParentID=0) owns no workshop, so picking it
            // only yields an empty workshop list -- hide it from the dropdown. Drafts that
            // already point at it are re-added by EnsureDeptOption so the stored value can
            // still be preselected instead of silently showing a different department.
            ViewBag.DeptOptions = depts.Where(d => d.IsDEP == 0 && d.Status == 1
                                                   && (d.ParentID ?? 0) != 0).ToList();
            ViewBag.SubDeptOptions = depts.Where(d => d.IsDEP != 0 && d.Status == 1).ToList();
            ViewBag.Workers = _db.t_PMS_Worker
                .Where(w => w.Status == true)
                .OrderBy(w => w.ID)
                .ToList();
        }

        /// <summary>
        /// Put a hidden department back into the dropdown when the stored bill points at it.
        /// Without this, editing a legacy draft whose FDeptID is the tree root would render a
        /// select that cannot preselect its own value -- the operator sees a different dept
        /// than the one actually saved, and the next save silently rewrites it.
        /// </summary>
        private void EnsureDeptOption(int? deptId)
        {
            if (deptId == null || deptId.Value <= 0) return;
            var list = ViewBag.DeptOptions as List<t_ERP_Department>;
            if (list == null || list.Any(d => d.ID == deptId.Value)) return;

            var dept = _db.t_ERP_Department
                .FirstOrDefault(d => d.ID == deptId.Value && d.IsDEP == 0 && d.Status == 1);
            if (dept == null) return;

            list.Add(dept);
            ViewBag.DeptOptions = list.OrderBy(d => d.ID).ToList();
        }

        /// <summary>
        /// Server-side cascade validation (anti-tamper): the workshop must be an
        /// active sub-department of the chosen dept, and the applicant must hang
        /// directly off the workshop or off a team under it.
        /// </summary>
        private void ValidateCascade(GoodsApplyEditViewModel vm, t_PMS_Worker worker)
        {
            if (vm.DeptId == null || vm.DeptId.Value <= 0
                || !_db.t_ERP_Department.Any(d => d.ID == vm.DeptId.Value && d.IsDEP == 0 && d.Status == 1))
            {
                ModelState.AddModelError(nameof(vm.DeptId), "\u8bf7\u9009\u62e9\u7533\u8bf7\u90e8\u95e8\uff01");   // choose applicant department
                return;
            }

            var ws = vm.WorkShopId == null
                ? null
                : _db.t_ERP_Department.FirstOrDefault(d => d.ID == vm.WorkShopId.Value && d.IsDEP != 0 && d.Status == 1);
            if (ws == null || ws.ParentID != vm.DeptId.Value)
            {
                ModelState.AddModelError(nameof(vm.WorkShopId), "\u8bf7\u9009\u62e9\u8be5\u90e8\u95e8\u4e0b\u5c5e\u7684\u8f66\u95f4\uff01");   // workshop must belong to the dept
                return;
            }

            if (worker == null) return;   // already reported by the caller

            // worker under workshop: directly (DEPID == ws) or via a team (DEPID's parent == ws)
            var wdep = _db.t_ERP_Department.FirstOrDefault(d => d.ID == worker.DEPID);
            var underWs = worker.DEPID == vm.WorkShopId.Value
                || (wdep != null && wdep.ParentID == vm.WorkShopId.Value);
            if (!underWs)
            {
                ModelState.AddModelError(nameof(vm.EmpId), "\u7533\u8bf7\u4eba\u4e0d\u5c5e\u4e8e\u6240\u9009\u8f66\u95f4\uff01");   // applicant not under the workshop
            }
        }
    }
}
