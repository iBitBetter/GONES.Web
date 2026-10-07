using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;
using GONES.Web.Services;
using Microsoft.Extensions.Logging;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// Stock-check (inventory count) bill (CKPDD) - mirrors the legacy WinForms
    /// Stock.FrmStockCheckManager (t_ERP_Menu.ID=1304). Bill-no prefix "CKPDD" is the
    /// legacy BillNoEx used by FrmStockCheckManager (all other migrated bill modules keep
    /// the legacy prefix too). Header t_PMS_StockBill (FBillType=4) + entries.
    /// In-stock: FROB = +1. Each row records a manual physical count (CheckQty);
    /// its 盘点单价 (count price) is the item's factory unit price t_ERP_ITEM.FactoryPrice,
    /// auto-filled when the item is added and forced from the DB on save/audit.
    /// Audit reuses StockService.ApplyInStock, which GENERATES one new batch per
    /// entry with t_PMS_BatchNoStock.FPrice = ccdj (the batch master row is created
    /// here, never by manual input -- the BatchNo column is empty until audited).
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // standard MVC form redisplay, skip Furion 400 short-circuit
    public class StockCheckController : Controller, IMenuGuarded
    {
        private const int BillType = 4;
        // 旧系统 Stock.FrmStockCheckManager 传 BillNoEx="CKPDD"（GetStockBillNoByFBillType 直接
        // 拿它当前缀），web 其他 8 个单据模块的前缀也都与旧系统一致，此处保持同源。
        private const string BillNoPrefix = "CKPDD";
        private const string BillTypeExName = "\u4ed3\u5e93\u76d8\u70b9";   // 仓库盘点（原「仓库盘点单」，去「单」统一口径）
        /// <summary>W1 越权提示：读可以少看，动必须整套归你（编辑/审核/反审核/删除共用一份文案）。</summary>
        private const string OutOfScopeMsg = "\u8be5\u5355\u636e\u5305\u542b\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u4ed3\u5e93\u7684\u660e\u7ec6\uff0c\u7981\u6b62\u64cd\u4f5c\uff01";   // 该单据包含不属于您管理仓库的明细，禁止操作！

        private readonly GonesPgDbContext _db;
        private readonly StockService _stock;
        private readonly ILogger<StockCheckController> _logger;
        public StockCheckController(GonesPgDbContext db, StockService stock, MenuService menu, DepotScope depot, ILogger<StockCheckController> logger)
        {
            _db = db;
            _stock = stock;
            _menu = menu;
            _depot = depot;
            _logger = logger;
        }



        private readonly MenuService _menu;
        private readonly DepotScope _depot;



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
            var dFrom = dateFrom.HasValue ? dateFrom.Value.Date : (DateTime?)null;
            var dTo = dateTo.HasValue ? dateTo.Value.Date.AddDays(1) : (DateTime?)null;
            if (dFrom.HasValue) hq = hq.Where(h => h.FDate >= dFrom.Value);
            if (dTo.HasValue) hq = hq.Where(h => h.FDate < dTo.Value);
            if (!string.IsNullOrEmpty(billNo)) hq = hq.Where(h => h.FBillNo.Contains(billNo));
            // FState 是 bit NULL：必须用 (x ?? false) 归一，直接 == false 会把 NULL 行漏掉
            // （筛「未审核」时历史单据凭空消失）。
            if (state.HasValue && state.Value >= 0) hq = hq.Where(h => (h.FState ?? false) == (state.Value == 1));

            int total = hq.Count();
            page = PagerViewModel.ClampPage(page, total, pageSize);
            var headers = hq.OrderByDescending(h => h.FInterID)
                .Skip((page - 1) * pageSize).Take(pageSize).ToList();
            var headerIds = headers.Select(h => h.FInterID).ToList();

            var matchedIds = hq.Select(h => h.FInterID);
            // 数据闸门①-b（合计口径）：合计必须与"可见行"同口径，否则把别仓的数量算进来=越权泄露。
            var sumQ = _db.t_PMS_StockBillEntry
                .Where(e => matchedIds.Contains(e.FInterID));
            if (_depot.IsRestricted) sumQ = _depot.FilterEntries(sumQ);
            var sums = sumQ
                .GroupBy(e => 1)
                .Select(g => new { Qty = g.Sum(x => x.FPlanNum ?? 0m) })
                .FirstOrDefault();
            ViewBag.TotalQty = sums?.Qty ?? 0m;

            // 数据闸门①-c（明细行收窄）：列表只渲染本仓库的明细行。
            var entryQ = _db.t_PMS_StockBillEntry
                .Where(e => headerIds.Contains(e.FInterID));
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

            // 数据闸门③-b（按钮灰化，与 W1 硬拦同源）：盘点列表视图暂无灰化机制（StockCheckListRow
            // 无 CanModify 字段），此处只算出并挂到 ViewBag，供后续视图接入；服务端 W1 硬拦已生效。
            var lockedByDepot = _depot.BillsNotFullyInScope(headerIds);
            ViewBag.LockedByDepot = lockedByDepot;

            var rows = new List<StockCheckListRow>();
            foreach (var h in headers)
            {
                var bills = entriesByBill.ContainsKey(h.FInterID) ? entriesByBill[h.FInterID] : new List<t_PMS_StockBillEntry>();
                var first = true;
                if (bills.Count == 0)
                {
                    rows.Add(new StockCheckListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = true, DeptName = DeptName(h.FDeptID), WarehouseName = DeptName(h.FWorkShopID), ManagerName = WorkerName(h.FManagerID),
                    });
                    continue;
                }
                foreach (var e in bills)
                {
                    t_ERP_ITEM it = null;
                    if (e.FItemID != null && items.ContainsKey(e.FItemID.Value)) it = items[e.FItemID.Value];
                    rows.Add(new StockCheckListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = first, DeptName = DeptName(h.FDeptID), WarehouseName = DeptName(h.FWorkShopID), ManagerName = WorkerName(h.FManagerID),
                        EntryId = e.FEntryID, BatchNo = e.FBatchNo,
                        Cpbm = it?.ItemCode ?? "", Cplb = it?.ProductCategory ?? "", Cpjc = it?.ItemShortName ?? "",
                        Cpgg = it?.ItemSpec ?? "", UnitName = it?.BaseUnit ?? "",
                        BookStock = it?.StockQuantity, CheckQty = e.FPlanNum, Price = e.FAfterTaxPrice, Note = e.FNote,
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
            var vm = new StockCheckEditViewModel
            {
                BillNo = NextBillNo(),
                Date = DateTime.Today,
                Rows = new List<StockCheckRowInput>(),
            };
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(StockCheckEditViewModel vm)
        {

            var items = LoadItems(vm);
            if (!ValidateBill(vm, items) || !ModelState.IsValid)
            {
                BindFormExtras(vm.DeptId);
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
                    FWorkShopID = vm.WarehouseId,
                    FGroupID = null,
                    FManagerID = vm.ManagerId,
                    FRemark = (vm.Remark ?? "").Trim(),
                    FCreaterID = CurrentUserId,
                    FROB = StockService.DirIn,          // +1, in-stock
                    FState = false,
                };
                if (!SaveBill(vm, header, items))
                {
                    BindFormExtras(vm.DeptId);
                    return View("Form", vm);       // 落库异常已回滚，回显表单
                }
            }

            TempData["Success"] = "\u4ed3\u5e93\u76d8\u70b9\u5355\u4fdd\u5b58\u6210\u529f\u3002";   // 仓库盘点单保存成功。
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

        private IActionResult ViewForm(t_PMS_StockBill header, bool readOnly)
        {
            var id = header.FInterID;
            // 行级读：Details 只显示本仓可见行；Edit 已被 W1 拦截（整单必在本仓），此处过滤为空操作。
            var entryQ = _db.t_PMS_StockBillEntry
                .Where(e => e.FInterID == id);
            if (_depot.IsRestricted) entryQ = _depot.FilterEntries(entryQ);
            var entries = entryQ.OrderBy(e => e.FEntryID).ToList();

            var itemIds = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            var rows = new List<StockCheckRowInput>();
            foreach (var e in entries)
            {
                if (e.FItemID == null || e.FItemID.Value == 0) continue;
                t_ERP_ITEM it = items.ContainsKey(e.FItemID.Value) ? items[e.FItemID.Value] : null;
                rows.Add(new StockCheckRowInput
                {
                    ItemId = e.FItemID.Value,
                    Cpbm = it?.ItemCode ?? "",
                    ProductName = it?.ItemShortName ?? "",
                    Spec = it?.ItemSpec ?? "",
                    CategoryName = it?.ProductCategory ?? "",
                    Unit = it?.BaseUnit ?? "",
                    BookStock = it?.StockQuantity ?? 0m,
                    CheckQty = e.FPlanNum,
                    Price = e.FAfterTaxPrice,
                    BatchNo = e.FBatchNo,
                    WarehouseName = WarehouseNameOf(it),
                    Note = e.FNote,
                });
            }

            var vm = new StockCheckEditViewModel
            {
                InterId = header.FInterID,
                BillNo = header.FBillNo,
                Date = header.FDate ?? DateTime.Today,
                DeptId = header.FDeptID,
                WarehouseId = header.FWorkShopID,
                ManagerId = header.FManagerID,
                Remark = header.FRemark,
                State = header.FState ?? false,
                Rows = rows,
            };
            BindFormExtras(header.FDeptID, header.FWorkShopID);
            ViewBag.ReadOnly = readOnly;
            ViewBag.Derived = LoadDerived(header.FInterID);
            return View("Form", vm);
        }

        // ------------------------------------------------------------ derived bills (read-only)

        /// <summary>盘点派生单（盘盈 FB=7 / 盘亏 FB=10），供盘点单页只读展示。</summary>
        private List<StockCheckDerivedRow> LoadDerived(int sheetInterId)
        {
            var hq = _db.t_PMS_StockBill
                .Where(h => h.FSourceInterID == sheetInterId
                            && (h.FBillType == StockService.StockSurplusBillType
                                || h.FBillType == StockService.StockLossBillType));
            // 数据闸门①-a（行级读）：只保留"至少有一行明细属于本仓库"的派生单。
            if (_depot.IsRestricted) hq = _depot.FilterBills(hq);
            var headers = hq
                .OrderBy(h => h.FInterID)
                .ToList();
            if (headers.Count == 0) return new List<StockCheckDerivedRow>();

            var ids = headers.Select(h => h.FInterID).ToList();
            // 数据闸门①-b（合计口径）：合计只统计本仓可见行，与列表页同口径。
            var qtyQ = _db.t_PMS_StockBillEntry
                .Where(e => ids.Contains(e.FInterID));
            if (_depot.IsRestricted) qtyQ = _depot.FilterEntries(qtyQ);
            var qtyByBill = qtyQ
                .GroupBy(e => e.FInterID)
                .Select(g => new { InterId = g.Key, Qty = g.Sum(x => x.FNum ?? 0m) })
                .ToDictionary(x => x.InterId, x => x.Qty);

            return headers.Select(h => new StockCheckDerivedRow
            {
                InterId = h.FInterID,
                BillNo = h.FBillNo,
                BillType = h.FBillType ?? 0,
                TypeName = h.FBillTypeEx ?? "",
                Date = h.FDate,
                Qty = qtyByBill.ContainsKey(h.FInterID) ? qtyByBill[h.FInterID] : 0m,
            }).ToList();
        }

        /// <summary>派生单只读查看（盘盈 / 盘亏共用一页，不可编辑）。</summary>
        public IActionResult Derived(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id
                && (h.FBillType == StockService.StockSurplusBillType
                    || h.FBillType == StockService.StockLossBillType));
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

            // 行级读：派生单详情只显示本仓可见行。
            var entryQ = _db.t_PMS_StockBillEntry
                .Where(e => e.FInterID == id);
            if (_depot.IsRestricted) entryQ = _depot.FilterEntries(entryQ);
            var entries = entryQ.OrderBy(e => e.FEntryID).ToList();
            var itemIds = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            var rows = entries.Select(e =>
            {
                t_ERP_ITEM it = null;
                if (e.FItemID != null && items.ContainsKey(e.FItemID.Value)) it = items[e.FItemID.Value];
                return new StockCheckDerivedEntryRow
                {
                    Cpbm = it?.ItemCode ?? "",
                    Cpjc = it?.ItemShortName ?? "",
                    Cpgg = it?.ItemSpec ?? "",
                    Unit = it?.BaseUnit ?? "",
                    BatchNo = e.FBatchNo,
                    Qty = e.FNum,
                    Price = e.FAfterTaxPrice,
                };
            }).ToList();

            string sourceBillNo = "";
            if (header.FSourceInterID.HasValue)
            {
                sourceBillNo = _db.t_PMS_StockBill
                    .Where(h => h.FInterID == header.FSourceInterID.Value)
                    .Select(h => h.FBillNo).FirstOrDefault() ?? "";
            }

            return View(new StockCheckDerivedViewModel
            {
                InterId = header.FInterID,
                BillNo = header.FBillNo,
                BillType = header.FBillType ?? 0,
                TypeName = header.FBillTypeEx ?? "",
                Date = header.FDate,
                State = header.FState ?? false,
                Remark = header.FRemark,
                SourceBillNo = sourceBillNo,
                SourceInterId = header.FSourceInterID,
                Rows = rows,
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, StockCheckEditViewModel vm)
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
            if (!ValidateBill(vm, items) || !ModelState.IsValid)
            {
                BindFormExtras(vm.DeptId);
                return View("Form", vm);
            }

            header.FDate = vm.Date;
            header.FDeptID = vm.DeptId;
            header.FWorkShopID = vm.WarehouseId;
            header.FManagerID = vm.ManagerId;
            header.FRemark = (vm.Remark ?? "").Trim();
            header.FModifyID = CurrentUserId;
            header.FModifyTime = DateTime.Now;
            if (!SaveBill(vm, header, items))
            {
                BindFormExtras(vm.DeptId);
                return View("Form", vm);       // 落库异常已回滚，回显表单
            }

            TempData["Success"] = "\u4ed3\u5e93\u76d8\u70b9\u5355\u4fee\u6539\u6210\u529f\u3002";   // 仓库盘点单修改成功。
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

            using (var tx = _db.Database.BeginTransaction())
            {
                var entries = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id).ToList();
                _db.t_PMS_StockBillEntry.RemoveRange(entries);
                _db.t_PMS_StockBill.Remove(header);
                _db.SaveChanges();
                tx.Commit();
            }

            TempData["Success"] = "\u4ed3\u5e93\u76d8\u70b9\u5355\u5220\u9664\u6210\u529f\u3002";   // 仓库盘点单删除成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ audit / unaudit

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Audit(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门③-b（写=整单级 W1）：跨仓单据禁止审核（审核会按实盘−账面派生盘盈/盘亏单，
            // 直接改写别仓库存，必须前置硬拦）。
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
                TempData["Error"] = "\u5355\u636e\u6ca1\u6709\u660e\u7ec6\uff0c\u65e0\u6cd5\u5ba1\u6838\u3002";   // 单据没有明细，无法审核。
                return RedirectToAction(nameof(Index));
            }

            // Force 盘点单价 = 出厂单价 from the DB (never trust client ccdj), so the
            // generated batch's FPrice will equal t_ERP_ITEM.FactoryPrice.
            var itemIds = entries.Where(e => e.FItemID.HasValue).Select(e => e.FItemID.Value).Distinct().ToList();
            var ccdjByItem = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID))
                                          .ToDictionary(i => i.ID, i => (decimal?)i.FactoryPrice);

            // 派生单（盘盈 FB=7 / 盘亏 FB=10）的单号与 FInterID 都是 Max+1：必须在 BillNoLock 内生成。
            // 锁顺序 BillNoLock -> 事务，与 Create/SaveBill 保持一致，反过来会与并发建单互相死锁。
            lock (StockService.BillNoLock)
            using (var tx = _stock.BeginStockTransaction(_db))
            {
                // 并发守卫：BeginStockTransaction 已持有 sp_getapplock，所以此刻重读 DB 才是原子的。
                // 两个「审核」请求同时到达时，先到的已把 FState 置 1；后到的必须在这里被挡下，
                // 否则派生单会被生成两次（库存被重复调整）。
                var freshState = _db.t_PMS_StockBill.Where(h => h.FInterID == id && h.FBillType == BillType)
                                                    .Select(h => h.FState).FirstOrDefault();
                if ((freshState ?? false))
                {
                    // 本请求尚未写入任何行（价格回写也在事务内），直接退出即可回滚。
                    return RedirectToAction(nameof(Index));
                }

                // 价格回写与库存动作同事务：派生抛错时价格一并回滚，
                // 不会留下「单价已改、单据仍未审核」的半成品状态。
                // 盘点单价强制取商品出厂单价（DB 真值）：盘盈单建批次时批次单价即 ccdj。
                foreach (var e in entries)
                {
                    if (e.FItemID.HasValue && ccdjByItem.ContainsKey(e.FItemID.Value))
                        e.FAfterTaxPrice = ccdjByItem[e.FItemID.Value];
                }

                if (!_stock.DeriveStockCheckAudit(_db, header, entries, User.Identity.Name, out string error))
                {
                    TempData["Error"] = error ?? "\u5ba1\u6838\u5931\u8d25\u3002";
                    return RedirectToAction(nameof(Index));       // tx disposed -> rolled back
                }
                tx.Commit();
            }

            TempData["Success"] = "\u5ba1\u6838\u6210\u529f\uff1a\u5df2\u6309\u76d8\u70b9\u5dee\u5f02\u751f\u6210\u76d8\u76c8/\u76d8\u4e8f\u5355\u3002";   // 审核成功：已按盘点差异生成盘盈/盘亏单。
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UnAudit(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门③-b（写=整单级 W1）：跨仓单据禁止反审核（回滚派生单=再次改写别仓库存；
            // 规则置于所有 early return 之前）。
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

            // 同 Audit：回滚派生单会删除其表头/明细（Max+1 号段），必须在 BillNoLock 内。
            lock (StockService.BillNoLock)
            using (var tx = _stock.BeginStockTransaction(_db))
            {
                // 并发守卫（同上）：重读 DB 状态，防止两个反审核请求都通过事务前的检查
                // 而让回滚跑第二遍（第二遍会把已注销批次的明细再擦一次）。
                var freshState = _db.t_PMS_StockBill.Where(h => h.FInterID == id && h.FBillType == BillType)
                                                    .Select(h => h.FState).FirstOrDefault();
                if (!(freshState ?? false))
                {
                    TempData["Error"] = "\u5355\u636e\u5df2\u53cd\u5ba1\u6838\uff0c\u65e0\u9700\u91cd\u590d\u64cd\u4f5c\uff01";   // 单据已反审核，无需重复操作！
                    return RedirectToAction(nameof(Index));
                }

                if (!_stock.DeriveStockCheckUnAudit(_db, header, out string error))
                {
                    TempData["Error"] = error ?? "\u53cd\u5ba1\u6838\u5931\u8d25\u3002";
                    return RedirectToAction(nameof(Index));       // tx disposed -> rolled back
                }
                tx.Commit();
            }

            TempData["Success"] = "\u53cd\u5ba1\u6838\u6210\u529f\uff1a\u76d8\u76c8/\u76d8\u4e8f\u5355\u5df2\u56de\u6eda\uff0c\u5e93\u5b58\u5df2\u6062\u590d\u3002";   // 反审核成功：盘盈/盘亏单已回滚，库存已恢复。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ lookups

        [HttpGet]
        public IActionResult SearchItems(string keyword, int whId = 0)
        {
            // 数据闸门②（选择器收窄）：whId 是客户端可任意构造的参数——指定了越权仓库
            // 直接返回空结果，防止借 whId 搜到别仓商品。
            if (whId > 0 && !_depot.Allows(whId)) return Json(new List<object>());
            var kw = (keyword ?? string.Empty).Trim();
            // 无关键字且未指定仓库 -> 不查询；指定了盘点仓库 -> 即使无关键字也默认带出本仓商品
            if (kw.Length == 0 && whId <= 0) return Json(new List<object>());
            int idMatch = int.TryParse(kw, out var n) ? n : -1;
            // 数据闸门②（选择器收窄）：商品搜索只返回本仓库的商品（未指定 whId 的
            // 关键字搜索同样收窄，关键字搜索不得绕过仓库范围）。
            var q = _depot.FilterItems(_db.t_ERP_ITEM)
                .Where(i => i.IsEnabled == true);
            if (kw.Length > 0)
            {
                q = q.Where(i => (i.ItemCode != null && i.ItemCode.Contains(kw))
                                 || (i.ItemShortName != null && i.ItemShortName.Contains(kw))
                                 || (i.ItemSpec != null && i.ItemSpec.Contains(kw))
                                 || (i.ItemPinyinCode != null && i.ItemPinyinCode.Contains(kw))
                                 || i.ID == idMatch);
            }
            // 指定盘点仓库 -> 只看本仓商品（t_ERP_ITEM.WarehouseId 指向 t_ERP_Department(IsDEP=1).ID），
            // 关键字搜索同样限定在本仓内。
            if (whId > 0)
            {
                q = q.Where(i => i.WarehouseId == whId);
            }
            var list = q.OrderBy(i => i.ItemCode).Take(20)
                .ToList();
            var ckids = list.Where(i => i.WarehouseId != null).Select(i => i.WarehouseId.Value).Distinct().ToList();
            var whNames = _db.t_ERP_Department
                .Where(d => d.IsDEP == 1 && ckids.Contains(d.ID))
                .ToDictionary(d => d.ID, d => d.DEPName ?? "");
            // 仓库名随商品直接带出（item.WarehouseId -> t_ERP_Department(IsDEP=1).DEPName），
            // 选商品落行即有仓库名。
            return Json(list.Select(i => new
            {
                id = i.ID, cpbm = i.ItemCode, cpjc = i.ItemShortName, cpgg = i.ItemSpec,
                cplb = i.ProductCategory, cppym = i.ItemPinyinCode, unit = i.BaseUnit,
                ccdj = i.FactoryPrice,                 // 出厂单价 -> 盘点单价自动带出
                stockNum = i.StockQuantity,        // 账面库存 -> 对照展示
                warehouseName = (i.WarehouseId != null && whNames.ContainsKey(i.WarehouseId.Value)) ? whNames[i.WarehouseId.Value] : "",
            }).ToList());
        }

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
        public IActionResult GetWarehouses(int deptId)
        {
            // 盘点仓库 = IsDEP=1 且挂在所选盘点部门下的部门
            var list = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 1 && d.ParentID == deptId)
                .OrderBy(d => d.ID)
                .Select(d => new { id = d.ID, name = d.DEPName }).ToList();
            return Json(list);
        }

        [HttpGet]
        public IActionResult GetManagers(int whId, int deptId = 0)
        {
            // 盘点人按「盘点仓库」过滤；未选仓库时回退按「盘点部门」过滤。
            int scope = whId > 0 ? whId : deptId;
            if (scope <= 0) return Json(new List<object>());
            var list = _db.t_PMS_Worker
                .Where(w => w.Status == true && w.DEPID == scope)
                .OrderBy(w => w.ID).Select(d => new { id = d.ID, name = d.FName }).ToList();
            return Json(list);
        }

        // ------------------------------------------------------------ helpers

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

        private Dictionary<int, t_ERP_ITEM> LoadItems(StockCheckEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<StockCheckRowInput>())
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

        private bool ValidateBill(StockCheckEditViewModel vm, Dictionary<int, t_ERP_ITEM> items)
        {
            if (vm.Date == default || vm.Date.Year < 2000)
            {
                ModelState.AddModelError("Date", "\u8bf7\u586b\u5199\u6b63\u786e\u7684\u5355\u636e\u65e5\u671f\uff01");
                return false;
            }
            // t_PMS_StockBill.FRemark 是 varchar(1000)：超长会被 SQL Server 直接拒绝
            // （未捕获 -> 500），必须在落库前挡住。
            if ((vm.Remark ?? "").Length > 1000)
            {
                ModelState.AddModelError("Remark", "\u6458\u8981\u957f\u5ea6\u4e0d\u80fd\u8d85\u8fc7 1000 \u4e2a\u5b57\u7b26\uff01");   // 摘要长度不能超过 1000 个字符！
                return false;
            }
            // 盘点部门可空（旧系统 FrmStockCheck 就是 FDeptID=null），但一旦填了必须真实存在，
            // 否则前端 required 被绕过时会写入脏 FK。
            if (vm.DeptId.HasValue && !_db.t_ERP_Department.Any(d => d.ID == vm.DeptId.Value))
            {
                ModelState.AddModelError("DeptId", "\u6240\u9009\u76d8\u70b9\u90e8\u95e8\u4e0d\u5b58\u5728\uff0c\u8bf7\u91cd\u65b0\u9009\u62e9\u3002");   // 所选盘点部门不存在，请重新选择。
                return false;
            }
            // 盘点人来自 t_PMS_Worker（按部门过滤的下拉），伪造 POST 可绕过；只校验存在性，
            // 不强制与所选部门匹配（换部门后仍沿用原盘点人是旧系统的正常用法）。
            if (vm.ManagerId.HasValue && vm.ManagerId.Value > 0
                && !_db.t_PMS_Worker.Any(w => w.ID == vm.ManagerId.Value))
            {
                ModelState.AddModelError("ManagerId", "\u6240\u9009\u76d8\u70b9\u4eba\u4e0d\u5b58\u5728\uff0c\u8bf7\u91cd\u65b0\u9009\u62e9\u3002");   // 所选盘点人不存在，请重新选择。
                return false;
            }

            var rows = (vm.Rows ?? new List<StockCheckRowInput>()).Where(r => r.ItemId > 0).ToList();
            if (rows.Count == 0)
            {
                ModelState.AddModelError("Rows", "\u8bf7\u81f3\u5c11\u6dfb\u52a0\u4e00\u4e2a\u5546\u54c1\uff01");
                return false;
            }
            // 同一商品只允许一行。审核时 DeriveStockCheckAudit 会按 FItemID GroupBy 后
            // **求和** FPlanNum 再与账面比较 —— 重复行等于把同一个商品数了两遍，盘盈/盘亏
            // 数量会静默翻倍。必须在落库前挡住（前端 addRow 也做了同名拦截，此处是硬拦）。
            var dupItems = rows.GroupBy(r => r.ItemId).Where(g => g.Count() > 1)
                               .Select(g => g.First()).ToList();
            if (dupItems.Count > 0)
            {
                var names = string.Join("\u3001", dupItems.Select(r =>
                    string.IsNullOrWhiteSpace(r.ProductName) ? ("\u5546\u54c1" + r.ItemId) : r.ProductName));
                ModelState.AddModelError("Rows",
                    "\u540c\u4e00\u5546\u54c1\u4e0d\u80fd\u91cd\u590d\u76d8\u70b9\uff0c\u8bf7\u5148\u5408\u5e76\u540e\u518d\u4fdd\u5b58\uff01\u3010" + names + "\u3011");
                // 同一商品不能重复盘点，请先合并后再保存！【xx】
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
                    ? ("\u7b2c " + (rows.IndexOf(r) + 1) + " \u884c")
                    : r.ProductName;
                // 实盘数允许 0（盘亏到零是合法场景），但不允许为空/负数：
                // 空值会让「没盘到」静默变成「盘成 0」，直接把该商品库存全部盘亏掉。
                if (!r.CheckQty.HasValue)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u8bf7\u586b\u5199\u5b9e\u76d8\u6570\u91cf\uff01");   // 【xx】请填写实盘数量！
                    return false;
                }
                if (r.CheckQty.Value < 0)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u76d8\u70b9\u6570\u91cf\u4e0d\u80fd\u4e3a\u8d1f\u6570\uff01");   // 【xx】盘点数量不能为负数！
                    return false;
                }
                // t_PMS_StockBillEntry.FNote 同为 varchar(1000)。
                if ((r.Note ?? "").Length > 1000)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u5907\u6ce8\u957f\u5ea6\u4e0d\u80fd\u8d85\u8fc7 1000 \u4e2a\u5b57\u7b26\uff01");   // 【xx】备注长度不能超过 1000 个字符！
                    return false;
                }
            }

            // 数据闸门③-a（写入）：所选盘点仓库必须落在本人管理范围内。admin 旁路不进入此段。
            if (_depot.IsRestricted && vm.WarehouseId.HasValue && !_depot.Allows(vm.WarehouseId))
            {
                ModelState.AddModelError("WarehouseId",
                    "\u6240\u9009\u76d8\u70b9\u4ed3\u5e93\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u7684\u4ed3\u5e93\uff0c\u7981\u6b62\u63d0\u4ea4\u3002");   // 所选盘点仓库不属于您管理的仓库，禁止提交。
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

            // 归属校验：明细商品的归属仓库（t_ERP_ITEM.WarehouseId，与其他模块的商品归仓判定同源）
            // 必须等于所选盘点仓库 WarehouseId —— 防止 A 仓盘点单里塞 B 仓商品（审核派生
            // 盘盈/盘亏会直接改写 B 仓库存）。ckid 为空的商品无法证明归属所选仓库 ⇒ 拒绝。
            // 未选仓库（WarehouseId 可空，沿用旧行为）时不比较，但越仓商品仍被上方闸门拦下。
            if (vm.WarehouseId.HasValue)
            {
                foreach (var r in rows)
                {
                    var it = items[r.ItemId];
                    if ((it.WarehouseId ?? 0) != vm.WarehouseId.Value)
                    {
                        var nm = !string.IsNullOrWhiteSpace(r.ProductName)
                            ? r.ProductName
                            : (items.ContainsKey(r.ItemId) ? items[r.ItemId].ItemShortName : null);
                        ModelState.AddModelError("Rows",
                            "\u3010" + (string.IsNullOrWhiteSpace(nm) ? ("\u5546\u54c1 " + r.ItemId) : nm)
                            + "\u3011\u4e0d\u5c5e\u4e8e\u6240\u9009\u76d8\u70b9\u4ed3\u5e93\uff0c\u7981\u6b62\u63d0\u4ea4\u3002");   // 【xx】不属于所选盘点仓库，禁止提交。
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>Replace-all save: delete old entries then insert new ones, inside a transaction.</summary>
        /// <summary>
        /// 落库（表头 + 明细，单事务）。异常（varchar 超长、外键、唯一约束等）不再冒泡成 500，
        /// 而是记录日志 + 回滚 + 返回 false，由调用方回显表单。事务未 Commit 时由 using 自动回滚；
        /// 这里刻意不手动 Rollback —— 二次 Rollback 会抛 InvalidOperationException。
        /// </summary>
        private bool SaveBill(StockCheckEditViewModel vm, t_PMS_StockBill header,
            Dictionary<int, t_ERP_ITEM> items)
        {
            using (var tx = _db.Database.BeginTransaction())
            {
                try
                {
                var old = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (old.Count > 0) _db.t_PMS_StockBillEntry.RemoveRange(old);

                if (header.FInterID == 0 || !_db.t_PMS_StockBill.Any(h => h.FInterID == header.FInterID))
                    _db.t_PMS_StockBill.Add(header);

                foreach (var r in (vm.Rows ?? new List<StockCheckRowInput>()).Where(r => r.ItemId > 0))
                {
                    // 盘点单价强制取商品出厂单价（DB 真值），忽略前端传入值，保证批次 FPrice=ccdj。
                    decimal? price = items.ContainsKey(r.ItemId) ? (decimal?)items[r.ItemId].FactoryPrice : null;
                    var entry = new t_PMS_StockBillEntry
                    {
                        FInterID = header.FInterID,
                        FStepID = 0,                             // NOT NULL: no step linkage
                        FBomId = 0,                              // NOT NULL: no BOM linkage
                        FProduceNo = "",
                        FProduceID = 0,
                        FItemID = r.ItemId,
                        // 盘点单是「工作表」：实盘数量记在 FPlanNum，FNum 恒为 0 —— 这样这一行对账本
                        // 零贡献（审核时由派生出的盘盈/盘亏单真正移动库存），也让重复盘点可幂等。
                        // 口径沿用旧系统 FrmStockCheck.cs:418（结存数量写入 FPlanNum）。
                        FPlanNum = r.CheckQty ?? 0m,
                        FNum = 0m,
                        FAfterTaxPrice = price,                  // = ccdj；ApplyInStock 写入批次 FPrice
                        FNote = string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note.Trim(),
                        // 批号由审核生成：建单时留空（手工指定也会被 ApplyInStock 覆盖）。
                        FBatchNo = "",
                        FBatchNoID = null,
                        FBillUseEntryID = 0,
                        FBillUseNo = "",
                        FROB = StockService.DirIn,               // +1, in-stock (NOT NULL column)
                    };
                    _db.t_PMS_StockBillEntry.Add(entry);
                }
                _db.SaveChanges();
                tx.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    // 未 Commit -> using Dispose 自动回滚（勿手动 Rollback，二次回滚会抛异常）。
                    _logger.LogError(ex, "盘点单保存失败 InterId={InterId}", header.FInterID);
                    ModelState.AddModelError("", "保存失败，数据已回滚，请检查填写内容后重试。");
                    return false;
                }
            }
        }

        private void BindFormExtras(int? deptId = null, int? warehouseId = null)
        {
            ViewBag.DeptOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.ParentID == 1).OrderBy(d => d.ID).ToList();
            int dept = deptId ?? 0;
            // 盘点仓库（IsDEP=1）挂在某盘点部门(ParentID=dept)名下，随部门联动。
            ViewBag.WarehouseOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 1 && d.ParentID == dept)
                .OrderBy(d => d.ID).ToList();
            // 盘点人按「盘点仓库」过滤；未选仓库时回退按「盘点部门」过滤。
            int scope = (warehouseId ?? 0) > 0 ? (warehouseId ?? 0) : dept;
            ViewBag.Managers = _db.t_PMS_Worker
                .Where(w => w.Status == true && w.DEPID == scope).OrderBy(w => w.ID).ToList();
            var creater = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == CurrentUserId);
            ViewBag.CreaterName = creater?.UserName ?? "";
        }

        // GET /StockCheck/Export?dateFrom=&dateTo=&billNo=&state=
        [HttpGet]
        [HttpGet]
        public IActionResult Export(DateTime? dateFrom, DateTime? dateTo, string billNo, int? state)
        {
            billNo = billNo?.Trim();
            var rows = StockBillExport.BuildRows(_db, _depot, BillType, dateFrom, dateTo, billNo, null, state);
            var cols = new List<string> { "审核", "单据日期", "单据编号", "盘点部门", "盘点仓库", "盘点人", "批号", "产品代码", "分类", "产品名称", "规格", "单位", "账面库存", "实盘数量", "盘点单价", "备注" };
            var data = rows.Select(r => new List<string>
            {
                r.State ? "已审" : "未审",
                r.Date?.ToString("yyyy-MM-dd") ?? "",
                r.BillNo ?? "",
                r.DeptName ?? "", r.WarehouseName ?? "", r.ManagerName ?? "",
                r.BatchNo ?? "",
                r.Cpbm ?? "", r.Cplb ?? "", r.Cpjc ?? "", r.Cpgg ?? "", r.UnitName ?? "",
                (r.BookStock ?? 0m).ToString("0.00##"),
                (r.CheckQty ?? 0m).ToString("0.00"),
                (r.CheckPrice ?? 0m).ToString("0.00##"),
                r.Note ?? "",
            }).ToList();
            return ExcelExport.HtmlTable("盘点单_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".xls", cols, data);
        }
    }
}
