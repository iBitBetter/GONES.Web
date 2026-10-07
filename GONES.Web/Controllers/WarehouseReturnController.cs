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
    /// Warehouse RETURN bill (TIN) - mirrors the legacy WinForms
    /// Stock.FrmPickingBackStockManager (\u4ed3\u5e93\u9000\u6599\u8f93\u5165, t_ERP_Menu.ID=1294).
    /// Header t_PMS_StockBill (FBillType=3, FBillTypeEx=\u4ed3\u5e93\u9000\u6599, prefix TIN) +
    /// entries t_PMS_StockBillEntry. In-stock direction: FROB = +1 (DirIn); batch nos are
    /// RE-USED from the source picking bill, so audit only stamps the rows and re-derives
    /// caches (StockService.StampReturnStock) - no batch master rows are created/dropped.
    /// Row qty split: FNum = \u6b63\u5e38\u9000\u6599, FNumExt1 = \u5382\u5bb6\u539f\u56e0,
    /// FNumExt2 = \u4eba\u4e3a\u539f\u56e0, FNumExt3 = \u5176\u5b83\u539f\u56e0 - all stored
    /// positive; the ledger sums FNum + all three Exts (direction from FROB).
    /// Rows are imported from an AUDITED warehouse picking bill (FBillType=8, FState=1).
    /// Trust boundary: PickBillId + PickEntryId locate the source row server-side; ItemId /
    /// BatchNo / PickNum etc. are rederived from it on save. Only the four return quantities
    /// and Note are trusted from the form.
    /// Over-return guard: per (item, batch), audited returns may never exceed audited picks.
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // standard MVC form redisplay, skip Furion 400 short-circuit
    public class WarehouseReturnController : Controller, IMenuGuarded
    {
        private const int BillType = 3;
        private const int PickBillType = 8;
        private const string BillNoPrefix = "TIN";
        private const string BillTypeExName = "\u4ed3\u5e93\u9000\u6599";   // 仓库退料

        /// <summary>W1 越权提示：读可以少看，动必须整套归你（编辑/审核/反审核/删除共用一份文案）。</summary>
        private const string OutOfScopeMsg = "\u8be5\u5355\u636e\u5305\u542b\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u4ed3\u5e93\u7684\u660e\u7ec6\uff0c\u7981\u6b62\u64cd\u4f5c\uff01";   // 该单据包含不属于您管理仓库的明细，禁止操作！

        private readonly GonesPgDbContext _db;
        private readonly StockService _stock;
        private readonly MenuService _menu;
        private readonly DepotScope _depot;
        public WarehouseReturnController(GonesPgDbContext db, StockService stock, MenuService menu, DepotScope depot)
        {
            _db = db;
            _stock = stock;
            _menu = menu;
            _depot = depot;
        }

        private bool IsAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        /// <summary>
        /// 入口闸门：admin 旁路恒 true（行为逐字节不变），非 admin 须由其角色的
        /// t_ERP_RoleMenu 授权覆盖本模块（1294）。原来的 <c>!CanAccess</c> 是"数据权限未实现"的占位。
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
            // 数据闸门①-a（行级读）：只保留"至少有一行明细属于本仓库"的单据。若按表头过滤，
            // 跨仓单据会整张消失（我有 68 的行，却看不到这张单），那行就无处可显。
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

            // grand total over ALL matched bills (server-side aggregate, not a full-table pull)
            var matchedIds = hq.Select(h => h.FInterID);
            // 数据闸门①-b（合计口径）：合计必须与"可见行"同口径，否则把别仓的数量算进来=越权泄露。
            var sumQ = _db.t_PMS_StockBillEntry.Where(e => matchedIds.Contains(e.FInterID));
            if (_depot.IsRestricted) sumQ = _depot.FilterEntries(sumQ);
            ViewBag.TotalQty = sumQ
                .Sum(e => (decimal?)((e.FNum ?? 0m) + (e.FNumExt1 ?? 0m) + (e.FNumExt2 ?? 0m) + (e.FNumExt3 ?? 0m))) ?? 0m;

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
            var groupIds = headers.Where(h => h.FGroupID != null).Select(h => h.FGroupID.Value).Distinct().ToList();
            var groups = _db.t_ERP_Department.Where(d => groupIds.Contains(d.ID)).ToDictionary(d => d.ID, d => d.DEPName);

            string DeptName(int? id) => (id != null && depts.ContainsKey(id.Value)) ? depts[id.Value] : "";
            string WorkerName(int? id) => (id != null && workers.ContainsKey(id.Value)) ? workers[id.Value] : "";
            string GroupName(int? id) => (id != null && groups.ContainsKey(id.Value)) ? groups[id.Value] : "";

            // 数据闸门③-b（按钮灰化）：与服务端 W1 硬拦同源——明细未全部落在仓库范围内的单据
            // 不许修改/审核/删除。只灰按钮而服务端不拦（或反之）都会造成口径分裂。
            var lockedByDepot = _depot.BillsNotFullyInScope(headerIds);

            var rows = new List<WarehouseReturnListRow>();
            foreach (var h in headers)
            {
                var bills = entriesByBill.ContainsKey(h.FInterID) ? entriesByBill[h.FInterID] : new List<t_PMS_StockBillEntry>();
                var first = true;
                if (bills.Count == 0)
                {
                    rows.Add(new WarehouseReturnListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = true, DeptName = DeptName(h.FDeptID), GroupName = GroupName(h.FGroupID), ManagerName = WorkerName(h.FManagerID),
                        CanModify = !lockedByDepot.Contains(h.FInterID),
                    });
                    continue;
                }
                foreach (var e in bills)
                {
                    t_ERP_ITEM it = null;
                    if (e.FItemID != null && items.ContainsKey(e.FItemID.Value)) it = items[e.FItemID.Value];
                    rows.Add(new WarehouseReturnListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = first, DeptName = DeptName(h.FDeptID), GroupName = GroupName(h.FGroupID), ManagerName = WorkerName(h.FManagerID),
                        CanModify = !lockedByDepot.Contains(h.FInterID),
                        FromPickBillNo = e.FBillUseNo,
                        EntryId = e.FEntryID, BatchNo = e.FBatchNo,
                        Cpbm = it?.ItemCode ?? "", Cplb = it?.ProductCategory ?? "", Cpjc = it?.ItemShortName ?? "",
                        Cpgg = it?.ItemSpec ?? "", UnitName = it?.BaseUnit ?? "",
                        Num = e.FNum, NumExt1 = e.FNumExt1, NumExt2 = e.FNumExt2, NumExt3 = e.FNumExt3,
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
            var vm = new WarehouseReturnEditViewModel
            {
                BillNo = NextBillNo(),
                Date = DateTime.Today,
                Rows = new List<WarehouseReturnRowInput>(),
            };
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(WarehouseReturnEditViewModel vm)
        {

            var items = LoadItems(vm);
            if (!DeriveFromPickBill(vm))
            {
                BindFormExtras(vm.DeptId, vm.WorkShopId);
                return View("Form", vm);
            }
            if (!ValidateBill(vm, items))
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
                    FGroupID = vm.GroupId,
                    FManagerID = vm.ManagerId,
                    FRemark = vm.Remark ?? "",
                    FCreaterID = CurrentUserId,
                    FROB = GONES.Web.Services.StockService.DirIn,   // +1, return moves stock back in
                    FState = false,
                };
                SaveBill(vm, header);
            }

            TempData["Success"] = "\u4ed3\u5e93\u9000\u6599\u5355\u4fdd\u5b58\u6210\u529f\u3002";   // 仓库退料单保存成功。
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

        // GET /WarehouseReturn/Details/5 —— 已审单只读查看（与 Edit 同一套装载逻辑）
        public IActionResult Details(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门①-d（行级读）：只读详情只显示本仓库的明细行——"读可以少看"，刻意不施加 W1。
            // 但一行都看不见时整页拒绝，否则表头（单号/部门/备注）会经直接构造 URL 泄露。
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

            // one GROUP BY for every batch in this bill instead of BatchQty per row (N+1)
            var batchQtyMap = _stock.BatchQtyMap(_db, batchNos);

            // source pick rows (locate by FBillUseNo -> pick bill header)
            var pickNos = entries.Select(e => e.FBillUseNo).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            var pickBills = _db.t_PMS_StockBill
                .Where(h => h.FBillType == PickBillType && pickNos.Contains(h.FBillNo))
                .GroupBy(h => h.FBillNo).ToDictionary(g => g.Key, g => g.First().FInterID);   // FBillNo 无唯一约束：防重复单号脏数据 500
            var pickEntries = LoadPickEntries(pickBills.Values.Distinct().ToList());

            var rows = new List<WarehouseReturnRowInput>();
            foreach (var e in entries)
            {
                if (e.FItemID == null || e.FItemID.Value == 0) continue;
                t_ERP_ITEM it = items.ContainsKey(e.FItemID.Value) ? items[e.FItemID.Value] : null;
                t_PMS_BatchNoStock batch = (!string.IsNullOrEmpty(e.FBatchNo) && batches.ContainsKey(e.FBatchNo))
                    ? batches[e.FBatchNo] : null;
                int pickBillId = (!string.IsNullOrEmpty(e.FBillUseNo) && pickBills.ContainsKey(e.FBillUseNo))
                    ? pickBills[e.FBillUseNo] : 0;
                (decimal pickNum, string stepName)? pk =
                    (pickBillId > 0 && e.FBillUseEntryID.HasValue
                          && pickEntries.ContainsKey((pickBillId, e.FBillUseEntryID.Value)))
                    ? pickEntries[(pickBillId, e.FBillUseEntryID.Value)] : null;

                rows.Add(new WarehouseReturnRowInput
                {
                    ItemId = e.FItemID.Value,
                    BomId = e.FBomId,
                    StepId = e.FStepID,
                    ProduceId = e.FProduceID,
                    ProduceNo = e.FProduceNo,
                    StepName = pk?.stepName ?? "",
                    ProductName = it?.ItemShortName ?? "",
                    Spec = it?.ItemSpec ?? "",
                    CategoryName = it?.ProductCategory ?? "",
                    Unit = it?.BaseUnit ?? "",
                    BatchStock = string.IsNullOrEmpty(e.FBatchNo) ? 0m : (batchQtyMap.TryGetValue(e.FBatchNo, out var bq) ? bq : 0m),
                    PickNum = pk?.pickNum,
                    PickBillId = pickBillId > 0 ? pickBillId : (int?)null,
                    PickBillNo = e.FBillUseNo,
                    PickEntryId = e.FBillUseEntryID,
                    Num = e.FNum,
                    NumExt1 = e.FNumExt1,
                    NumExt2 = e.FNumExt2,
                    NumExt3 = e.FNumExt3,
                    BatchNo = e.FBatchNo,
                    WarehouseName = WarehouseNameOf(it),
                    Note = e.FNote,
                });
            }

            var vm = new WarehouseReturnEditViewModel
            {
                InterId = header.FInterID,
                BillNo = header.FBillNo,
                Date = header.FDate ?? DateTime.Today,
                DeptId = header.FDeptID,
                WorkShopId = header.FWorkShopID,
                GroupId = header.FGroupID,
                ManagerId = header.FManagerID,
                Remark = header.FRemark,
                State = header.FState ?? false,
                Rows = rows,
            };
            BindFormExtras(header.FDeptID, header.FWorkShopID, header.FGroupID);
            ViewBag.ReadOnly = readOnly;
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, WarehouseReturnEditViewModel vm)
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

            var items = LoadItems(vm);
            if (!DeriveFromPickBill(vm))
            {
                BindFormExtras(vm.DeptId, vm.WorkShopId);
                return View("Form", vm);
            }
            if (!ValidateBill(vm, items))
            {
                BindFormExtras(vm.DeptId, vm.WorkShopId);
                return View("Form", vm);
            }

            header.FDate = vm.Date;
            header.FDeptID = vm.DeptId;
            header.FWorkShopID = vm.WorkShopId;
            header.FGroupID = vm.GroupId;
            header.FManagerID = vm.ManagerId;
            header.FRemark = vm.Remark ?? "";
            header.FModifyID = CurrentUserId;
            header.FModifyTime = DateTime.Now;
            SaveBill(vm, header);

            TempData["Success"] = "\u4ed3\u5e93\u9000\u6599\u5355\u4fee\u6539\u6210\u529f\u3002";   // 仓库退料单修改成功。
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

            TempData["Success"] = "\u4ed3\u5e93\u9000\u6599\u5355\u5220\u9664\u6210\u529f\u3002";   // 仓库退料单删除成功。
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

                // 来源单复验：保存时校验过「来源领料单必须已审」，但下游锁只认已审退料单——
                // 本单还是草稿时来源领料单可以被反审核/删除。不复查就盖章会按失效来源回增
                // 库存，产生幻影库存（账实不符且可被正常出库变现）。
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

                // 超退复验（分组口径，与 ValidateBill 相同但此刻在 advisory lock 内原子成立）：
                // 保存时的校验是"先查后写"，两张草稿可并发通过；审核是落账前的最后一道闸。
                var checkItemIds = entries.Where(e => e.FItemID.HasValue).Select(e => e.FItemID.Value).Distinct().ToList();
                var issuedMap = StockService.UsageByItemBatch(
                    _db, PickBillType, checkItemIds, null, includeDraft: false, fourColumns: false);
                var returnedMap = StockService.UsageByItemBatch(
                    _db, BillType, checkItemIds, header.FInterID, includeDraft: true, fourColumns: true);
                foreach (var g in entries.Where(e => e.FItemID.HasValue && !string.IsNullOrEmpty(e.FBatchNo))
                                         .GroupBy(e => new { ItemId = e.FItemID.Value, BatchNo = e.FBatchNo }))
                {
                    decimal groupTotal = g.Sum(e => (e.FNum ?? 0m) + (e.FNumExt1 ?? 0m) + (e.FNumExt2 ?? 0m) + (e.FNumExt3 ?? 0m));
                    decimal issued = issuedMap.TryGetValue((g.Key.ItemId, g.Key.BatchNo), out var iq) ? iq : 0m;
                    decimal returned = returnedMap.TryGetValue((g.Key.ItemId, g.Key.BatchNo), out var rq) ? rq : 0m;
                    if (groupTotal > issued - returned + 0.0001m)
                    {
                        TempData["Error"] = "\u6279\u6b21 " + g.Key.BatchNo + " \u9000\u6599\u5408\u8ba1 " + groupTotal.ToString("0.##")
                            + " \u8d85\u8fc7\u8be5\u6279\u53ef\u9000\u4f59\u91cf " + Math.Max(0m, issued - returned).ToString("0.##") + "\uff01";
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

        // ------------------------------------------------------------ picking-bill picker (modal)

        // GET /WarehouseReturn/GetPickBills
        // Audited warehouse picking bills (FBillType=8, FState=1), newest first, top 50.
        [HttpGet]
        public IActionResult GetPickBills()
        {

            var hq = _db.t_PMS_StockBill
                .Where(h => h.FBillType == PickBillType && (h.FState ?? false));
            // 数据闸门②-a（来源单选择器收窄）：只列"至少有一行明细属于本仓库"的领料单。
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
                    qty = g.Sum(x => (x.FNum ?? 0m) + (x.FNumExt1 ?? 0m) + (x.FNumExt2 ?? 0m) + (x.FNumExt3 ?? 0m)),
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

        // GET /WarehouseReturn/GetPickBillDetails?interId=N
        // Every audited picking row that actually moved stock out (has item + batch). Rows are
        // returned ready to become return rows: the operator only fills the four quantities.
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
            var stepIds = entries.Select(e => e.FStepID).Where(s => s > 0).Distinct().ToList();
            var steps = _db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID))
                .ToDictionary(s => s.FItemID, s => s.FName ?? "");

            // one GROUP BY for every batch in this pick bill instead of BatchQty per row
            var batchQtyMap = _stock.BatchQtyMap(_db,
                entries.Select(e => e.FBatchNo).Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList());

            var detail = entries
                .Where(e => e.FItemID != null && e.FItemID.Value > 0)
                // zero-qty waste rows (empty batch) ARE importable: waste is re-entered
                // into stock through this return module, quantities typed by the user.
                .Select(e =>
                {
                    var it = items.ContainsKey(e.FItemID.Value) ? items[e.FItemID.Value] : null;
                    return new
                    {
                        pickBillId = interId,
                        pickBillNo = header.FBillNo ?? "",
                        pickEntryId = e.FEntryID,
                        itemId = e.FItemID.Value,
                        bomId = e.FBomId,
                        stepId = e.FStepID,
                        produceId = e.FProduceID,
                        produceNo = e.FProduceNo ?? "",
                        stepName = steps.ContainsKey(e.FStepID) ? steps[e.FStepID] : "",
                        productName = it?.ItemShortName ?? "",
                        spec = it?.ItemSpec ?? "",
                        categoryName = it?.ProductCategory ?? "",
                        unit = it?.BaseUnit ?? "",
                        batchNo = e.FBatchNo ?? "",
                        batchStock = string.IsNullOrEmpty(e.FBatchNo) ? 0m : (batchQtyMap.TryGetValue(e.FBatchNo, out var bq) ? bq : 0m),
                        // source pick-bill header ids: the form backfills its four
                        // dept/workshop/group/manager selects from them on import
                        deptId = header.FDeptID,
                        workShopId = header.FWorkShopID,
                        groupId = header.FGroupID,
                        managerId = header.FManagerID,
                        pickNum = e.FNum ?? 0m,
                        warehouseName = WarehouseNameOf(it),
                    };
                })
                .ToList();
            return Json(detail);
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
        public IActionResult GetGroups(int workShopId)
        {
            var list = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 3 && d.ParentID == workShopId)
                .OrderBy(d => d.ID).Select(d => new { id = d.ID, name = d.DEPName }).ToList();
            return Json(list);
        }

        [HttpGet]
        public IActionResult GetManagers(int workShopId, [FromQuery] int? groupId = null)
        {
            int scope = (groupId != null && groupId.Value > 0) ? groupId.Value : workShopId;
            var list = _db.t_PMS_Worker
                .Where(w => w.Status == true && w.DEPID == scope)
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

        private Dictionary<int, t_ERP_ITEM> LoadItems(WarehouseReturnEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<WarehouseReturnRowInput>())
                .Where(r => r.ItemId > 0).Select(r => r.ItemId).Distinct().ToList();
            return _db.t_ERP_ITEM.Where(i => ids.Contains(i.ID)).ToDictionary(i => i.ID);
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

        private Dictionary<(int billId, int entryId), (decimal pickNum, string stepName)> LoadPickEntries(
            List<int> pickBillIds)
        {
            var result = new Dictionary<(int, int), (decimal, string)>();
            if (pickBillIds.Count == 0) return result;
            var rows = _db.t_PMS_StockBillEntry
                .Where(e => pickBillIds.Contains(e.FInterID)).ToList();
            var stepIds = rows.Select(e => e.FStepID).Where(s => s > 0).Distinct().ToList();
            var steps = _db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID))
                .ToDictionary(s => s.FItemID, s => s.FName ?? "");
            foreach (var e in rows)
                result[(e.FInterID, e.FEntryID)] = (e.FNum ?? 0m,
                    steps.ContainsKey(e.FStepID) ? steps[e.FStepID] : "");
            return result;
        }

        /// <summary>
        /// Trust boundary: for every row, (PickBillId, PickEntryId) must locate an entry of an
        /// AUDITED picking bill (FBillType=8). ItemId/BomId/StepId/ProduceId/ProduceNo/BatchNo/
        /// PickNum/PickBillNo are all rederived from it; POST values for those are overwritten.
        /// Returns false (ModelState error) when any row's source cannot be resolved.
        /// </summary>
        private bool DeriveFromPickBill(WarehouseReturnEditViewModel vm)
        {
            var rows = (vm.Rows ?? new List<WarehouseReturnRowInput>()).ToList();
            var keys = rows
                .Where(r => r.PickBillId.HasValue && r.PickEntryId.HasValue)
                .Select(r => (r.PickBillId.Value, r.PickEntryId.Value))
                .Distinct().ToList();
            if (keys.Count == 0)
            {
                ModelState.AddModelError("Rows", "\u8bf7\u5148\u4ece\u9886\u6599\u5355\u5bfc\u5165\u9000\u6599\u660e\u7ec6\u3002");   // 请先从领料单导入退料明细。
                return false;
            }

            var billIds = keys.Select(k => k.Item1).Distinct().ToList();
            var pickBills = _db.t_PMS_StockBill
                .Where(h => h.FBillType == PickBillType && (h.FState ?? false) && billIds.Contains(h.FInterID))
                .ToDictionary(h => h.FInterID, h => h);
            var pickEntries = _db.t_PMS_StockBillEntry
                .Where(e => billIds.Contains(e.FInterID)).ToList();
            var pickMap = pickEntries.ToDictionary(e => (e.FInterID, e.FEntryID));
            var stepIds = pickEntries.Select(e => e.FStepID).Where(s => s > 0).Distinct().ToList();
            var steps = _db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID))
                .ToDictionary(s => s.FItemID, s => s.FName ?? "");
            var itemIds = pickEntries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            foreach (var r in rows)
            {
                if (!r.PickBillId.HasValue || !r.PickEntryId.HasValue
                    || !pickBills.ContainsKey(r.PickBillId.Value)
                    || !pickMap.ContainsKey((r.PickBillId.Value, r.PickEntryId.Value)))
                {
                    ModelState.AddModelError("Rows", "\u9000\u6599\u660e\u7ec6\u7684\u6765\u6e90\u9886\u6599\u5355\u884c\u4e0d\u5b58\u5728\u6216\u672a\u5ba1\u6838\uff0c\u8bf7\u91cd\u65b0\u5bfc\u5165\u3002");   // 退料明细的来源领料单行不存在或未审核，请重新导入。
                    return false;
                }
                var pb = pickBills[r.PickBillId.Value];
                var pe = pickMap[(r.PickBillId.Value, r.PickEntryId.Value)];
                var it = (pe.FItemID != null && items.ContainsKey(pe.FItemID.Value)) ? items[pe.FItemID.Value] : null;

                r.ItemId = pe.FItemID ?? 0;
                r.BomId = pe.FBomId;
                r.StepId = pe.FStepID;
                r.ProduceId = pe.FProduceID;
                r.ProduceNo = pe.FProduceNo;
                r.StepName = steps.ContainsKey(pe.FStepID) ? steps[pe.FStepID] : "";
                r.ProductName = it?.ItemShortName ?? "";
                r.Spec = it?.ItemSpec ?? "";
                r.CategoryName = it?.ProductCategory ?? "";
                r.Unit = it?.BaseUnit ?? "";
                r.BatchNo = pe.FBatchNo ?? "";
                r.PickNum = pe.FNum ?? 0m;
                r.PickBillNo = pb.FBillNo ?? "";
            }
            return true;
        }

        private bool ValidateBill(WarehouseReturnEditViewModel vm, Dictionary<int, t_ERP_ITEM> items)
        {
            if (vm.Date == default || vm.Date.Year < 2000)
            {
                ModelState.AddModelError("Date", "\u8bf7\u586b\u5199\u6b63\u786e\u7684\u5355\u636e\u65e5\u671f\uff01");
                return false;
            }
            var rows = (vm.Rows ?? new List<WarehouseReturnRowInput>()).Where(r => r.ItemId > 0).ToList();
            if (rows.Count == 0)
            {
                ModelState.AddModelError("Rows", "\u8bf7\u81f3\u5c11\u5bfc\u5165\u4e00\u4e2a\u5546\u54c1\uff01");
                return false;
            }

            // 数据闸门③-a（写入）：提交行的商品必须全部属于本仓库。admin 旁路不进入此段。
            if (_depot.IsRestricted)
            {
                foreach (var r in rows)
                {
                    if (_depot.AllowsItem(r.ItemId)) continue;
                    var nm = !string.IsNullOrWhiteSpace(r.ProductName)
                        ? r.ProductName
                        : (items.ContainsKey(r.ItemId) ? items[r.ItemId].ItemShortName : null);
                    ModelState.AddModelError("Rows",
                        "\u3010" + (string.IsNullOrWhiteSpace(nm) ? ("\u5546\u54c1 " + r.ItemId) : nm)
                        + "\u3011\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u7684\u4ed3\u5e93\uff0c\u7981\u6b62\u63d0\u4ea4\u3002");   // 【xx】不属于您管理的仓库，禁止提交。
                    return false;
                }
            }

            // Over-return guard, per (item, batch): audited picks must cover all returns.
            // issued = audited out-stock (FBillType=8) —— 已领量是物理事实，草稿未移动库存，
            //                                            故这里保持「仅已审」是正确的。
            // returned = other return bills (FBillType=3, excluding this one)，含草稿。
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
            // 口径与 ProductLoss / ExchangeIn / ProductIn 统一，统一走 StockService 共享层。
            var returnedMap = StockService.UsageByItemBatch(
                _db, BillType, itemIds, vm.InterId, includeDraft: true, fourColumns: true);
            decimal Returned(int itemId, string batchNo)
            {
                return returnedMap.TryGetValue((itemId, batchNo ?? ""), out var q) ? q : 0m;
            }

            foreach (var r in rows)
            {
                var label = string.IsNullOrWhiteSpace(r.ProductName)
                    ? ("\u7b2c " + (rows.IndexOf(r) + 1) + " \u884c")
                    : r.ProductName;
                decimal num = r.Num ?? 0m, ext1 = r.NumExt1 ?? 0m, ext2 = r.NumExt2 ?? 0m, ext3 = r.NumExt3 ?? 0m;
                if (num < 0m || ext1 < 0m || ext2 < 0m || ext3 < 0m)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u9000\u6599\u6570\u91cf\u4e0d\u80fd\u4e3a\u8d1f\u6570\uff01");
                    return false;
                }

                // 厂家原因必须单独退料：填了厂家原因(FNumExt1)，则正常/人为/其它原因必须为空；
                // 反之未填厂家原因时，正常/人为/其它原因可任意组合，但至少填一项。
                if (ext1 > 0.0001m)
                {
                    if (num > 0.0001m || ext2 > 0.0001m || ext3 > 0.0001m)
                    {
                        ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u5382\u5bb6\u539f\u56e0\u9700\u5355\u72ec\u9000\u6599\uff0c\u4e0d\u80fd\u540c\u65f6\u586b\u5199\u6b63\u5e38/\u4eba\u4e3a/\u5176\u5b83\u539f\u56e0\u6570\u91cf\uff01");
                        return false;
                    }
                }
                else
                {
                    if (num <= 0.0001m && ext2 <= 0.0001m && ext3 <= 0.0001m)
                    {
                        ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u672a\u586b\u5199\u5382\u5bb6\u539f\u56e0\u65f6\uff0c\u5fc5\u987b\u586b\u5199\u6b63\u5e38/\u4eba\u4e3a/\u5176\u5b83\u539f\u56e0\u4e2d\u7684\u81f3\u5c11\u4e00\u9879\uff01");
                        return false;
                    }
                }

                decimal rowTotal = num + ext1 + ext2 + ext3;
                if (rowTotal <= 0m)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u56db\u9879\u9000\u6599\u6570\u91cf\u4e4b\u548c\u5fc5\u987b\u5927\u4e8e 0\uff01");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(r.BatchNo))
                {
                    // waste rows (no batch, source pick qty = 0): re-entered as NEW stock,
                    // so neither a batch no nor the over-return guard applies.
                    continue;
                }
            }

            // 超退校验按 (ItemId, BatchNo) 分组合计：同一批次拆两行（或重复提交同一来源行）时，
            // 逐行各自 ≤ 剩余量但合计可超——2026-09-19 审计修复（对齐 ProductLoss/ExchangeIn 的防重复口径）。
            foreach (var g in rows.Where(r => !string.IsNullOrWhiteSpace(r.BatchNo))
                                  .GroupBy(r => (r.ItemId, r.BatchNo)))
            {
                decimal groupTotal = g.Sum(r => (r.Num ?? 0m) + (r.NumExt1 ?? 0m) + (r.NumExt2 ?? 0m) + (r.NumExt3 ?? 0m));
                decimal issued = Issued(g.Key.ItemId, g.Key.BatchNo);
                decimal returned = Returned(g.Key.ItemId, g.Key.BatchNo);
                if (groupTotal > issued - returned + 0.0001m)
                {
                    ModelState.AddModelError("Rows", "\u6279\u6b21 " + g.Key.BatchNo + " \u9000\u6599\u5408\u8ba1 " + groupTotal.ToString("0.##")
                        + " \u8d85\u8fc7\u8be5\u6279\u53ef\u9000\u4f59\u91cf " + Math.Max(0m, issued - returned).ToString("0.##") + "\uff01");
                    return false;
                }
            }
            return true;
        }

        /// <summary>Replace-all save: delete old entries then insert new ones, inside a transaction.</summary>
        private void SaveBill(WarehouseReturnEditViewModel vm, t_PMS_StockBill header)
        {
            using (var tx = _db.Database.BeginTransaction())
            {
                var old = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (old.Count > 0) _db.t_PMS_StockBillEntry.RemoveRange(old);

                if (header.FInterID == 0 || !_db.t_PMS_StockBill.Any(h => h.FInterID == header.FInterID))
                    _db.t_PMS_StockBill.Add(header);

                foreach (var r in (vm.Rows ?? new List<WarehouseReturnRowInput>()).Where(r => r.ItemId > 0))
                {
                    var entry = new t_PMS_StockBillEntry
                    {
                        FInterID = header.FInterID,
                        FStepID = r.StepId ?? 0,
                        FBomId = r.BomId ?? 0,
                        FProduceNo = r.ProduceNo,
                        FProduceID = r.ProduceId,
                        FItemID = r.ItemId,
                        FNum = r.Num ?? 0m,                      // 正常退料 -> FNum
                        FNumExt1 = r.NumExt1 ?? 0m,              // 厂家原因 -> FNumExt1
                        FNumExt2 = r.NumExt2 ?? 0m,              // 人为原因 -> FNumExt2
                        FNumExt3 = r.NumExt3 ?? 0m,              // 其它原因 -> FNumExt3
                        // price/amount are derived from the source batch (StampBatchPrice below)
                        FNote = string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note.Trim(),
                        FBatchNo = r.BatchNo ?? "",
                        FBatchNoID = BatchInterIdOf(r.BatchNo),
                        FBillUseEntryID = r.PickEntryId,         // source picking row (traceability)
                        FBillUseNo = r.PickBillNo ?? "",         // source picking bill no
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

        private void BindFormExtras(int? deptId = null, int? workShopId = null, int? groupId = null)
        {
            ViewBag.DeptOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.ParentID == 1).OrderBy(d => d.ID).ToList();
            ViewBag.WorkShopOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 2 && d.ParentID == deptId).OrderBy(d => d.ID).ToList();
            ViewBag.GroupOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 3 && d.ParentID == workShopId).OrderBy(d => d.ID).ToList();
            int scope = (groupId != null && groupId.Value > 0) ? groupId.Value : (workShopId ?? 0);
            ViewBag.Managers = _db.t_PMS_Worker
                .Where(w => w.Status == true && w.DEPID == scope).OrderBy(w => w.ID).ToList();
            var creater = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == CurrentUserId);
            ViewBag.CreaterName = creater?.UserName ?? "";
        }

        // GET /WarehouseReturn/Export?dateFrom=&dateTo=&billNo=&state=
        [HttpGet]
        [HttpGet]
        public IActionResult Export(DateTime? dateFrom, DateTime? dateTo, string billNo, int? state)
        {
            billNo = billNo?.Trim();
            var rows = StockBillExport.BuildRows(_db, _depot, BillType, dateFrom, dateTo, billNo, null, state);
            var cols = new List<string> { "审核", "单据日期", "单据编号", "来源领料单", "批号", "产品代码", "分类", "产品名称", "规格", "单位", "正常退料", "厂家原因", "人为原因", "其它原因", "合计" };
            var data = rows.Select(r => new List<string>
            {
                r.State ? "已审" : "未审",
                r.Date?.ToString("yyyy-MM-dd") ?? "",
                r.BillNo ?? "",
                r.FromBillUseNo ?? "", r.BatchNo ?? "",
                r.Cpbm ?? "", r.Cplb ?? "", r.Cpjc ?? "", r.Cpgg ?? "", r.UnitName ?? "",
                (r.Num ?? 0m).ToString("0.00"),
                (r.NumExt1 ?? 0m).ToString("0.00"),
                (r.NumExt2 ?? 0m).ToString("0.00"),
                (r.NumExt3 ?? 0m).ToString("0.00"),
                r.RowTotal.ToString("0.00"),
            }).ToList();
            return ExcelExport.HtmlTable("仓库退料单_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".xls", cols, data);
        }
    }
}

