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
    /// Department picking bill (BMLLD) - mirrors the legacy WinForms
    /// Stock.FrmDepartmentPickingManager (t_ERP_Menu.ID=1292). Header
    /// t_PMS_StockBill (FBillType=9) + entries t_PMS_StockBillEntry.
    /// Unlike the warehouse pick bill there is NO import from picking
    /// applications / BOMs: every row is added by hand via the item picker.
    /// Out-stock: FROB = -1, consumes existing batches, never creates batch
    /// master rows. Audit reuses StockService.ApplyOutStock (no BillUse
    /// write-back because department rows carry no FBillUseEntryID link).
    /// Header cascade: department (IsDEP=0) -> workshop (IsDEP=2) -> picker.
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // standard MVC form redisplay, skip Furion 400 short-circuit
    public class DepartmentPickController : Controller, IMenuGuarded
    {
        private const int BillType = 9;
        private const string BillNoPrefix = "BMLLD";
        private const string BillTypeExName = "\u90e8\u95e8\u9886\u6599";   // 部门领料（原「部门领料单」，去「单」统一口径）
        /// <summary>W1 越权提示：读可以少看，动必须整套归你（编辑/审核/反审核/删除共用一份文案）。</summary>
        private const string OutOfScopeMsg = "\u8be5\u5355\u636e\u5305\u542b\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u4ed3\u5e93\u7684\u660e\u7ec6\uff0c\u7981\u6b62\u64cd\u4f5c\uff01";   // 该单据包含不属于您管理仓库的明细，禁止操作！

        private readonly GonesPgDbContext _db;
        private readonly StockService _stock;
        private readonly MenuService _menu;
        private readonly DepotScope _depot;
        public DepartmentPickController(GonesPgDbContext db, StockService stock, MenuService menu, DepotScope depot)
        {
            _db = db;
            _stock = stock;
            _menu = menu;
            _depot = depot;
        }

        private bool IsAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        /// <summary>
        /// 入口闸门：admin 旁路恒 true（行为逐字节不变），非 admin 须由其角色的
        /// t_ERP_RoleMenu 授权覆盖本模块（1292）。原来的 <c>!CanAccess</c> 是"数据权限未实现"的占位。
        /// 部门领料的行级判定只用 item.WarehouseId（仓库维度）——部门维度权限不在范围内（2026-09-12 裁定）。
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
            var sums = sumQ
                .GroupBy(e => 1)
                .Select(g => new { Qty = g.Sum(x => x.FNum ?? 0m) })
                .FirstOrDefault();
            ViewBag.TotalQty = sums?.Qty ?? 0m;

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

            var rows = new List<DepartmentPickListRow>();
            foreach (var h in headers)
            {
                var bills = entriesByBill.ContainsKey(h.FInterID) ? entriesByBill[h.FInterID] : new List<t_PMS_StockBillEntry>();
                var first = true;
                if (bills.Count == 0)
                {
                    rows.Add(new DepartmentPickListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = true, DeptName = DeptName(h.FDeptID), WorkShopName = DeptName(h.FWorkShopID),
                        ManagerName = WorkerName(h.FManagerID),
                        CanModify = !lockedByDepot.Contains(h.FInterID),
                    });
                    continue;
                }
                foreach (var e in bills)
                {
                    t_ERP_ITEM it = null;
                    if (e.FItemID != null && items.ContainsKey(e.FItemID.Value)) it = items[e.FItemID.Value];
                    rows.Add(new DepartmentPickListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = first, DeptName = DeptName(h.FDeptID), WorkShopName = DeptName(h.FWorkShopID),
                        ManagerName = WorkerName(h.FManagerID),
                        CanModify = !lockedByDepot.Contains(h.FInterID),
                        EntryId = e.FEntryID, BatchNo = e.FBatchNo,
                        Cpbm = it?.ItemCode ?? "", Cplb = it?.ProductCategory ?? "", Cpjc = it?.ItemShortName ?? "",
                        Cpgg = it?.ItemSpec ?? "", UnitName = it?.BaseUnit ?? "",
                        Qty = e.FNum, Note = e.FNote,
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
            var vm = new DepartmentPickEditViewModel
            {
                BillNo = NextBillNo(),
                Date = DateTime.Today,
                Rows = new List<DepartmentPickRowInput>(),
            };
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(DepartmentPickEditViewModel vm)
        {

            var items = LoadItems(vm);
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
                    FGroupID = null,                    // department pick has no group level
                    FManagerID = vm.ManagerId,
                    FRemark = vm.Remark ?? "",
                    FCreaterID = CurrentUserId,
                    FROB = -1,                          // red bill (out-stock)
                    FState = false,
                };
                SaveBill(vm, header, items);
            }

            TempData["Success"] = "\u90e8\u95e8\u9886\u6599\u5355\u4fdd\u5b58\u6210\u529f\u3002";   // 部门领料单保存成功。
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

        // GET /DepartmentPick/Details/5  (read-only detail page for audited bills)
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
                .ToDictionary(b => b.FBatchNo, b => b);

            var batchQtyMap = _stock.BatchQtyMap(_db, batchNos);

            // Step names for display (old WinForms form had a 工序名称 column; FStepName
            // is not stored on the entry -- join t_PMS_Step at render time).
            var stepNames = _db.Database.SqlQueryRaw<ProductBomStatStepOption>("SELECT \"f_item_id\" AS \"Id\", \"f_name\" AS Name FROM \"t_pms_step\"")
                .ToDictionary(s => s.Id, s => s.Name ?? "");

            var rows = new List<DepartmentPickRowInput>();
            foreach (var e in entries)
            {
                if (e.FItemID == null || e.FItemID.Value == 0) continue;
                t_ERP_ITEM it = items.ContainsKey(e.FItemID.Value) ? items[e.FItemID.Value] : null;
                t_PMS_BatchNoStock batch = (!string.IsNullOrEmpty(e.FBatchNo) && batches.ContainsKey(e.FBatchNo))
                    ? batches[e.FBatchNo] : null;

                rows.Add(new DepartmentPickRowInput
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
                    OutNum = e.FNum,
                    BatchNo = e.FBatchNo,
                    WarehouseName = WarehouseNameOf(it),
                    Note = e.FNote,
                });
            }

            var vm = new DepartmentPickEditViewModel
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
        public IActionResult Edit(int id, DepartmentPickEditViewModel vm)
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
            if (!ValidateBill(vm, items))
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
            SaveBill(vm, header, items);

            TempData["Success"] = "\u90e8\u95e8\u9886\u6599\u5355\u4fee\u6539\u6210\u529f\u3002";   // 部门领料单修改成功。
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

            TempData["Success"] = "\u90e8\u95e8\u9886\u6599\u5355\u5220\u9664\u6210\u529f\u3002";   // 部门领料单删除成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ audit / unaudit

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Recalc()
        {
            // 全库对账重算属运维操作，admin 专属（服务端强制，与 WarehousePick 同口径）。
            if (!User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1"))
            {
                TempData["Error"] = "\u53ea\u6709\u7ba1\u7406\u5458\u624d\u80fd\u6267\u884c\u5168\u5e93\u5bf9\u8d26\u91cd\u7b97\u3002";   // 只有管理员才能执行全库对账重算。
                return RedirectToAction(nameof(Index));
            }
            int drift = _stock.RecalcAll(_db);
            TempData["Success"] = drift > 0
                ? "\u91cd\u7b97\u5b8c\u6210\uff0c\u5171\u4fee\u6b63 " + drift + " \u5904\u7f13\u5b58\u4e0e\u5355\u636e\u4e0d\u4e00\u81f4\u3002"
                : "\u91cd\u7b97\u5b8c\u6210\uff0c\u7f13\u5b58\u4e0e\u5355\u636e\u8d26\u5b8c\u5168\u4e00\u81f4\u3002";
            return RedirectToAction(nameof(Index));
        }

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
                if (!_stock.ApplyOutStock(_db, header, entries, out string error))
                {
                    TempData["Error"] = error;
                    return RedirectToAction(nameof(Index));       // tx disposed -> rolled back
                }
                // ApplyOutStock 内部已 SaveChanges + header.FState=true；勿再设避免空操作。
                tx.Commit();
            }

            // No t_PMS_BillUse write-back: department pick rows are manual, they never link
            // to a picking application (FBillUseEntryID stays 0).

            TempData["Success"] = "\u5ba1\u6838\u6210\u529f\uff0c\u5e93\u5b58\u5df2\u6309\u6279\u6b21\u51cf\u51cf\u3002";   // 审核成功，库存已按批次递减。
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

            using (var tx = _stock.BeginStockTransaction(_db))
            {
                _stock.RevertOutStock(_db, header, entries);
                // FState 已由 RevertOutStock 内部置 false 并 SaveChanges，此处无需重复赋值。
                tx.Commit();
            }

            TempData["Success"] = "\u53cd\u5ba1\u6838\u6210\u529f\uff0c\u5e93\u5b58\u5df2\u6062\u590d\u3002";   // 反审核成功，库存已恢复。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ lookups

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

        // Step dictionary for the item-picker modal. t_PMS_Step has no EF entity -> SqlQuery
        // (must map to PROPERTIES, EF6 SqlQuery does not map public fields).
        [HttpGet]
        public IActionResult GetSteps()
        {
            var list = _db.Database.SqlQueryRaw<ProductBomStatStepOption>("SELECT \"f_item_id\" AS \"Id\", \"f_name\" AS Name FROM \"t_pms_step\" WHERE COALESCE(\"f_delete\",0) = 0 ORDER BY \"f_item_id\"")   // 禁用工序不进新建选择器
                .ToList();
            return Json(list);
        }

        // Batch info lookup for a row's BatchNo blur: refreshes 批次库存 / 仓库名称.
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

        // Batches with positive derived stock of one item, for the row's batch-number
        // dropdown (ledger truth, not the FBatchNum cache).
        [HttpGet]
        public IActionResult GetItemBatches(int itemId)
        {
            if (itemId <= 0) return Json(new List<object>());
            // 数据闸门②（选择器收窄）：越仓商品不返回批次下拉。
            if (!_depot.AllowsItem(itemId)) return Json(new List<object>());
            var rows = _stock.BatchesOfItem(_db, itemId)
                .Select(r => new { batchNo = r.FBatchNo, qty = r.q })
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

        private Dictionary<int, t_ERP_ITEM> LoadItems(DepartmentPickEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<DepartmentPickRowInput>())
                .Where(r => r.ItemId > 0).Select(r => r.ItemId).Distinct().ToList();
            return _db.t_ERP_ITEM.Where(i => ids.Contains(i.ID)).ToDictionary(i => i.ID);
        }

        private string WarehouseNameOf(t_ERP_ITEM it)
        {
            if (it?.WarehouseId != null && it.WarehouseId.Value > 0)
            {
                return _db.t_ERP_Department
                    .Where(d => d.ID == it.WarehouseId.Value && d.IsDEP == 1)
                    .Select(d => d.DEPName).FirstOrDefault() ?? "";
            }
            return "";
        }

        private bool ValidateBill(DepartmentPickEditViewModel vm, Dictionary<int, t_ERP_ITEM> items)
        {
            if (vm.Date == default || vm.Date.Year < 2000)
            {
                ModelState.AddModelError("Date", "\u8bf7\u586b\u5199\u6b63\u786e\u7684\u5355\u636e\u65e5\u671f\uff01");
                return false;
            }
            var rows = (vm.Rows ?? new List<DepartmentPickRowInput>()).Where(r => r.ItemId > 0).ToList();
            if (rows.Count == 0)
            {
                ModelState.AddModelError("Rows", "\u8bf7\u81f3\u5c11\u6dfb\u52a0\u4e00\u4e2a\u5546\u54c1\uff01");
                return false;
            }
            foreach (var r in rows)
            {
                if (!items.ContainsKey(r.ItemId))
                {
                    ModelState.AddModelError("Rows", "\u6240\u9009\u5546\u54c1\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664\uff0c\u8bf7\u91cd\u65b0\u9009\u62e9\u3002");
                    return false;
                }
                var label = string.IsNullOrWhiteSpace(r.ProductName)
                    ? ("\u7b2c " + (rows.IndexOf(r) + 1) + " \u884c")          // 第 N 行
                    : r.ProductName;
                // Sign check is unconditional (see WarehousePickController.ValidateBill):
                // a negative out-qty would pass the ledger balance check and be recorded with
                // FROB=-1, silently CREDITING the batch -- inventory out of thin air.
                if ((r.OutNum ?? 0m) < 0m)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u51fa\u5e93\u6570\u91cf\u4e0d\u80fd\u4e3a\u8d1f\u6570\uff01");   // 【xx】出库数量不能为负数！
                    return false;
                }
                // Waste rows (category contains 杂质) mirror the warehouse pick rule:
                // zero out-qty and no batch no are allowed (recorded for traceability,
                // returned to stock later via the return module).
                // Category comes from the SERVER-side item master only -- r.CategoryName is a
                // client display field; trusting it would let any row forge the exemption.
                var category = (items.ContainsKey(r.ItemId) ? items[r.ItemId].ProductCategory : null) ?? "";
                bool isWaste = category.Contains("\u6742\u8d28");   // 杂质
                if (!isWaste && (r.OutNum ?? 0) <= 0)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u51fa\u5e93\u6570\u91cf\u5fc5\u987b\u5927\u4e8e 0\uff01");   // 【xx】出库数量必须大于 0！
                    return false;
                }
                if (!isWaste && string.IsNullOrWhiteSpace(r.BatchNo))
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u8bf7\u6307\u5b9a\u6279\u6b21\u3002");
                    return false;
                }
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

            // Batch no arrives as free text: reject a batch that belongs to a different item,
            // otherwise the batch ledger and the item ledger disagree with no way to repair.
            var mismatch = _stock.FindBatchItemMismatch(_db, rows.Select(r => (r.ItemId, r.BatchNo)));
            if (mismatch != null)
            {
                ModelState.AddModelError("Rows", mismatch);
                return false;
            }
            return true;
        }

        /// <summary>Replace-all save: delete old entries then insert new ones, inside a transaction.</summary>
        private void SaveBill(DepartmentPickEditViewModel vm, t_PMS_StockBill header,
            Dictionary<int, t_ERP_ITEM> items)
        {
            using (var tx = _db.Database.BeginTransaction())
            {
                var old = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (old.Count > 0) _db.t_PMS_StockBillEntry.RemoveRange(old);

                if (header.FInterID == 0 || !_db.t_PMS_StockBill.Any(h => h.FInterID == header.FInterID))
                    _db.t_PMS_StockBill.Add(header);

                foreach (var r in (vm.Rows ?? new List<DepartmentPickRowInput>()).Where(r => r.ItemId > 0))
                {
                    var entry = new t_PMS_StockBillEntry
                    {
                        FInterID = header.FInterID,
                        FStepID = r.StepId ?? 0,                 // 工序（t_PMS_Step.FItemID；0=未指定）
                        FBomId = 0,                              // NOT NULL: no BOM linkage
                        FProduceNo = "",
                        FProduceID = 0,
                        FItemID = r.ItemId,
                        FNum = r.OutNum ?? 0m,                   // 出库数量 -> FNum
                        // price/amount are derived from the source batch (StampBatchPrice below)
                        FNote = string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note.Trim(),
                        FBatchNo = r.BatchNo ?? "",
                        FBatchNoID = BatchInterIdOf(r.BatchNo),
                        FBillUseEntryID = 0,                     // manual rows never link to a picking application
                        FBillUseNo = "",
                        FROB = GONES.Web.Services.StockService.DirOut,   // -1, out-stock (NOT NULL column)
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

        // GET /DepartmentPick/Export?dateFrom=&dateTo=&billNo=&state=
        [HttpGet]
        [HttpGet]
        public IActionResult Export(DateTime? dateFrom, DateTime? dateTo, string billNo, int? state)
        {
            billNo = billNo?.Trim();
            var rows = StockBillExport.BuildRows(_db, _depot, BillType, dateFrom, dateTo, billNo, null, state);
            var cols = new List<string> { "审核", "单据日期", "单据编号", "领料部门", "车间", "领料人", "批号", "产品代码", "分类", "产品名称", "规格", "单位", "出库数量", "备注" };
            var data = rows.Select(r => new List<string>
            {
                r.State ? "已审" : "未审",
                r.Date?.ToString("yyyy-MM-dd") ?? "",
                r.BillNo ?? "",
                r.DeptName ?? "", r.WorkShopName ?? "", r.ManagerName ?? "",
                r.BatchNo ?? "",
                r.Cpbm ?? "", r.Cplb ?? "", r.Cpjc ?? "", r.Cpgg ?? "", r.UnitName ?? "",
                (r.Qty ?? 0m).ToString("0.00"),
                r.Note ?? "",
            }).ToList();
            return ExcelExport.HtmlTable("部门领料单_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".xls", cols, data);
        }
    }
}
