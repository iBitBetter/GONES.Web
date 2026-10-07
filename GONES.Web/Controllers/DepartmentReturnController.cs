using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;
using GONES.Web.Services;
using Microsoft.EntityFrameworkCore;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// Department RETURN bill - mirrors the legacy WinForms
    /// Stock.FrmDepartmentReturnManager (\u90e8\u95e8\u9000\u6599\u7ef4\u62a4, t_ERP_Menu.ID=1295).
    /// Header t_PMS_StockBill (FBillType=2, FBillTypeEx=\u90e8\u95e8\u9000\u6599, prefix BMTLD) +
    /// entries t_PMS_StockBillEntry. In-stock direction: FROB = +1 (DirIn); audit stamps the
    /// rows and re-derives caches (StockService.StampReturnStock) - batch nos are re-used.
    /// Legacy form has a SINGLE return-quantity column -> FNum (no FNumExt split).
    /// Rows have two accepted origins:
    ///   1. imported from an AUDITED department picking bill (FBillType=9, FState""=true) -
    ///      trust boundary: PickBillId + PickEntryId locate the source row server-side;
    ///      ItemId / BatchNo / PickNum are rederived from it on save;
    ///   2. manually added item rows - ItemId / BatchNo / Num trusted from the form after
    ///      the item and the batch master rows are verified to exist.
    /// Guards: every row needs Num &gt; 0 and a batch no. Imported rows additionally obey the
    /// per-(item,batch) over-return guard (audited dept returns &lt;= audited dept picks);
    /// manual rows may not exceed the batch's current on-hand stock (no phantom stock).
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // standard MVC form redisplay, skip Furion 400 short-circuit
    public class DepartmentReturnController : Controller, IMenuGuarded
    {
        private const int BillType = 2;
        private const int PickBillType = 9;
        private const string BillNoPrefix = "BMTLD";
        private const string BillTypeExName = "\u90e8\u95e8\u9000\u6599";   // 部门退料

        /// <summary>W1 越权提示：读可以少看，动必须整套归你（编辑/审核/反审核/删除共用一份文案）。</summary>
        private const string OutOfScopeMsg = "\u8be5\u5355\u636e\u5305\u542b\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u4ed3\u5e93\u7684\u660e\u7ec6\uff0c\u7981\u6b62\u64cd\u4f5c\uff01";   // 该单据包含不属于您管理仓库的明细，禁止操作！

        private readonly GonesPgDbContext _db;
        private readonly StockService _stock;
        private readonly MenuService _menu;
        private readonly DepotScope _depot;
        public DepartmentReturnController(GonesPgDbContext db, StockService stock, MenuService menu, DepotScope depot)
        {
            _db = db;
            _stock = stock;
            _menu = menu;
            _depot = depot;
        }

        private bool IsAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        /// <summary>
        /// 入口闸门：admin 旁路恒 true（行为逐字节不变），非 admin 须由其角色的
        /// t_ERP_RoleMenu 授权覆盖本模块（1295）。行级判定只用 item.WarehouseId（仓库维度）。
        /// </summary>

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

        public IActionResult Index(DateTime? dateFrom, DateTime? dateTo, string billNo,
            int? state, int page = 1, int pageSize = 20)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);
            billNo = billNo?.Trim();

            var hq = _db.t_PMS_StockBill.Where(h => h.FBillType == BillType);
            // 数据闸门①-a（行级读）：只保留"至少有一行明细属于本仓库"的单据。
            if (_depot.IsRestricted) hq = _depot.FilterBills(hq);
            // hoist .Date out of the lambdas: EF6 cannot translate Nullable<DateTime>.Date
            var dFrom = dateFrom.HasValue ? dateFrom.Value.Date : (DateTime?)null;
            var dTo = dateTo.HasValue ? dateTo.Value.Date.AddDays(1) : (DateTime?)null;
            if (dFrom.HasValue) hq = hq.Where(h => h.FDate >= dFrom.Value);
            if (dTo.HasValue) hq = hq.Where(h => h.FDate < dTo.Value);
            if (!string.IsNullOrEmpty(billNo)) hq = hq.Where(h => h.FBillNo.Contains(billNo));
            if (state.HasValue && state.Value >= 0) hq = hq.Where(h => h.FState == (state.Value == 1));

            int total = hq.Count();
            page = PagerViewModel.ClampPage(page, total, pageSize);
            var headers = hq.OrderByDescending(h => h.FInterID)
                .Skip((page - 1) * pageSize).Take(pageSize).ToList();
            var headerIds = headers.Select(h => h.FInterID).ToList();

            var matchedIds = hq.Select(h => h.FInterID);
            // 数据闸门①-b（合计口径）：合计必须与"可见行"同口径，否则把别仓的数量算进来=越权泄露。
            var sumQ = _db.t_PMS_StockBillEntry.Where(e => matchedIds.Contains(e.FInterID));
            if (_depot.IsRestricted) sumQ = _depot.FilterEntries(sumQ);
            ViewBag.TotalQty = sumQ
                .Sum(e => (decimal?)(e.FNum ?? 0m)) ?? 0m;

            // 数据闸门①-c（明细行收窄）：列表只渲染本仓库的明细行。
            var entryQ = _db.t_PMS_StockBillEntry.Where(e => headerIds.Contains(e.FInterID));
            if (_depot.IsRestricted) entryQ = _depot.FilterEntries(entryQ);
            var entries = entryQ
                .OrderBy(e => e.FInterID).ThenBy(e => e.FEntryID)
                .ToList();
            var entriesByBill = entries.GroupBy(e => e.FInterID).ToDictionary(g => g.Key, g => g.ToList());

            var itemIds = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);
            var depts = _db.t_ERP_Department.ToDictionary(d => d.ID, d => d.DEPName);
            var managerIds = headers.Where(h => h.FManagerID != null).Select(h => h.FManagerID.Value).Distinct().ToList();
            var workers = _db.t_PMS_Worker.Where(w => managerIds.Contains(w.ID)).ToDictionary(w => w.ID, w => w.FName);

            string DeptName(int? id) => (id != null && depts.ContainsKey(id.Value)) ? depts[id.Value] : "";
            string WorkerName(int? id) => (id != null && workers.ContainsKey(id.Value)) ? workers[id.Value] : "";

            // 数据闸门③-b（按钮灰化）：与服务端 W1 硬拦同源。
            var lockedByDepot = _depot.BillsNotFullyInScope(headerIds);

            var rows = new List<DepartmentReturnListRow>();
            foreach (var h in headers)
            {
                var bills = entriesByBill.ContainsKey(h.FInterID) ? entriesByBill[h.FInterID] : new List<t_PMS_StockBillEntry>();
                var first = true;
                if (bills.Count == 0)
                {
                    rows.Add(new DepartmentReturnListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = true, DeptName = DeptName(h.FDeptID),
                        WorkShopName = DeptName(h.FWorkShopID), ManagerName = WorkerName(h.FManagerID),
                        CanModify = !lockedByDepot.Contains(h.FInterID),
                    });
                    continue;
                }
                foreach (var e in bills)
                {
                    t_ERP_ITEM it = null;
                    if (e.FItemID != null && items.ContainsKey(e.FItemID.Value)) it = items[e.FItemID.Value];
                    rows.Add(new DepartmentReturnListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = first, DeptName = DeptName(h.FDeptID),
                        WorkShopName = DeptName(h.FWorkShopID), ManagerName = WorkerName(h.FManagerID),
                        CanModify = !lockedByDepot.Contains(h.FInterID),
                        FromPickBillNo = e.FBillUseNo,
                        EntryId = e.FEntryID, BatchNo = e.FBatchNo,
                        Cpbm = it?.ItemCode ?? "", Cplb = it?.ProductCategory ?? "", Cpjc = it?.ItemShortName ?? "",
                        Cpgg = it?.ItemSpec ?? "", UnitName = it?.BaseUnit ?? "",
                        Num = e.FNum,
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
                ["state"] = (!state.HasValue || state.Value < 0) ? "-1" : state.Value.ToString(),
            };
            var extra = new Dictionary<string, object>();
            if (dateFrom.HasValue) extra["dateFrom"] = dateFrom.Value.ToString("yyyy-MM-dd");
            if (dateTo.HasValue) extra["dateTo"] = dateTo.Value.ToString("yyyy-MM-dd");
            if (!string.IsNullOrEmpty(billNo)) extra["billNo"] = billNo;
            if (state.HasValue && state.Value >= 0) extra["state"] = state.Value;
            ViewBag.Pager = new PagerViewModel
            {
                Page = page, PageSize = pageSize, TotalCount = total, Action = "Index",
                ExtraValues = extra.Count > 0 ? extra : null,
            };
            return View(rows);
        }

        // ------------------------------------------------------------ create

        [HttpGet]
        public IActionResult Create()
        {
            BindFormExtras();
            var vm = new DepartmentReturnEditViewModel
            {
                BillNo = NextBillNo(),
                Date = DateTime.Today,
                Rows = new List<DepartmentReturnRowInput>(),
            };
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(DepartmentReturnEditViewModel vm)
        {

            if (!DeriveRows(vm) || !ValidateBill(vm))
            {
                BindFormExtras(vm.DeptId, vm.WorkShopId);
                return View("Form", vm);
            }

            lock (StockService.BillNoLock)
            {
                var header = new t_PMS_StockBill
                {
                    FInterID = NextInterId(),
                    FBillNo = NextBillNo(),
                    FBillType = BillType,
                    FBillTypeEx = BillTypeExName,
                    FDate = vm.Date,
                    FDeptID = vm.DeptId,
                    FWorkShopID = vm.WorkShopId,
                    FManagerID = vm.ManagerId,
                    FRemark = vm.Remark ?? "",
                    FCreaterID = CurrentUserId,
                    FROB = GONES.Web.Services.StockService.DirIn,   // +1, return moves stock back in
                    FState = false,
                };
                SaveBill(vm, header);
            }

            TempData["Success"] = "\u90e8\u95e8\u9000\u6599\u5355\u4fdd\u5b58\u6210\u529f\u3002";   // 部门退料单保存成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ edit

        [HttpGet]
        public IActionResult Edit(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门③-b（写=整单级 W1）：明细未全部落在本仓库范围内 ⇒ 禁止修改。
            if (!_depot.BillFullyInScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            if ((header.FState ?? false))
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff0c\u7981\u6b62\u4fee\u6539\uff01";   // 单据已审核，禁止修改！
                return RedirectToAction(nameof(Index));
            }

            return ViewForm(header, readOnly: false);
        }

        // GET /DepartmentReturn/Details/5  (read-only detail page for audited bills)
        public IActionResult Details(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门①-d（行级读）：只读详情只显示本仓库的明细行；一行都看不见时整页拒绝。
            if (_depot.IsRestricted)
            {
                var dq = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id && e.FItemID != null && e.FItemID.Value > 0);
                if (_depot.FilterEntries(dq).Count() == 0)
                {
                    TempData["Error"] = OutOfScopeMsg;
                    return RedirectToAction(nameof(Index));
                }
            }
            return ViewForm(header, readOnly: true);
        }

        /// <summary>
        /// Shared form loader for Edit (editable) and Details (read-only): both build the same
        /// view model and render Form.cshtml, so there is exactly one load path and no drift
        /// between the edit page and the read-only page.
        /// </summary>
        private IActionResult ViewForm(t_PMS_StockBill header, bool readOnly)
        {
            var id = header.FInterID;
            // 行级读：Details 只显示本仓可见行；Edit 已被 W1 拦截（整单必在本仓），此处过滤为空操作。
            var entryQ = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id);
            if (_depot.IsRestricted) entryQ = _depot.FilterEntries(entryQ);
            var entries = entryQ.OrderBy(e => e.FEntryID).ToList();

            var itemIds = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);
            var batchNos = entries.Select(e => e.FBatchNo).Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList();
            var batches = _db.t_PMS_BatchNoStock.Where(b => batchNos.Contains(b.FBatchNo))
                .GroupBy(b => b.FBatchNo).ToDictionary(g => g.Key, g => g.First());   // FBatchNo 无唯一索引：脏数据重复批号时防 ToDictionary 抛键冲突 500

            var batchQtyMap = _stock.BatchQtyMap(_db, batchNos);

            // Step names for display (旧 FrmDepartmentReturn 的「工序名称」列；FStepName
            // is not stored on the entry -- join t_PMS_Step at render time).
            var stepNames = _db.Database.SqlQueryRaw<ProductBomStatStepOption>("SELECT \"f_item_id\" AS \"Id\", \"f_name\" AS Name FROM \"t_pms_step\"")
                .ToDictionary(s => s.Id, s => s.Name ?? "");

            // source dept-pick rows (locate by FBillUseNo -> type-9 bill header)
            var pickNos = entries.Select(e => e.FBillUseNo).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            var pickBills = _db.t_PMS_StockBill
                .Where(h => h.FBillType == PickBillType && pickNos.Contains(h.FBillNo))
                .GroupBy(h => h.FBillNo).ToDictionary(g => g.Key, g => g.First().FInterID);   // FBillNo 无唯一约束：防重复单号脏数据 500
            var pickEntries = LoadPickEntries(pickBills.Values.Distinct().ToList());

            var rows = new List<DepartmentReturnRowInput>();
            foreach (var e in entries)
            {
                if (e.FItemID == null || e.FItemID.Value == 0) continue;
                t_ERP_ITEM it = items.ContainsKey(e.FItemID.Value) ? items[e.FItemID.Value] : null;
                t_PMS_BatchNoStock batch = (!string.IsNullOrEmpty(e.FBatchNo) && batches.ContainsKey(e.FBatchNo))
                    ? batches[e.FBatchNo] : null;
                int pickBillId = (!string.IsNullOrEmpty(e.FBillUseNo) && pickBills.ContainsKey(e.FBillUseNo))
                    ? pickBills[e.FBillUseNo] : 0;
                decimal? pickNum = (pickBillId > 0 && e.FBillUseEntryID.HasValue
                                    && pickEntries.ContainsKey((pickBillId, e.FBillUseEntryID.Value)))
                    ? pickEntries[(pickBillId, e.FBillUseEntryID.Value)] : (decimal?)null;

                rows.Add(new DepartmentReturnRowInput
                {
                    ItemId = e.FItemID.Value,
                    Cpbm = it?.ItemCode ?? "",
                    ProductName = it?.ItemShortName ?? "",
                    Spec = it?.ItemSpec ?? "",
                    CategoryName = it?.ProductCategory ?? "",
                    Unit = it?.BaseUnit ?? "",
                    StepId = e.FStepID,
                    StepName = (e.FStepID > 0 && stepNames.ContainsKey(e.FStepID)) ? stepNames[e.FStepID] : "",
                    BatchStock = string.IsNullOrEmpty(e.FBatchNo) ? 0m : (batchQtyMap.TryGetValue(e.FBatchNo, out var bq) ? bq : 0m),
                    BatchNo = e.FBatchNo,
                    WarehouseName = WarehouseNameOf(it),
                    PickBillId = pickBillId > 0 ? pickBillId : (int?)null,
                    PickBillNo = e.FBillUseNo,
                    PickEntryId = e.FBillUseEntryID,
                    PickNum = pickNum,
                    Num = e.FNum,
                    Note = e.FNote,
                });
            }

            var vm = new DepartmentReturnEditViewModel
            {
                InterId = header.FInterID,
                BillNo = header.FBillNo,
                Date = header.FDate ?? DateTime.Today,
                DeptId = header.FDeptID,
                WorkShopId = header.FWorkShopID,
                ManagerId = header.FManagerID,
                Remark = header.FRemark,
                State = header.FState ?? false,
                Rows = rows,
            };
            BindFormExtras(header.FDeptID, header.FWorkShopID);
            ViewBag.ReadOnly = readOnly;
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, DepartmentReturnEditViewModel vm)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门③-b（写=整单级 W1）：不但要防页面被打开，也要防**直接构造 POST**。
            if (!_depot.BillFullyInScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            if ((header.FState ?? false))
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff0c\u7981\u6b62\u4fee\u6539\uff01";
                return RedirectToAction(nameof(Index));
            }

            vm.InterId = id;
            vm.BillNo = header.FBillNo;

            if (!DeriveRows(vm) || !ValidateBill(vm))
            {
                BindFormExtras(vm.DeptId, vm.WorkShopId);
                return View("Form", vm);
            }

            header.FDate = vm.Date;
            header.FDeptID = vm.DeptId;
            header.FWorkShopID = vm.WorkShopId;
            header.FManagerID = vm.ManagerId;
            header.FRemark = vm.Remark ?? "";
            header.FModifyID = CurrentUserId;
            header.FModifyTime = DateTime.Now;
            SaveBill(vm, header);

            TempData["Success"] = "\u90e8\u95e8\u9000\u6599\u5355\u4fee\u6539\u6210\u529f\u3002";   // 部门退料单修改成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ delete

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门③-b（写=整单级 W1）：跨仓单据禁止删除。
            if (!_depot.BillFullyInScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            if ((header.FState ?? false))
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff0c\u7981\u6b62\u5220\u9664\uff01";   // 单据已审核，禁止删除！
                return RedirectToAction(nameof(Index));
            }

            // tri_deleteStockBillEntry trigger dropped (2026-09-09): single-pass delete.
            using (var tx = _db.Database.BeginTransaction())
            {
                var entries = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id).ToList();
                _db.t_PMS_StockBillEntry.RemoveRange(entries);
                _db.t_PMS_StockBill.Remove(header);
                _db.SaveChanges();
                tx.Commit();
            }

            TempData["Success"] = "\u90e8\u95e8\u9000\u6599\u5355\u5220\u9664\u6210\u529f\u3002";   // 部门退料单删除成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ audit / unaudit

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Audit(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门③-b（写=整单级 W1）：跨仓单据禁止审核。
            if (!_depot.BillFullyInScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            if ((header.FState ?? false))
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff0c\u65e0\u9700\u91cd\u590d\u5ba1\u6838\uff01";
                return RedirectToAction(nameof(Index));
            }

            var entries = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id).OrderBy(e => e.FEntryID).ToList();
            if (entries.Count == 0)
            {
                // An empty bill must never become "audited": it would pollute the audited
                // counters and the ledger reconciliation for no reason.
                TempData["Error"] = "\u5355\u636e\u6ca1\u6709\u660e\u7ec6\uff0c\u65e0\u6cd5\u5ba1\u6838\u3002";   // 单据没有明细，无法审核。
                return RedirectToAction(nameof(Index));
            }

            using (var tx = _stock.BeginStockTransaction(_db))
            {
                // 并发守卫（同 StockCheckController.Audit）：事务外的 FState 检查存在双审窗口，
                // 此刻在 advisory lock 保护下重读 DB 原子判定（投影绕过 EF 标识缓存）。
                var freshState = _db.t_PMS_StockBill.Where(h => h.FInterID == id && h.FBillType == BillType)
                                                    .Select(h => h.FState).FirstOrDefault();
                if ((freshState ?? false))
                {
                    TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff0c\u65e0\u9700\u91cd\u590d\u5ba1\u6838\uff01";
                    return RedirectToAction(nameof(Index));
                }

                // 来源单复验：本单还是草稿时，来源部门领料单（FB=9）可被反审核/删除
                // （DepartmentPick.UnAudit 不查草稿下游）。不复查就盖章会按失效来源回增库存。
                var srcNos = entries.Select(e => e.FBillUseNo)
                    .Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
                if (srcNos.Count > 0)
                {
                    var auditedSrc = _db.t_PMS_StockBill
                        .Where(h => h.FBillType == PickBillType && (h.FState ?? false) && srcNos.Contains(h.FBillNo))
                        .Select(h => h.FBillNo).ToList();
                    var missing = srcNos.Where(n => !auditedSrc.Contains(n)).ToList();
                    if (missing.Count > 0)
                    {
                        TempData["Error"] = "\u6765\u6e90\u9886\u6599\u5355 " + string.Join("\u3001", missing)
                            + " \u5df2\u88ab\u53cd\u5ba1\u6838\u6216\u5220\u9664\uff0c\u65e0\u6cd5\u5ba1\u6838\u672c\u9000\u6599\u5355\u3002";   // 来源领料单 X 已被反审核或删除，无法审核本退料单。
                        return RedirectToAction(nameof(Index));
                    }
                }

                // 超退复验（分组口径，与 ValidateBill 相同，此刻在 advisory lock 内原子成立）：
                // 导入行按 (ItemId, BatchNo) 分组合计 vs 已领-已退(含其他草稿)；手工行 vs
                // 批次现存量-其他草稿退料。保存时是"先查后写"，两张草稿可并发通过。
                var checkItemIds = entries.Where(e => e.FItemID.HasValue).Select(e => e.FItemID.Value).Distinct().ToList();
                var issuedMap = StockService.UsageByItemBatch(
                    _db, PickBillType, checkItemIds, null, includeDraft: false, fourColumns: false);
                var returnedMap = StockService.UsageByItemBatch(
                    _db, BillType, checkItemIds, header.FInterID, includeDraft: true, fourColumns: false);
                var draftMap = StockService.DraftUsageByItemBatch(
                    _db, BillType, checkItemIds, header.FInterID, fourColumns: false);
                var checkBatchNos = entries.Where(e => !string.IsNullOrEmpty(e.FBatchNo))
                                           .Select(e => e.FBatchNo).Distinct().ToList();
                var batchQtyMap = _stock.BatchQtyMap(_db, checkBatchNos);
                foreach (var g in entries.Where(e => e.FItemID.HasValue && !string.IsNullOrEmpty(e.FBatchNo))
                                         .GroupBy(e => new { ItemId = e.FItemID.Value, BatchNo = e.FBatchNo }))
                {
                    decimal groupTotal = g.Sum(e => (e.FNum ?? 0m));
                    decimal issued = issuedMap.TryGetValue((g.Key.ItemId, g.Key.BatchNo), out var iq) ? iq : 0m;
                    decimal returned = returnedMap.TryGetValue((g.Key.ItemId, g.Key.BatchNo), out var rq) ? rq : 0m;
                    bool fromPick = g.Any(e => !string.IsNullOrEmpty(e.FBillUseNo));
                    bool over;
                    if (fromPick)
                    {
                        over = groupTotal > issued - returned + 0.0001m;
                    }
                    else
                    {
                        decimal onHand = batchQtyMap.TryGetValue(g.Key.BatchNo, out var q) ? q : 0m;
                        decimal draftReturned = draftMap.TryGetValue((g.Key.ItemId, g.Key.BatchNo), out var d) ? d : 0m;
                        over = groupTotal > onHand - draftReturned + 0.0001m;
                    }
                    if (over)
                    {
                        TempData["Error"] = "\u6279\u6b21 " + g.Key.BatchNo + " \u9000\u6599\u5408\u8ba1 " + groupTotal.ToString("0.##")
                            + " \u8d85\u8fc7\u8be5\u6279\u53ef\u9000\u4f59\u91cf\uff0c\u65e0\u6cd5\u5ba1\u6838\u3002";
                        return RedirectToAction(nameof(Index));
                    }
                }

                if (!_stock.StampReturnStock(_db, header, entries, out string error))
                {
                    TempData["Error"] = error;
                    return RedirectToAction(nameof(Index));       // tx disposed -> rolled back
                }
                tx.Commit();
            }

            TempData["Success"] = "\u5ba1\u6838\u6210\u529f\uff0c\u5e93\u5b58\u5df2\u6309\u6279\u53f7\u56de\u589e\u3002";   // 审核成功，库存已按批号回增。
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UnAudit(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门③-b（写=整单级 W1）：跨仓单据禁止反审核（规则置于所有 early return 之前）。
            if (!_depot.BillFullyInScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            if (!(header.FState ?? false))
            {
                TempData["Error"] = "\u5355\u636e\u672a\u5ba1\u6838\uff0c\u65e0\u9700\u53cd\u5ba1\u6838\uff01";
                return RedirectToAction(nameof(Index));
            }

            var entries = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id).ToList();
            // A return moves stock IN, so un-auditing takes it back out: refuse when the
            // quantity was already consumed by a later audited bill (would go negative).
            using (var tx = _stock.BeginStockTransaction(_db))
            {
                if (!_stock.RevertReturnStock(_db, header, entries, out string revertError))
                {
                    TempData["Error"] = revertError;
                    return RedirectToAction(nameof(Index));       // tx disposed -> rolled back
                }
                tx.Commit();
            }

            TempData["Success"] = "\u53cd\u5ba1\u6838\u6210\u529f\uff0c\u5e93\u5b58\u5df2\u6062\u590d\u3002";   // 反审核成功，库存已恢复。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ dept-pick picker (modal)

        // GET /DepartmentReturn/GetPickBills
        // Audited department picking bills (FBillType=9, FState""=true), newest first, top 50.
        [HttpGet]
        public IActionResult GetPickBills()
        {

            var hq = _db.t_PMS_StockBill
                .Where(h => h.FBillType == PickBillType && (h.FState ?? false));
            // 数据闸门②-a（来源单选择器收窄）：只列"至少有一行明细属于本仓库"的部门领料单。
            if (_depot.IsRestricted) hq = _depot.FilterBills(hq);
            var headers = hq
                .OrderByDescending(h => h.FInterID)
                .Take(50)
                .ToList();
            var ids = headers.Select(h => h.FInterID).ToList();
            var stats = _db.t_PMS_StockBillEntry
                .Where(e => ids.Contains(e.FInterID))
                .GroupBy(e => e.FInterID)
                .ToDictionary(g => g.Key, g => new
                {
                    cnt = g.Count(),
                    qty = g.Sum(x => x.FNum ?? 0m),
                });

            var deptIds = headers.Where(h => h.FDeptID != null).Select(h => h.FDeptID.Value).Distinct().ToList();
            var depts = _db.t_ERP_Department.Where(d => deptIds.Contains(d.ID)).ToDictionary(d => d.ID, d => d.DEPName);

            var result = headers.Select(h =>
            {
                var st = stats.ContainsKey(h.FInterID) ? stats[h.FInterID] : null;
                return new
                {
                    interId = h.FInterID,
                    billNo = h.FBillNo,
                    date = (h.FDate ?? DateTime.Today).ToString("yyyy-MM-dd"),
                    deptName = (h.FDeptID != null && depts.ContainsKey(h.FDeptID.Value)) ? depts[h.FDeptID.Value] : "",
                    rowCount = st?.cnt ?? 0,
                    totalQty = st?.qty ?? 0m,
                };
            }).ToList();
            return Json(result);
        }

        // GET /DepartmentReturn/GetPickBillDetails?interId=N
        // Audited dept-pick rows that moved stock out (item + batch + qty > 0). The operator
        // only fills Num; the header backfills dept/workshop/manager from the source bill.
        [HttpGet]
        public IActionResult GetPickBillDetails(int interId)
        {

            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == interId
                && h.FBillType == PickBillType && (h.FState ?? false));
            if (header == null) return Json(new List<object>());

            // 数据闸门②-b：只返回本仓库的明细行，越仓行根本进不了导入流程。
            var entryQ = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == interId);
            if (_depot.IsRestricted) entryQ = _depot.FilterEntries(entryQ);
            var entries = entryQ
                .OrderBy(e => e.FEntryID)
                .ToList();
            var itemIds = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            var batchQtyMap = _stock.BatchQtyMap(_db,
                entries.Select(e => e.FBatchNo).Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList());
            var stepNames = _db.Database.SqlQueryRaw<ProductBomStatStepOption>("SELECT \"f_item_id\" AS \"Id\", \"f_name\" AS Name FROM \"t_pms_step\"")
                .ToDictionary(s => s.Id, s => s.Name ?? "");

            var detail = entries
                .Where(e => e.FItemID != null && e.FItemID.Value > 0
                            && !string.IsNullOrEmpty(e.FBatchNo) && (e.FNum ?? 0m) > 0m)
                .Select(e =>
                {
                    var it = items.ContainsKey(e.FItemID.Value) ? items[e.FItemID.Value] : null;
                    return new
                    {
                        pickBillId = interId,
                        pickBillNo = header.FBillNo ?? "",
                        pickEntryId = e.FEntryID,
                        itemId = e.FItemID.Value,
                        cpbm = it?.ItemCode ?? "",
                        productName = it?.ItemShortName ?? "",
                        spec = it?.ItemSpec ?? "",
                        categoryName = it?.ProductCategory ?? "",
                        unit = it?.BaseUnit ?? "",
                        batchNo = e.FBatchNo ?? "",
                        batchStock = string.IsNullOrEmpty(e.FBatchNo) ? 0m : (batchQtyMap.TryGetValue(e.FBatchNo, out var bq) ? bq : 0m),
                        stepId = e.FStepID,
                        stepName = (e.FStepID > 0 && stepNames.ContainsKey(e.FStepID)) ? stepNames[e.FStepID] : "",
                        // source dept-pick header ids: the form backfills its dept/workshop/
                        // manager selects from them on import
                        deptId = header.FDeptID,
                        workShopId = header.FWorkShopID,
                        managerId = header.FManagerID,
                        pickNum = e.FNum ?? 0m,
                        warehouseName = WarehouseNameOf(it),
                    };
                })
                .ToList();
            return Json(detail);
        }

        // ------------------------------------------------------------ manual item rows (lookup)

        [HttpGet]
        public IActionResult SearchItems(string keyword)
        {
            var kw = (keyword ?? string.Empty).Trim();
            int idMatch = int.TryParse(kw, out var n) ? n : -1;
            // 数据闸门②（选择器收窄）：商品搜索只返回本仓库的商品。
            // 关键字为空 = 默认列出本仓库全部启用商品（选择商品弹窗的默认列表）。
            var q = _depot.FilterItems(_db.t_ERP_ITEM).Where(i => i.IsEnabled == true);
            if (kw.Length > 0)
            {
                q = q.Where(i => (i.ItemCode != null && i.ItemCode.Contains(kw))
                              || (i.ItemShortName != null && i.ItemShortName.Contains(kw))
                              || (i.ItemSpec != null && i.ItemSpec.Contains(kw))
                              || (i.ItemPinyinCode != null && i.ItemPinyinCode.Contains(kw))
                              || i.ID == idMatch);
            }
            var items = q.OrderBy(i => i.ItemCode).Take(20).ToList();
            var ckids = items.Where(i => i.WarehouseId != null).Select(i => i.WarehouseId.Value).Distinct().ToList();
            var whNames = _db.t_ERP_Department
                .Where(d => d.IsDEP == 1 && ckids.Contains(d.ID))
                .ToDictionary(d => d.ID, d => d.DEPName ?? "");
            // 仓库名随商品直接带出（item.WarehouseId -> t_ERP_Department(IsDEP=1).DEPName）：
            // 弹窗「确定」落行即有仓库名，不必等选批号触发 GetBatchInfo 回填。
            return Json(items.Select(i => new
            {
                id = i.ID, cpbm = i.ItemCode, cpjc = i.ItemShortName, cpgg = i.ItemSpec,
                cplb = i.ProductCategory, cppym = i.ItemPinyinCode, unit = i.BaseUnit,
                warehouseName = (i.WarehouseId != null && whNames.ContainsKey(i.WarehouseId.Value)) ? whNames[i.WarehouseId.Value] : "",
            }).ToList());
        }

        // Step dictionary for the item-picker modal (same semantics as DepartmentPick:
        // 旧 FrmDepartmentReturn 的 gc_FStepName 可编辑列）。t_PMS_Step has no EF entity
        // -> SqlQuery (must map to PROPERTIES, EF6 SqlQuery does not map public fields).
        [HttpGet]
        public IActionResult GetSteps()
        {
            var list = _db.Database.SqlQueryRaw<ProductBomStatStepOption>("SELECT \"f_item_id\" AS \"Id\", \"f_name\" AS Name FROM \"t_pms_step\" WHERE COALESCE(\"f_delete\",0) = 0 ORDER BY \"f_item_id\"")   // 禁用工序不进新建选择器
                .ToList();
            return Json(list);
        }

        // Batch info lookup for a row's BatchNo change: refreshes 批次库存 / 仓库名称.
        // Warehouse name = item.WarehouseId -> t_ERP_Department (IsDEP=1). t_PMS_BatchNoStock.FName
        // is the operator name, NOT the warehouse.
        [HttpGet]
        public IActionResult GetBatchInfo(string batchNo)
        {
            if (string.IsNullOrWhiteSpace(batchNo)) return Json(new { batchStock = 0m, warehouseName = "" });
            var b = _db.t_PMS_BatchNoStock.FirstOrDefault(x => x.FBatchNo == batchNo);
            if (b == null) return Json(new { batchStock = 0m, warehouseName = "" });
            // 数据闸门②（选择器收窄）：不属于本仓的批次不返回库存与仓库名。
            if (!_depot.AllowsItem(b.FItemID ?? 0)) return Json(new { batchStock = 0m, warehouseName = "" });

            string warehouseName = "";
            if (b.FItemID != null && b.FItemID.Value > 0)
            {
                var it = _db.t_ERP_ITEM.FirstOrDefault(i => i.ID == b.FItemID.Value);
                if (it?.WarehouseId != null && it.WarehouseId.Value > 0)
                {
                    warehouseName = _db.t_ERP_Department
                        .Where(d => d.ID == it.WarehouseId.Value && d.IsDEP == 1)
                        .Select(d => d.DEPName)
                        .FirstOrDefault() ?? "";
                }
            }
            return Json(new { batchStock = _stock.BatchQty(_db, b.FBatchNo), warehouseName = warehouseName });
        }

        // All known batches of one item (including zero-stock -- returns often go back
        // into a batch that is currently at 0), newest first, for the row's batch-number
        // dropdown. Qty is ledger truth (BatchQtyMap), not the FBatchNum cache.
        [HttpGet]
        public IActionResult GetItemBatches(int itemId)
        {
            if (itemId <= 0) return Json(new List<object>());
            // 数据闸门②（选择器收窄）：越仓商品不返回批次下拉。
            if (!_depot.AllowsItem(itemId)) return Json(new List<object>());
            var batchNos = _db.t_PMS_BatchNoStock
                .Where(b => b.FItemID == itemId && !string.IsNullOrEmpty(b.FBatchNo))
                .OrderByDescending(b => b.FInterID)
                .Select(b => b.FBatchNo)
                .ToList();
            var qtyMap = _stock.BatchQtyMap(_db, batchNos);
            var rows = batchNos.Distinct()
                .Select(b => new { batchNo = b, qty = qtyMap.TryGetValue(b, out var q) ? q : 0m })
                .ToList();
            return Json(rows);
        }

        [HttpGet]
        public IActionResult GetWorkShops(int deptId)
        {
            var list = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 2 && d.ParentID == deptId)
                .OrderBy(d => d.ID).Select(d => new { id = d.ID, name = d.DEPName }).ToList();
            return Json(list);
        }

        [HttpGet]
        public IActionResult GetManagers(int workShopId)
        {
            var list = _db.t_PMS_Worker
                .Where(w => w.Status == true && w.DEPID == workShopId)
                .OrderBy(w => w.ID).Select(d => new { id = d.ID, name = d.FName }).ToList();
            return Json(list);
        }

        // ------------------------------------------------------------ helpers

        // BillNoLock lives on StockService (shared across all bill types that write t_PMS_StockBill).

        private string NextBillNo()
        {
            lock (StockService.BillNoLock)
            {
                int maxNo = 0;
                foreach (var bn in _db.t_PMS_StockBill.Where(h => h.FBillType == BillType).Select(h => h.FBillNo))
                {
                    if (string.IsNullOrEmpty(bn) || bn.Length != BillNoPrefix.Length + 8 || !bn.StartsWith(BillNoPrefix)) continue;
                    int no;
                    if (int.TryParse(bn.Substring(BillNoPrefix.Length), out no) && no > maxNo) maxNo = no;
                }
                return BillNoPrefix + (maxNo + 1).ToString("00000000");
            }
        }

        private int NextInterId()
        {
            lock (StockService.BillNoLock)
            {
                var max = _db.t_PMS_StockBill.Max(h => (int?)h.FInterID) ?? 0;
                return max + 1;
            }
        }

        /// <summary>Warehouse display name: item.WarehouseId -> t_ERP_Department (IsDEP=1).DEPName.</summary>
        private string WarehouseNameOf(t_ERP_ITEM it)
        {
            if (it?.WarehouseId == null || it.WarehouseId.Value <= 0) return "";
            return _db.t_ERP_Department
                .Where(d => d.ID == it.WarehouseId.Value && d.IsDEP == 1)
                .Select(d => d.DEPName)
                .FirstOrDefault() ?? "";
        }

        private Dictionary<(int billId, int entryId), decimal> LoadPickEntries(List<int> pickBillIds)
        {
            var result = new Dictionary<(int, int), decimal>();
            if (pickBillIds.Count == 0) return result;
            var rows = _db.t_PMS_StockBillEntry
                .Where(e => pickBillIds.Contains(e.FInterID)).ToList();
            foreach (var e in rows)
                result[(e.FInterID, e.FEntryID)] = e.FNum ?? 0m;
            return result;
        }

        /// <summary>
        /// Trust boundary / row normalization.
        /// Imported rows: (PickBillId, PickEntryId) must locate an entry of an AUDITED
        /// dept-pick bill (FBillType=9); ItemId / BatchNo / PickNum / display fields are
        /// rederived from it. Manual rows: ItemId must exist; item display fields are
        /// reloaded from the DB (form values for those are overwritten).
        /// </summary>
        private bool DeriveRows(DepartmentReturnEditViewModel vm)
        {
            var rows = (vm.Rows ?? new List<DepartmentReturnRowInput>()).ToList();

            // ---- imported rows
            var keys = rows
                .Where(r => r.PickBillId.HasValue && r.PickEntryId.HasValue)
                .Select(r => (r.PickBillId.Value, r.PickEntryId.Value))
                .Distinct().ToList();
            var billIds = keys.Select(k => k.Item1).Distinct().ToList();
            var pickBills = _db.t_PMS_StockBill
                .Where(h => h.FBillType == PickBillType && (h.FState ?? false) && billIds.Contains(h.FInterID))
                .ToDictionary(h => h.FInterID, h => h);
            var pickEntries = _db.t_PMS_StockBillEntry
                .Where(e => billIds.Contains(e.FInterID)).ToList();
            var pickMap = pickEntries.ToDictionary(e => (e.FInterID, e.FEntryID));
            var pickItemIds = pickEntries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var pickItems = _db.t_ERP_ITEM.Where(i => pickItemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            // ---- manual rows
            var manualItemIds = rows
                .Where(r => !r.PickBillId.HasValue && r.ItemId > 0)
                .Select(r => r.ItemId).Distinct().ToList();
            var manualItems = _db.t_ERP_ITEM.Where(i => manualItemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            foreach (var r in rows)
            {
                if (r.PickBillId.HasValue && r.PickEntryId.HasValue)
                {
                    if (!pickBills.ContainsKey(r.PickBillId.Value)
                        || !pickMap.ContainsKey((r.PickBillId.Value, r.PickEntryId.Value)))
                    {
                        ModelState.AddModelError("Rows", "\u9000\u6599\u660e\u7ec6\u7684\u6765\u6e90\u9886\u6599\u5355\u884c\u4e0d\u5b58\u5728\u6216\u672a\u5ba1\u6838\uff0c\u8bf7\u91cd\u65b0\u5bfc\u5165\u3002");   // 退料明细的来源领料单行不存在或未审核，请重新导入。
                        return false;
                    }
                    var pb = pickBills[r.PickBillId.Value];
                    var pe = pickMap[(r.PickBillId.Value, r.PickEntryId.Value)];
                    var it = (pe.FItemID != null && pickItems.ContainsKey(pe.FItemID.Value)) ? pickItems[pe.FItemID.Value] : null;

                    r.ItemId = pe.FItemID ?? 0;
                    r.StepId = pe.FStepID;                   // 导入行工序继承来源领料行
                    r.Cpbm = it?.ItemCode ?? "";
                    r.ProductName = it?.ItemShortName ?? "";
                    r.Spec = it?.ItemSpec ?? "";
                    r.CategoryName = it?.ProductCategory ?? "";
                    r.Unit = it?.BaseUnit ?? "";
                    r.BatchNo = pe.FBatchNo ?? "";
                    r.PickNum = pe.FNum ?? 0m;
                    r.PickBillNo = pb.FBillNo ?? "";
                }
                else
                {
                    if (r.ItemId <= 0 || !manualItems.ContainsKey(r.ItemId))
                    {
                        ModelState.AddModelError("Rows", "\u9000\u6599\u660e\u7ec6\u4e2d\u542b\u6709\u4e0d\u5b58\u5728\u7684\u5546\u54c1\uff0c\u8bf7\u91cd\u65b0\u6dfb\u52a0\u3002");   // 退料明细中含有不存在的商品，请重新添加。
                        return false;
                    }
                    var it = manualItems[r.ItemId];
                    r.Cpbm = it.ItemCode ?? "";
                    r.ProductName = it.ItemShortName ?? "";
                    r.Spec = it.ItemSpec ?? "";
                    r.CategoryName = it.ProductCategory ?? "";
                    r.Unit = it.BaseUnit ?? "";
                    r.PickBillId = null;
                    r.PickBillNo = "";
                    r.PickEntryId = null;
                    r.PickNum = null;
                }
                r.WarehouseName = WarehouseNameOf(manualItems.ContainsKey(r.ItemId) ? manualItems[r.ItemId]
                    : (pickItems.ContainsKey(r.ItemId) ? pickItems[r.ItemId] : null));
            }
            return true;
        }

        private bool ValidateBill(DepartmentReturnEditViewModel vm)
        {
            if (vm.Date == default || vm.Date.Year < 2000)
            {
                ModelState.AddModelError("Date", "\u8bf7\u586b\u5199\u6b63\u786e\u7684\u5355\u636e\u65e5\u671f\uff01");
                return false;
            }
            var rows = (vm.Rows ?? new List<DepartmentReturnRowInput>()).Where(r => r.ItemId > 0).ToList();
            if (rows.Count == 0)
            {
                ModelState.AddModelError("Rows", "\u8bf7\u81f3\u5c11\u5bfc\u5165\u6216\u6dfb\u52a0\u4e00\u4e2a\u5546\u54c1\uff01");
                return false;
            }

            // 数据闸门③-a（写入）：提交行的商品必须全部属于本仓库。admin 旁路不进入此段。
            // DeriveRows 已先行校验存在性并回填 ProductName，故此处直接以名称定位越权行。
            if (_depot.IsRestricted)
            {
                foreach (var r in rows)
                {
                    if (_depot.AllowsItem(r.ItemId)) continue;
                    var nm = string.IsNullOrWhiteSpace(r.ProductName) ? ("\u5546\u54c1 " + r.ItemId) : r.ProductName;
                    ModelState.AddModelError("Rows",
                        "\u3010" + nm + "\u3011\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u7684\u4ed3\u5e93\uff0c\u7981\u6b62\u63d0\u4ea4\u3002");   // 【xx】不属于您管理的仓库，禁止提交。
                    return false;
                }
            }

            // one GROUP BY for every batch in the form instead of BatchQty per row (N+1)
            var batchQtyMap = _stock.BatchQtyMap(_db,
                rows.Select(r => r.BatchNo).Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList());

            // Over-return guard, per (item, batch):
            //   imported rows: audited dept picks (type 9) must cover all audited dept
            //                  returns (type 2, excluding this bill);
            //   manual rows:   the return may not exceed the batch's current on-hand stock
            //                  minus any un-audited (draft) returns of the same batch.
            //
            // 一次性取完（一次 GROUP BY），避免每行一次查询的 N+1。
            var itemIds = rows.Select(r => r.ItemId).Distinct().ToList();

            // 已领量：只有审核才真正移动库存，故只统计已审。口径同样走共享层。
            var issuedMap = StockService.UsageByItemBatch(
                _db, PickBillType, itemIds, null, includeDraft: false, fourColumns: false);
            decimal Issued(int itemId, string batchNo)
            {
                return issuedMap.TryGetValue((itemId, batchNo ?? ""), out var q) ? q : 0m;
            }

            // 「已退量」必须含草稿：本校验在保存时执行（草稿阶段），若只统计已审，两张草稿
            // 可各退同一批次的定量并各自保存成功，审核后合计超退（2026-09-10 修复）。
            // 口径与 ProductLoss / ExchangeIn / ProductIn / WarehouseReturn 统一，走共享层。
            var returnedMap = StockService.UsageByItemBatch(
                _db, BillType, itemIds, vm.InterId, includeDraft: true, fourColumns: false);
            decimal Returned(int itemId, string batchNo)
            {
                return returnedMap.TryGetValue((itemId, batchNo ?? ""), out var q) ? q : 0m;
            }

            // 手工行的上限是「批次现存量」，而现存量来自 ledger（h.FState"" = true），已把已审退料
            // 扣减过了。所以这里只能补减「未审草稿」那一份——若减全部（含已审）会重复扣减，
            // 使可用量偏小、正常单据被误拒。这是与导入行口径相反的地方，勿照抄。
            var draftReturnMap = StockService.DraftUsageByItemBatch(
                _db, BillType, itemIds, vm.InterId, fourColumns: false);
            decimal DraftReturned(int itemId, string batchNo)
            {
                return draftReturnMap.TryGetValue((itemId, batchNo ?? ""), out var q) ? q : 0m;
            }

            foreach (var r in rows)
            {
                var label = string.IsNullOrWhiteSpace(r.ProductName)
                    ? ("\u7b2c " + (rows.IndexOf(r) + 1) + " \u884c")
                    : r.ProductName;
                decimal num = r.Num ?? 0m;
                if (num < 0m)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u9000\u6599\u6570\u91cf\u4e0d\u80fd\u4e3a\u8d1f\u6570\uff01");
                    return false;
                }
                if (num <= 0m)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u9000\u6599\u6570\u91cf\u5fc5\u987b\u5927\u4e8e 0\uff01");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(r.BatchNo))
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u7f3a\u5c11\u6279\u53f7\uff0c\u8bf7\u586b\u5199\u6216\u91cd\u65b0\u5bfc\u5165\u3002");   // 【xx】缺少批号，请填写或重新导入。
                    return false;
                }

                // 超退/手工行上限校验已移至循环后的分组合计（见下）：同一批次拆两行时
                // 逐行各自 ≤ 剩余量但合计可超。此处只保留行内符号/必填校验。
            }

            // 批号-商品归属校验（手工行为唯一敞口）：批号是自由文本，若填了别的商品的批次，
            // 落库后批次账本（按 FBatchNo 聚合）与商品账本（按 FItemID 聚合）将永久对不上，
            // 且"单据即账本"模型下事后无法检测与修复。对齐 DepartmentPick/ProductLoss 的既有防线。
            var mismatch = _stock.FindBatchItemMismatch(_db, rows.Select(r => (r.ItemId, r.BatchNo)));
            if (mismatch != null)
            {
                ModelState.AddModelError("Rows", mismatch);
                return false;
            }

            // 超退校验按 (ItemId, BatchNo, 是否导入行) 分组合计：逐行校验时同一批次拆两行
            // （或重复提交同一来源行）各自通过但合计可超——2026-09-19 审计修复。
            foreach (var g in rows.Where(r => !string.IsNullOrWhiteSpace(r.BatchNo))
                                  .GroupBy(r => (r.ItemId, r.BatchNo, FromPick: r.PickBillId.HasValue)))
            {
                decimal groupTotal = g.Sum(r => (r.Num ?? 0m));
                if (g.Key.FromPick)
                {
                    decimal issued = Issued(g.Key.ItemId, g.Key.BatchNo);
                    decimal returned = Returned(g.Key.ItemId, g.Key.BatchNo);
                    if (groupTotal > issued - returned + 0.0001m)
                    {
                        ModelState.AddModelError("Rows", "\u6279\u6b21 " + g.Key.BatchNo + " \u9000\u6599\u5408\u8ba1 " + groupTotal.ToString("0.##")
                            + " \u8d85\u8fc7\u8be5\u6279\u53ef\u9000\u4f59\u91cf " + Math.Max(0m, issued - returned).ToString("0.##") + "\uff01");
                        return false;
                    }
                }
                else
                {
                    decimal onHand = batchQtyMap.TryGetValue(g.Key.BatchNo, out var bq) ? bq : 0m;
                    // 现存量已含已审退料的扣减，这里只补减「未审草稿」的退料量（口径见上方注释）。
                    decimal available = onHand - DraftReturned(g.Key.ItemId, g.Key.BatchNo);
                    if (groupTotal > available + 0.0001m)
                    {
                        ModelState.AddModelError("Rows", "\u6279\u6b21 " + g.Key.BatchNo + " \u9000\u6599\u5408\u8ba1 " + groupTotal.ToString("0.##")
                            + " \u8d85\u8fc7\u8be5\u6279\u53ef\u9000\u4f59\u91cf " + Math.Max(0m, available).ToString("0.##") + "\uff01");
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>Replace-all save: delete old entries then insert new ones, inside a transaction.</summary>
        private void SaveBill(DepartmentReturnEditViewModel vm, t_PMS_StockBill header)
        {
            using (var tx = _db.Database.BeginTransaction())
            {
                var old = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (old.Count > 0) _db.t_PMS_StockBillEntry.RemoveRange(old);

                if (header.FInterID == 0 || !_db.t_PMS_StockBill.Any(h => h.FInterID == header.FInterID))
                    _db.t_PMS_StockBill.Add(header);

                foreach (var r in (vm.Rows ?? new List<DepartmentReturnRowInput>()).Where(r => r.ItemId > 0))
                {
                    var entry = new t_PMS_StockBillEntry
                    {
                        FInterID = header.FInterID,
                        FStepID = r.StepId ?? 0,                 // 工序（t_PMS_Step.FItemID；0=未指定，旧窗体工序名称列）
                        FBomId = 0,
                        FProduceNo = "",
                        FProduceID = 0,
                        FItemID = r.ItemId,
                        FNum = r.Num ?? 0m,                      // legacy single 退料数量 column
                        FNumExt1 = 0m,
                        FNumExt2 = 0m,
                        FNumExt3 = 0m,
                        // price/amount are derived from the source batch (StampBatchPrice below)
                        FNote = string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note.Trim(),
                        FBatchNo = r.BatchNo ?? "",
                        FBatchNoID = BatchInterIdOf(r.BatchNo),
                        FBillUseEntryID = r.PickEntryId ?? 0,    // source dept-pick row (traceability)
                        FBillUseNo = r.PickBillNo ?? "",         // source dept-pick bill no
                        FROB = GONES.Web.Services.StockService.DirIn,   // +1, in-stock (NOT NULL column)
                    };
                    StockService.StampBatchPrice(_db, entry);
                    _db.t_PMS_StockBillEntry.Add(entry);
                }
                _db.SaveChanges();
                tx.Commit();
            }
        }

        /// <summary>Map a batch no to its master FInterID (used to stamp FBatchNoID).</summary>
        private int? BatchInterIdOf(string batchNo)
        {
            if (string.IsNullOrEmpty(batchNo)) return null;
            return _db.t_PMS_BatchNoStock.Where(b => b.FBatchNo == batchNo)
                       .Select(b => (int?)b.FInterID).FirstOrDefault();
        }

        private void BindFormExtras(int? deptId = null, int? workShopId = null)
        {
            ViewBag.DeptOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.ParentID == 1).OrderBy(d => d.ID).ToList();
            ViewBag.WorkShopOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 2 && d.ParentID == deptId).OrderBy(d => d.ID).ToList();
            int scope = workShopId ?? 0;
            ViewBag.Managers = _db.t_PMS_Worker
                .Where(w => w.Status == true && w.DEPID == scope).OrderBy(w => w.ID).ToList();
            var creater = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == CurrentUserId);
            ViewBag.CreaterName = creater?.UserName ?? "";
        }

        // GET /DepartmentReturn/Export?dateFrom=&dateTo=&billNo=&state=
        [HttpGet]
        [HttpGet]
        public IActionResult Export(DateTime? dateFrom, DateTime? dateTo, string billNo, int? state)
        {
            billNo = billNo?.Trim();
            var rows = StockBillExport.BuildRows(_db, _depot, BillType, dateFrom, dateTo, billNo, null, state);
            var cols = new List<string> { "审核", "单据日期", "单据编号", "退料部门", "车间", "退料人", "来源领料单", "批号", "产品代码", "分类", "产品名称", "规格", "单位", "退料数量", "备注" };
            var data = rows.Select(r => new List<string>
            {
                r.State ? "已审" : "未审",
                r.Date?.ToString("yyyy-MM-dd") ?? "",
                r.BillNo ?? "",
                r.DeptName ?? "", r.WorkShopName ?? "", r.ManagerName ?? "",
                r.FromBillUseNo ?? "", r.BatchNo ?? "",
                r.Cpbm ?? "", r.Cplb ?? "", r.Cpjc ?? "", r.Cpgg ?? "", r.UnitName ?? "",
                (r.Num ?? 0m).ToString("0.00"),
                r.Note ?? "",
            }).ToList();
            return ExcelExport.HtmlTable("部门退料单_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".xls", cols, data);
        }
    }
}
