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
    /// Purchase in-stock bill (CGRKD) - old WinForms Stock.FrmPurchaseInStockManager / FrmPurchaseInStock.
    /// Header t_PMS_StockBill (FBillType=0) + entries t_PMS_StockBillEntry.
    ///
    /// New-world rules (user decisions 2026-09-06):
        ///   - FDeptID (入库部门) / FWorkShopID (入库车间) from t_ERP_Department; dept is level-1
        ///     (ParentID=1), workshop is its IsDEP=2 children, manager is t_PMS_Worker.DEPID=workshop.
    ///   - FManagerID (入库人) from t_PMS_Worker (Status=1).
    ///   - FBuyingUnit (供货单位) from t_ERP_DataDict FParentID=4 (码表「供货单位」分类节点), stored as DictName.
    ///   - Tax rate from dict FParentID=2 (13%/9%/6%); FTaxRate=name, FTaxRateValue=decimal.
    ///     FAmount=Qty*Price; FAfterTaxAmount=FAmount/(1+rate); FTaxAmount=FAmount-FAfterTaxAmount;
    ///     FAfterTaxPrice=FPrice/(1+rate).
    ///   - Audit (StockService.ApplyInStock) registers one batch MASTER row per entry: batch no
    ///     yyyyMMdd + class(2) + daily seq(3), stamps FBatchNo / FBatchNoID (= batch FInterID) and
    ///     FROB=+1 on the bill rows, then RE-DERIVES t_ERP_ITEM.StockQuantity and the batch balance
    ///     from the ledger. t_PMS_BatchNoStock.FBatchNum is a CACHE (rebuilt, never hand-written;
    ///     t_PMS_BatchNoStockEntry was dropped 2026-09-07).
    ///     Un-audit refuses once a batch was consumed downstream, otherwise clears the stamp and
    ///     re-derives. xnkc keeps its old "virtual stock" meaning and is never written here.
    ///
    /// Old rules kept: FBillNo "CGRKD"+8 digits (max+1 within FBillType=0); FInterID max+1;
    /// FROB=1 (blue); FBillTypeEx="采购入库"; delete = entries + header in one pass
    /// (tri_deleteStockBillEntry trigger was dropped 2026-09-09: entry cleanup is app-owned).
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // standard MVC form redisplay, skip Furion 400 short-circuit
    public class StockBillController : Controller, IMenuGuarded
    {
        private const int BillType = 0;
        private const string BillNoPrefix = "CGRKD";
        private const string BillTypeExName = "\u91c7\u8d2d\u5165\u5e93";   // 采购入库（原「采购入库单」，去「单」统一口径）

        private readonly GonesPgDbContext _db;
        private readonly StockService _stock;
        private readonly MenuService _menu;
        private readonly DepotScope _depot;

        public StockBillController(GonesPgDbContext db, StockService stock, MenuService menu, DepotScope depot)
        {
            _db = db;
            _stock = stock;
            _menu = menu;
            _depot = depot;
        }


        /// <summary>
        /// 仓库数据权限（W1）越权提示语。与另 8 个单据模块逐字一致：
        /// 整单明细只要有一行不属于当前用户管理的仓库，就禁止任何写操作。
        /// </summary>
        private const string OutOfScopeMsg = "\u8be5\u5355\u636e\u5305\u542b\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u4ed3\u5e93\u7684\u660e\u7ec6\uff0c\u7981\u6b62\u64cd\u4f5c\uff01";   // 该单据包含不属于您管理仓库的明细，禁止操作！


        private bool IsAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        /// <summary>整单是否全部落在当前用户的仓库范围内（admin 恒 true）。</summary>
        private bool InDepotScope(int id) => _depot.BillFullyInScope(id);

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

        // GET /StockBill?dateFrom=&dateTo=&billNo=&pym=&state=&page=&pageSize=
        public IActionResult Index(DateTime? dateFrom, DateTime? dateTo, string billNo, string pym,
            int? state, int page = 1, int pageSize = 20)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);
            billNo = billNo?.Trim();
            pym = pym?.Trim();

            var hq = BuildQuery(dateFrom, dateTo, billNo, pym, state);
            int total = hq.Count();                     // number of BILLS, not rows
            page = PagerViewModel.ClampPage(page, total, pageSize);
            var headers = hq.OrderByDescending(h => h.FInterID)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();
            var headerIds = headers.Select(h => h.FInterID).ToList();

            // 仓库数据权限③-b（按钮灰化）：与服务端 W1 硬拦同源，见 DepotScope.BillsNotFullyInScope。
            var lockedByDepot = _depot.BillsNotFullyInScope(headerIds);

            // grand totals over ALL bills matching the filter (old grid footer style)
            var matchedIds = hq.Select(h => h.FInterID);
            var sums = _db.t_PMS_StockBillEntry
                .Where(e => matchedIds.Contains(e.FInterID))
                .GroupBy(e => 1)
                .Select(g => new { Qty = g.Sum(x => x.FNum ?? 0m), Amount = g.Sum(x => x.FAmount ?? 0m) })
                .FirstOrDefault();
            ViewBag.TotalQty = sums?.Qty ?? 0m;
            ViewBag.TotalAmount = sums?.Amount ?? 0m;

            var rows = BuildRows(headers, lockedByDepot);

            ViewBag.Filter = new Dictionary<string, string>
            {
                ["dateFrom"] = dateFrom?.ToString("yyyy-MM-dd") ?? "",
                ["dateTo"] = dateTo?.ToString("yyyy-MM-dd") ?? "",
                ["billNo"] = billNo ?? "",
                ["pym"] = pym ?? "",
                ["state"] = (!state.HasValue || state.Value < 0) ? "-1" : state.Value.ToString(),
            };

            var extra = new Dictionary<string, object>();
            if (dateFrom.HasValue) extra["dateFrom"] = dateFrom.Value.ToString("yyyy-MM-dd");
            if (dateTo.HasValue) extra["dateTo"] = dateTo.Value.ToString("yyyy-MM-dd");
            if (!string.IsNullOrEmpty(billNo)) extra["billNo"] = billNo;
            if (!string.IsNullOrEmpty(pym)) extra["pym"] = pym;
            if (state.HasValue && state.Value >= 0) extra["state"] = state.Value;
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

        // ------------------------------------------------------------ item pick modal

        // GET /StockBill/ListItems?keyword=&cplb=
        // Modal bulk item picker. Default (cplb empty) lists every active item EXCEPT
        // 产成品 (finished goods are stocked in through 生产入库, not purchase);
        // picking an explicit category (including 产成品) filters to it.
        [HttpGet]
        public IActionResult ListItems(string keyword, string cplb)
        {

            var kw = (keyword ?? string.Empty).Trim();
            int idMatch = int.TryParse(kw, out var n) ? n : -1;

            // 数据闸门②-c（选择器）：商品弹窗只能搜到本仓库的商品（与另 8 个单据模块同口径）。
            var q = _depot.FilterItems(_db.t_ERP_ITEM).Where(i => i.IsEnabled == true);
            if (string.IsNullOrEmpty(cplb))
                q = q.Where(i => i.ProductCategory != "产成品");          // default: everything but finished goods
            else
                q = q.Where(i => i.ProductCategory == cplb);

            if (kw.Length > 0)
                q = q.Where(i => (i.ItemCode != null && i.ItemCode.Contains(kw))
                              || (i.ItemShortName != null && i.ItemShortName.Contains(kw))
                              || (i.ItemSpec != null && i.ItemSpec.Contains(kw))
                              || (i.ItemPinyinCode != null && i.ItemPinyinCode.Contains(kw))
                              || i.ID == idMatch);

            var list = q
                .OrderBy(i => i.ItemCode)
                .Take(200)
                .Select(i => new
                {
                    id = i.ID,
                    cpbm = i.ItemCode,
                    cpjc = i.ItemShortName,
                    cpgg = i.ItemSpec,
                    cplb = i.ProductCategory,
                    cppym = i.ItemPinyinCode,
                    unit = i.BaseUnit,
                    price = i.CostPrice ?? 0m,
                })
                .ToList();
            return Json(list);
        }

        // GET /StockBill/GetWorkShops?deptId=65
        [HttpGet]
        public IActionResult GetWorkShops(int deptId)
        {
            var list = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 1 && d.ParentID == deptId)
                .OrderBy(d => d.ID)
                .Select(d => new { id = d.ID, name = d.DEPName })
                .ToList();
            return Json(list);
        }

        // GET /StockBill/GetManagers?workShopId=66
        [HttpGet]
        public IActionResult GetManagers(int workShopId)
        {
            var list = _db.t_PMS_Worker
                .Where(w => w.Status == true && w.DEPID == workShopId)
                .OrderBy(w => w.ID)
                .Select(w => new { id = w.ID, name = w.FName })
                .ToList();
            return Json(list);
        }

        // ------------------------------------------------------------ create

        // GET /StockBill/Create
        [HttpGet]
        public IActionResult Create()
        {

            BindFormExtras();
            var vm = new StockBillEditViewModel
            {
                BillNo = NextBillNo(),
                Date = DateTime.Today,
                Rows = new List<StockBillRowInput>(),
            };
            return View("Form", vm);
        }

        // POST /StockBill/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(StockBillEditViewModel vm)
        {

            var items = LoadItems(vm);
            if (!ValidateBill(vm, items))
            {
                BindFormExtras();
                BindRowInfo(vm);
                return View("Form", vm);
            }

            // Generate bill no + inter id AND persist under one lock so concurrent
            // creates cannot read the same max and collide on the primary key.
            lock (StockService.BillNoLock)
            {
                var header = new t_PMS_StockBill
                {
                    FInterID = NextInterId(),
                    FBillNo = NextBillNo(),          // regenerate on save (old behavior)
                    FBillType = BillType,
                    FBillTypeEx = BillTypeExName,
                    FDate = vm.Date,
                    FDeptID = vm.DeptId,
                    FWorkShopID = vm.WorkShopId,
                    FManagerID = vm.ManagerId,
                    FBuyingUnit = vm.BuyingUnit ?? "",
                    FInspectors = vm.Inspectors ?? "",
                    FRemark = vm.Remark ?? "",
                    FCreaterID = CurrentUserId,
                    FROB = 1,                        // blue bill
                    FState = false,
                    FModifyID = null,
                    FModifyTime = null,
                };
                SaveBill(vm, header, items);
            }

            TempData["Success"] = "\u91c7\u8d2d\u5165\u5e93\u5355\u4fdd\u5b58\u6210\u529f\u3002";   // 采购入库单保存成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ edit

        // GET /StockBill/Edit/5 (FInterID)
        [HttpGet]
        public IActionResult Edit(int id)
        {

            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            if ((header.FState ?? false))
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff0c\u7981\u6b62\u4fee\u6539\uff01";   // 单据已审核，禁止修改！
                return RedirectToAction(nameof(Index));
            }
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            return ViewForm(header, readOnly: false);
        }

        // GET /StockBill/Details/5 —— 已审单只读查看（与 Edit 同一套装载逻辑）
        public IActionResult Details(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            return ViewForm(header, readOnly: true);
        }

        private IActionResult ViewForm(t_PMS_StockBill header, bool readOnly)
        {
            var id = header.FInterID;
            var rows = _db.t_PMS_StockBillEntry
                .Where(e => e.FInterID == id)
                .OrderBy(e => e.FEntryID)
                .ToList()
                .Select(e => new StockBillRowInput
                {
                    ItemId = e.FItemID ?? 0,
                    Qty = e.FNum,
                    Price = e.FPrice,
                    TaxRateId = ResolveTaxRateId(e.FTaxRate),
                    Note = e.FNote,
                })
                .Where(r => r.ItemId > 0)
                .ToList();
            var vm = new StockBillEditViewModel
            {
                InterId = header.FInterID,
                BillNo = header.FBillNo,
                Date = header.FDate ?? DateTime.Today,
                DeptId = header.FDeptID,
                WorkShopId = header.FWorkShopID,
                ManagerId = header.FManagerID,
                BuyingUnit = header.FBuyingUnit,
                Inspectors = header.FInspectors,
                Remark = header.FRemark,
                State = header.FState ?? false,
                Rows = rows,
            };
            BindFormExtras(header.FDeptID, header.FWorkShopID);
            BindRowInfo(vm);
            var creater = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == header.FCreaterID);
            var modifier = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == header.FModifyID);
            ViewBag.CreaterName = creater?.UserName ?? "";
            ViewBag.ModifyName = (header.FModifyID != null) ? (modifier?.UserName ?? "") : "";
            ViewBag.ReadOnly = readOnly;
            return View("Form", vm);
        }

        // POST /StockBill/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, StockBillEditViewModel vm)
        {

            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            if ((header.FState ?? false))
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff0c\u7981\u6b62\u4fee\u6539\uff01";
                return RedirectToAction(nameof(Index));
            }
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }

            vm.InterId = id;
            vm.BillNo = header.FBillNo;   // bill no is immutable

            var items = LoadItems(vm);
            if (!ValidateBill(vm, items))
            {
                BindFormExtras(vm.DeptId, vm.WorkShopId);
                BindRowInfo(vm);
                return View("Form", vm);
            }

            header.FDate = vm.Date;
            header.FDeptID = vm.DeptId;
            header.FWorkShopID = vm.WorkShopId;
            header.FManagerID = vm.ManagerId;
            header.FBuyingUnit = vm.BuyingUnit ?? "";
            header.FInspectors = vm.Inspectors ?? "";
            header.FRemark = vm.Remark ?? "";
            header.FModifyID = CurrentUserId;
            header.FModifyTime = DateTime.Now;
            SaveBill(vm, header, items);

            TempData["Success"] = "\u91c7\u8d2d\u5165\u5e93\u5355\u4fee\u6539\u6210\u529f\u3002";   // 采购入库单修改成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ delete (whole bill)

        // POST /StockBill/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {

            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            if ((header.FState ?? false))
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff0c\u7981\u6b62\u5220\u9664\uff01";   // 单据已审核，禁止删除！
                return RedirectToAction(nameof(Index));
            }
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }

            // tri_deleteStockBillEntry trigger dropped (2026-09-09): entry cleanup is fully
            // app-owned, so entries + header go in a single SaveChanges inside the transaction.
            using (var tx = _db.Database.BeginTransaction())
            {
                var entries = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id).ToList();
                _db.t_PMS_StockBillEntry.RemoveRange(entries);
                _db.t_PMS_StockBill.Remove(header);
                _db.SaveChanges();
                tx.Commit();
            }

            TempData["Success"] = "\u91c7\u8d2d\u5165\u5e93\u5355\u5220\u9664\u6210\u529f\u3002";   // 采购入库单删除成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ audit / unaudit

        // POST /StockBill/Recalc (repair: rebuild batch/item caches from the ledger)
        [HttpPost]
        [ValidateAntiForgeryToken]
        /// <summary>
        /// Admin repair action: rebuild every batch / item cache from the bill ledger and report
        /// how many cache rows were out of sync. Use after direct SQL fixes or data imports.
        /// </summary>
        public IActionResult Recalc()
        {
            // 服务端强制 admin（此前只写在 XML 注释里）：RecalcAll 是**全局**（跨全部仓库）的
            // 库存缓存重算，属于运维动作，非 admin 可触发会造成全表 UPDATE 压力。
            if (!IsAdmin)
            {
                TempData["Error"] = "\u53ea\u6709\u7ba1\u7406\u5458\u624d\u80fd\u6267\u884c\u5168\u5e93\u5bf9\u8d26\u91cd\u7b97\u3002";   // 只有管理员才能执行全库对账重算。
                return RedirectToAction(nameof(Index));
            }

            int drift = _stock.RecalcAll(_db);
            TempData["Success"] = drift > 0
                ? "\u91cd\u7b97\u5b8c\u6210\uff0c\u5171\u4fee\u6b63 " + drift + " \u5904\u7f13\u5b58\u4e0e\u5355\u636e\u4e0d\u4e00\u81f4\u3002"   // 重算完成，共修正 N 处缓存与单据不一致。
                : "\u91cd\u7b97\u5b8c\u6210\uff0c\u7f13\u5b58\u4e0e\u5355\u636e\u8d26\u5b8c\u5168\u4e00\u81f4\u3002";   // 重算完成，缓存与单据账完全一致。
            return RedirectToAction(nameof(Index));
        }

        // POST /StockBill/Audit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Audit(int id)
        {

            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            if ((header.FState ?? false))
            {
                TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff0c\u65e0\u9700\u91cd\u590d\u5ba1\u6838\uff01";   // 单据已审核，无需重复审核！
                return RedirectToAction(nameof(Index));
            }
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
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

            // Audit = create batches + raise t_ERP_ITEM.StockQuantity + write batch balance/ledger.
            // Whole action in one transaction: ApplyInStock stamps FROB, inserts the batch
            // master rows and re-derives the caches. A crash between those steps used to leave
            // the caches contradicting the ledger with nothing to detect it.
            using (var tx = _stock.BeginStockTransaction(_db))
            {
                if (!_stock.ApplyInStock(_db, header, entries, User.Identity.Name, out string error))
                {
                    TempData["Error"] = error;
                    return RedirectToAction(nameof(Index));       // tx disposed -> rolled back
                }
                // ApplyInStock 内部已 SaveChanges + header.FState=true；勿再设避免空操作。
                tx.Commit();
            }

            TempData["Success"] = "\u5ba1\u6838\u6210\u529f\uff0c\u5df2\u751f\u6210\u6279\u6b21\u5e76\u66f4\u65b0\u5e93\u5b58\u3002";   // 审核成功，已生成批次并更新库存。
            return RedirectToAction(nameof(Index));
        }

        // POST /StockBill/UnAudit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UnAudit(int id)
        {

            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            if (!(header.FState ?? false))
            {
                TempData["Error"] = "\u5355\u636e\u672a\u5ba1\u6838\uff0c\u65e0\u9700\u53cd\u5ba1\u6838\uff01";   // 单据未审核，无需反审核！
                return RedirectToAction(nameof(Index));
            }
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }

            var entries = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id).ToList();

            // Un-audit = flip the flag, drop the batches this bill created and re-derive the
            // caches from the ledger. StockService owns the ordering: the occupancy check has to
            // run while the bill is still audited, so FState is cleared inside the service.
            // Refuses when a batch was already consumed by a later out-stock bill.
            string revertError;
            using (var tx = _stock.BeginStockTransaction(_db))
            {
                if (!_stock.RevertInStock(_db, header, entries, out revertError))
                {
                    TempData["Error"] = revertError;
                    return RedirectToAction(nameof(Index));       // tx disposed -> rolled back
                }

                // FState 已由 RevertInStock 内部置 false 并 SaveChanges，此处无需重复赋值。
                tx.Commit();
            }

            TempData["Success"] = "\u53cd\u5ba1\u6838\u6210\u529f\uff0c\u6279\u6b21\u4e0e\u5e93\u5b58\u5df2\u56de\u6eda\u3002";   // 反审核成功，批次与库存已回滚。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ helpers

        // Process-wide lock serializes "read max + 1" so concurrent creates cannot
        // yield the same bill no / inter id (single Kestrel process per deploy).
        // BillNoLock lives on StockService (shared across all bill types that write t_PMS_StockBill).

        /// <summary>"CGRKD" + 8-digit sequence = max(FBillNo)+1 within FBillType=0 (old GetStockBillNoByFBillType).</summary>
        private string NextBillNo()
        {
            lock (StockService.BillNoLock)
            {
                // Only consider well-formed "CGRKD"+8-digit bill numbers; dirty/legacy
                // values are ignored so they cannot poison the sequence or collide.
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

        /// <summary>Load posted entry items from t_ERP_ITEM.</summary>
        private Dictionary<int, t_ERP_ITEM> LoadItems(StockBillEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<StockBillRowInput>())
                .Where(r => r.ItemId > 0)
                .Select(r => r.ItemId)
                .Distinct()
                .ToList();
            return _db.t_ERP_ITEM.Where(i => ids.Contains(i.ID)).ToDictionary(i => i.ID);
        }

        /// <summary>Business validation.</summary>
        private bool ValidateBill(StockBillEditViewModel vm, Dictionary<int, t_ERP_ITEM> items)
        {
            // SQL Server datetime lower bound is 1753; an unparseable date binds to
            // default(DateTime) (0001-01-01) and EF6 sends it as datetime2 -> SqlException 242.
            if (vm.Date == default || vm.Date.Year < 2000)
            {
                ModelState.AddModelError("Date", "\u8bf7\u586b\u5199\u6b63\u786e\u7684\u5355\u636e\u65e5\u671f\uff01");   // 请填写正确的单据日期！
                return false;
            }

            // 必填项（除「备注」外全部必填）：入库部门 / 仓库 / 入库人 / 供货单位 / 检验人
            var headOk = true;
            if (!vm.DeptId.HasValue || vm.DeptId.Value <= 0)
            {
                ModelState.AddModelError("DeptId", "\u8bf7\u9009\u62e9\u5165\u5e93\u90e8\u95e8\uff01");   // 请选择入库部门！
                headOk = false;
            }
            if (!vm.WorkShopId.HasValue || vm.WorkShopId.Value <= 0)
            {
                ModelState.AddModelError("WorkShopId", "\u8bf7\u9009\u62e9\u5165\u5e93\u4ed3\u5e93\uff01");   // 请选择入库仓库！
                headOk = false;
            }
            if (!vm.ManagerId.HasValue || vm.ManagerId.Value <= 0)
            {
                ModelState.AddModelError("ManagerId", "\u8bf7\u9009\u62e9\u5165\u5e93\u4eba\uff01");   // 请选择入库人！
                headOk = false;
            }
            if (string.IsNullOrWhiteSpace(vm.BuyingUnit))
            {
                ModelState.AddModelError("BuyingUnit", "\u8bf7\u9009\u62e9\u4f9b\u8d27\u5355\u4f4d\uff01");   // 请选择供货单位！
                headOk = false;
            }
            if (string.IsNullOrWhiteSpace(vm.Inspectors))
            {
                ModelState.AddModelError("Inspectors", "\u8bf7\u9009\u62e9\u68c0\u9a8c\u4eba\uff01");   // 请选择检验人！
                headOk = false;
            }
            if (!headOk) return false;

            var rows = (vm.Rows ?? new List<StockBillRowInput>()).Where(r => r.ItemId > 0).ToList();

            if (rows.Count == 0)
            {
                ModelState.AddModelError("Rows", "\u8bf7\u81f3\u5c11\u6dfb\u52a0\u4e00\u4e2a\u5546\u54c1\uff01");   // 请至少添加一个商品！
                return false;
            }

            if (rows.Select(r => r.ItemId).Distinct().Count() != rows.Count)
            {
                ModelState.AddModelError("Rows", "\u5165\u5e93\u660e\u7ec6\u4e2d\u5b58\u5728\u91cd\u590d\u5546\u54c1\uff01");   // 入库明细中存在重复商品！
                return false;
            }

            // 仓库数据权限（W1）：整单商品必须全部落在当前用户管理的仓库内。
            // 少了这一条，"新建/修改一张含别仓商品的采购入库单再审核"就能改到别的仓库的库存。
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
                if ((r.Qty ?? 0) <= 0)
                {
                    ModelState.AddModelError("Rows", "\u5165\u5e93\u6570\u91cf\u5fc5\u987b\u5927\u4e8e 0\uff01");   // 入库数量必须大于 0！
                    return false;
                }
                if ((r.Price ?? 0) < 0)
                {
                    ModelState.AddModelError("Rows", "\u5355\u4ef7\u4e0d\u80fd\u4e3a\u8d1f\u6570\uff01");   // 单价不能为负数！
                    return false;
                }
            }

            return true;
        }

        /// <summary>Replace-all save: delete old entries then insert new ones, inside a transaction.</summary>
        private void SaveBill(StockBillEditViewModel vm, t_PMS_StockBill header,
            Dictionary<int, t_ERP_ITEM> items)
        {
            using (var tx = _db.Database.BeginTransaction())
            {
                var old = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (old.Count > 0) _db.t_PMS_StockBillEntry.RemoveRange(old);

                if (header.FInterID == 0 || !_db.t_PMS_StockBill.Any(h => h.FInterID == header.FInterID))
                    _db.t_PMS_StockBill.Add(header);

                foreach (var r in (vm.Rows ?? new List<StockBillRowInput>()).Where(r => r.ItemId > 0))
                {
                    var qty = r.Qty ?? 0m;
                    var price = r.Price ?? 0m;
                    var amount = qty * price;
                    var tax = ResolveTax(r.TaxRateId);
                    decimal afterAmount, taxAmount, afterPrice;
                    if (tax.Value > 0)
                    {
                        afterAmount = Math.Round(amount / (1 + tax.Value), 2);
                        afterPrice = Math.Round(price / (1 + tax.Value), 5);
                    }
                    else
                    {
                        afterAmount = amount;
                        afterPrice = price;
                    }
                    taxAmount = amount - afterAmount;

                    _db.t_PMS_StockBillEntry.Add(new t_PMS_StockBillEntry
                    {
                        FInterID = header.FInterID,
                        FStepID = 0,
                        FBomId = 0,
                        FItemID = r.ItemId,
                        FNum = qty,
                        FPrice = price,
                        FAmount = amount,
                        FTaxRate = tax.Name,
                        FTaxRateValue = tax.Value,
                        FTaxAmount = taxAmount,
                        FAfterTaxPrice = afterPrice,
                        FAfterTaxAmount = afterAmount,
                        FNote = string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note.Trim(),
                        FBatchNo = "",
                        FROB = GONES.Web.Services.StockService.DirIn,   // +1, in-stock (NOT NULL column)
                    });
                }

                _db.SaveChanges();
                tx.Commit();
            }
        }

        /// <summary>Dict id (FParentID=2) -> (name like "13%", decimal rate 0.13).</summary>
        private (string Name, decimal Value) ResolveTax(int taxRateId)
        {
            if (taxRateId <= 0) return ("", 0m);
            var d = _db.t_ERP_DataDict.FirstOrDefault(x => x.ID == taxRateId && x.FParentID == 2);
            if (d == null || string.IsNullOrWhiteSpace(d.DictName)) return ("", 0m);
            var name = d.DictName.Trim();
            var numPart = name.TrimEnd('%').Trim();
            decimal pct;
            if (!decimal.TryParse(numPart, out pct)) pct = 0m;
            return (name, pct / 100m);
        }

        /// <summary>Reverse lookup: tax rate name -> dict id (edit redisplay).</summary>
        private int ResolveTaxRateId(string taxRateName)
        {
            if (string.IsNullOrWhiteSpace(taxRateName)) return 0;
            var name = taxRateName.Trim();
            var d = _db.t_ERP_DataDict.FirstOrDefault(x => x.FParentID == 2 && x.DictName == name);
            return d?.ID ?? 0;
        }

        /// <summary>Item snapshots for rows already in the form (edit / validation redisplay).</summary>
        private void BindRowInfo(StockBillEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<StockBillRowInput>())
                .Where(r => r.ItemId > 0)
                .Select(r => r.ItemId)
                .Distinct()
                .ToList();
            ViewBag.RowItems = _db.t_ERP_ITEM
                .Where(i => ids.Contains(i.ID))
                .ToDictionary(i => i.ID, i => i);

            // tax rate value per dict id, for razor-side tax split display
            ViewBag.TaxRateValues = _db.t_ERP_DataDict
                .Where(d => d.FParentID == 2)
                .ToList()
                .ToDictionary(d => d.ID, d =>
                {
                    var s = (d.DictName ?? "").Trim().TrimEnd('%').Trim();
                    decimal v;
                    return decimal.TryParse(s, out v) ? v / 100m : 0m;
                });
        }

        /// <summary>
        /// Form data: dept (level-1 departments), warehouse (IsDEP=1 children of selected dept),
        /// manager (t_PMS_Worker whose DEPID=selected warehouse), suppliers (dict 52), tax rates (dict 2).
        /// </summary>
        private void BindFormExtras(int? deptId = null, int? workShopId = null)
        {
            // 入库部门：一级部门（ParentID=1），如生产部、物管部等
            ViewBag.DeptOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.ParentID == 1)
                .OrderBy(d => d.ID)
                .ToList();

            // 入库仓库：所选部门下的仓库（IsDEP=1）
            ViewBag.WorkShopOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 1 && d.ParentID == deptId)
                .OrderBy(d => d.ID)
                .ToList();

            // 入库人：所选仓库下的人员（t_PMS_Worker.DEPID = warehouse id）
            ViewBag.Managers = _db.t_PMS_Worker
                .Where(w => w.Status == true && w.DEPID == workShopId)
                .OrderBy(w => w.ID)
                .ToList();

            ViewBag.Suppliers = _db.t_ERP_DataDict
                .Where(d => d.FParentID == 4)
                .OrderBy(d => d.ID)
                .ToList();
            ViewBag.TaxRates = _db.t_ERP_DataDict
                .Where(d => d.FParentID == 2)
                .OrderBy(d => d.ID)
                .ToList();

            // item pick modal category dropdown: distinct classes of active items
            ViewBag.ItemCategories = _db.t_ERP_ITEM
                .Where(i => i.IsEnabled == true && i.ProductCategory != null && i.ProductCategory != "")
                .Select(i => i.ProductCategory)
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            var creater = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == CurrentUserId);
            ViewBag.CreaterName = creater?.UserName ?? "";
            ViewBag.ModifyName = "";
            ViewBag.ModifyTime = null;

            // 检验人下拉：码表 t_erp_data_dict 中 f_parent_id=8 的字典项（检验人名单）。
            ViewBag.Inspectors = _db.t_ERP_DataDict
                .Where(d => d.FParentID == 8)
                .OrderBy(d => d.ID)
                .ToList();
        }

        // ------------------------------------------------------------ export

        // GET /StockBill/Export?dateFrom=&dateTo=&billNo=&pym=&state=
        // 复用 BuildQuery + BuildRows：与列表页同筛选、同口径（含仓库数据权限闸门①-a/③-b），
        // 不翻页、导出全部命中行。零三方依赖（ExcelExport.HtmlTable 内联 table，Excel 可直接打开）。
        // 单据级字段（审核/日期/编号/供货单位）在每一行都重复填写，便于 Excel 排序筛选。
        [HttpGet]
        public IActionResult Export(DateTime? dateFrom, DateTime? dateTo, string billNo, string pym, int? state)
        {

            billNo = billNo?.Trim();
            pym = pym?.Trim();

            var headers = BuildQuery(dateFrom, dateTo, billNo, pym, state)
                .OrderByDescending(h => h.FInterID)
                .ToList();
            var headerIds = headers.Select(h => h.FInterID).ToList();
            var lockedByDepot = _depot.BillsNotFullyInScope(headerIds);
            var rows = BuildRows(headers, lockedByDepot);

            var cols = new List<string>
            {
                "审核", "单据日期", "单据编号", "批号", "产品代码", "产品分类",
                "产品名称", "规格", "单位", "入库数量", "含税单价", "含税金额", "税率", "供货单位",
            };
            var data = rows.Select(r => new List<string>
            {
                r.State ? "已审" : "未审",
                r.Date?.ToString("yyyy-MM-dd") ?? "",
                r.BillNo ?? "",
                r.BatchNo ?? "",
                r.Cpbm ?? "",
                r.Cplb ?? "",
                r.Cpjc ?? "",
                r.Cpgg ?? "",
                r.UnitName ?? "",
                (r.Qty ?? 0m).ToString("0.00"),
                (r.Price ?? 0m).ToString("0.00##"),
                (r.Amount ?? 0m).ToString("0.00"),
                r.TaxRate ?? "",
                r.BuyingUnit ?? "",
            }).ToList();

            var fileName = "采购入库单_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".xls";
            return ExcelExport.HtmlTable(fileName, cols, data);
        }

        // ------------------------------------------------------------ query / row builders (shared by Index & Export)

        /// <summary>列表/导出共用的过滤：FBillType 限定 + 仓库闸门①-a + 日期/编号/拼音码/审核状态。</summary>
        private IQueryable<t_PMS_StockBill> BuildQuery(DateTime? dateFrom, DateTime? dateTo, string billNo, string pym, int? state)
        {
            var q = _db.t_PMS_StockBill.Where(h => h.FBillType == BillType);
            // 数据闸门①-a（行级读）：只保留"至少有一行明细属于本仓库"的单据。
            if (_depot.IsRestricted) q = _depot.FilterBills(q);
            // hoist .Date out of the lambdas（EF 不能翻译 Nullable<DateTime>.Date）
            var dFrom = dateFrom.HasValue ? dateFrom.Value.Date : (DateTime?)null;
            var dTo = dateTo.HasValue ? dateTo.Value.Date.AddDays(1) : (DateTime?)null;
            if (dFrom.HasValue) q = q.Where(h => h.FDate >= dFrom.Value);
            if (dTo.HasValue) q = q.Where(h => h.FDate < dTo.Value);
            if (!string.IsNullOrEmpty(billNo)) q = q.Where(h => h.FBillNo.Contains(billNo));
            if (!string.IsNullOrEmpty(pym)) q = q.Where(h => h.FBuyingUnit.Contains(pym));
            if (state.HasValue && state.Value >= 0) q = q.Where(h => (h.FState ?? false) == (state.Value == 1));
            return q;
        }

        /// <summary>把一组表头 + 仓库灰化集合展开成列表行（与 Index 原内联逻辑一致）。</summary>
        private List<StockBillListRow> BuildRows(List<t_PMS_StockBill> headers, HashSet<int> lockedByDepot)
        {
            var headerIds = headers.Select(h => h.FInterID).ToList();
            var entries = _db.t_PMS_StockBillEntry
                .Where(e => headerIds.Contains(e.FInterID))
                .OrderBy(e => e.FInterID)
                .ThenBy(e => e.FEntryID)
                .ToList();
            var entriesByBill = entries.GroupBy(e => e.FInterID)
                .ToDictionary(g => g.Key, g => g.ToList());

            var itemIds = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM
                .Where(i => itemIds.Contains(i.ID))
                .ToDictionary(i => i.ID);
            var depts = _db.t_ERP_Department.ToDictionary(d => d.ID, d => d.DEPName);
            var managerIds = headers.Where(h => h.FManagerID != null).Select(h => h.FManagerID.Value).Distinct().ToList();
            var workers = _db.t_PMS_Worker
                .Where(w => managerIds.Contains(w.ID))
                .ToDictionary(w => w.ID, w => w.FName);

            string DeptName(int? id) => (id != null && depts.ContainsKey(id.Value)) ? depts[id.Value] : "";

            var rows = new List<StockBillListRow>();
            foreach (var h in headers)
            {
                var hid = h.FInterID;
                var billEntries = entriesByBill.ContainsKey(hid) ? entriesByBill[hid] : new List<t_PMS_StockBillEntry>();
                var first = true;
                if (billEntries.Count == 0)
                {
                    rows.Add(new StockBillListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate,
                        State = h.FState ?? false, BuyingUnit = h.FBuyingUnit,
                        DeptName = DeptName(h.FDeptID),
                        ManagerName = (h.FManagerID != null && workers.ContainsKey(h.FManagerID.Value)) ? workers[h.FManagerID.Value] : "",
                        FirstOfBill = true,
                        CanModify = !lockedByDepot.Contains(hid),
                    });
                    continue;
                }
                foreach (var e in billEntries)
                {
                    t_ERP_ITEM it = null;
                    if (e.FItemID != null && items.ContainsKey(e.FItemID.Value)) it = items[e.FItemID.Value];
                    rows.Add(new StockBillListRow
                    {
                        InterId = h.FInterID,
                        BillNo = h.FBillNo,
                        Date = h.FDate,
                        State = h.FState ?? false,
                        FirstOfBill = first,
                        DeptName = DeptName(h.FDeptID),
                        ManagerName = (h.FManagerID != null && workers.ContainsKey(h.FManagerID.Value)) ? workers[h.FManagerID.Value] : "",
                        BuyingUnit = h.FBuyingUnit,
                        EntryId = e.FEntryID,
                        BatchNo = e.FBatchNo,
                        Cpbm = it?.ItemCode ?? "",
                        Cplb = it?.ProductCategory ?? "",
                        Cpjc = it?.ItemShortName ?? "",
                        Cpgg = it?.ItemSpec ?? "",
                        UnitName = it?.BaseUnit ?? "",
                        Qty = e.FNum,
                        Price = e.FPrice,
                        Amount = e.FAmount,
                        TaxRate = e.FTaxRate,
                        Note = e.FNote,
                        CanModify = !lockedByDepot.Contains(hid),
                    });
                    first = false;
                }
            }
            return rows;
        }
    }
}
