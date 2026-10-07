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
    /// Warehouse picking bill (CKLLD) - mirrors the legacy WinForms
    /// Stock.FrmPickingOutStockManager (\u4ed3\u5e93\u9886\u6599\u8f93\u5165, t_ERP_Menu.ID=1293) which is the
    /// \u51fa\u5e93\u5355 (stock-out) form. Header t_PMS_StockBill (FBillType=8) + entries t_PMS_StockBillEntry.
    /// Out-stock: FROB = -1, consumes existing batches via FIFO, never creates batch master rows.
    /// 13-column table (BOMID/\u751f\u4ea7\u5355\u53f7/\u5de5\u5e8f\u540d\u79f0/\u4ea7\u54c1\u540d\u79f0/\u89c4\u683c\u578b\u53f7/\u4ea7\u54c1\u5206\u7c7b/\u5355\u4f4d/
    /// \u6279\u6b21\u5e93\u5b58/\u9886\u7528\u6570\u91cf/\u51fa\u5e93\u6570\u91cf/\u6279\u53f7/\u4ed3\u5e93\u540d\u79f0/\u5907\u6ce8) with NO
    /// price/tax columns. Out-stock is not a financial document, so FPrice/FTax* stay NULL.
    ///
    /// Importing a production-order picking application (t_PMS_BillUse) pulls its FIsProduct=0 rows,
    /// FIFO-allocates batches, and pre-fills the table. On audit we write back
    /// t_PMS_BillUseEntry.FCurrentUseNum += issued qty (per linked row) so the same application
    /// cannot be imported twice. Un-audit reverses that write-back.
    ///
    /// Trust boundary: BomId/StepId/ProduceNo/ProduceId/UseNum are server-rederived from
    /// t_PMS_BillUseEntry on save (defense vs. tampered POST). Only OutNum/BatchNo/Note are
    /// user-editable and trusted from the form.
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // standard MVC form redisplay, skip Furion 400 short-circuit
    public class WarehousePickController : Controller, IMenuGuarded
    {
        private const int BillType = 8;
        private const int ReturnBillType = 3;   // 仓库退料单 (FB=3): downstream consumer of a pick bill
        private const string BillNoPrefix = "SCLLD";
        private const string BillTypeExName = "\u4ed3\u5e93\u9886\u6599";   // 仓库领料（原「仓库领料单」，去「单」统一口径）

        /// <summary>W1 越权提示：读可以少看，动必须整套归你（编辑/审核/反审核/删除共用一份文案）。</summary>
        private const string OutOfScopeMsg = "\u8be5\u5355\u636e\u5305\u542b\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u4ed3\u5e93\u7684\u660e\u7ec6\uff0c\u7981\u6b62\u64cd\u4f5c\uff01";   // 该单据包含不属于您管理仓库的明细，禁止操作！

        private readonly GonesPgDbContext _db;
        private readonly StockService _stock;
        private readonly MenuService _menu;
        private readonly DepotScope _depot;
        public WarehousePickController(GonesPgDbContext db, StockService stock, MenuService menu, DepotScope depot)
        {
            _db = db;
            _stock = stock;
            _menu = menu;
            _depot = depot;
        }

        private bool IsAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        /// <summary>
        /// 入口闸门：admin 旁路恒 true（所以行为逐字节不变），非 admin 须由其角色的
        /// t_ERP_RoleMenu 授权覆盖本模块（1293）。原来的 <c>!CanAccess</c> 是"数据权限未实现"
        /// 的占位——它把已获授权的 26/27 仓库管理员也一并挡在门外。
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

            var matchedIds = hq.Select(h => h.FInterID);
            // 数据闸门①-b（合计口径）：合计必须与"可见行"同口径。原实现把命中的**全部**明细行
            // 求和，行级过滤后会**把别仓的数量/金额算进来**——这不是显示不一致，是越权泄露。
            var sumQ = _db.t_PMS_StockBillEntry.Where(e => matchedIds.Contains(e.FInterID));
            if (_depot.IsRestricted) sumQ = _depot.FilterEntries(sumQ);
            var sums = sumQ
                .GroupBy(e => 1)
                .Select(g => new { Qty = g.Sum(x => x.FNum ?? 0m) })
                .FirstOrDefault();
            ViewBag.TotalQty = sums?.Qty ?? 0m;

            // 数据闸门①-c：明细分行同样按仓库收窄（页脚的"本页合计"由 Model 现算，会自动跟随）。
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

            // Pre-compute the un-audit lock once per page (same rule as the UnAudit server guard):
            // a bill whose materials were already stocked in as finished goods cannot be reversed.
            var auditedPickIds = headers.Where(h => h.FState ?? false).Select(h => h.FInterID).ToList();
            var lockedPickIds = LoadLockedPickIds(auditedPickIds);

            // 数据闸门③-b（按钮灰化）：与服务端 W1 硬拦同源——明细未全部落在本仓库范围内的单据
            // 不许修改/审核/删除。只灰按钮而服务端不拦（或反之）都会造成口径分裂。
            var lockedByDepot = _depot.BillsNotFullyInScope(headerIds);

            var rows = new List<WarehousePickListRow>();
            foreach (var h in headers)
            {
                var bills = entriesByBill.ContainsKey(h.FInterID) ? entriesByBill[h.FInterID] : new List<t_PMS_StockBillEntry>();
                var canUnAudit = !lockedPickIds.Contains(h.FInterID);
                var canModify = !lockedByDepot.Contains(h.FInterID);
                var first = true;
                if (bills.Count == 0)
                {
                    rows.Add(new WarehousePickListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = true, DeptName = DeptName(h.FDeptID), GroupName = GroupName(h.FGroupID), ManagerName = WorkerName(h.FManagerID),
                        CanUnAudit = canUnAudit, CanModify = canModify,
                    });
                    continue;
                }
                foreach (var e in bills)
                {
                    t_ERP_ITEM it = null;
                    if (e.FItemID != null && items.ContainsKey(e.FItemID.Value)) it = items[e.FItemID.Value];
                    rows.Add(new WarehousePickListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = first, DeptName = DeptName(h.FDeptID), GroupName = GroupName(h.FGroupID), ManagerName = WorkerName(h.FManagerID),
                        CanUnAudit = canUnAudit, CanModify = canModify,
                        FromBillUseNo = e.FBillUseNo,
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

        // ------------------------------------------------------------ import source list

        public IActionResult ImportList()
        {

            // 数据闸门②-a（选择器）：申请单列表的"可导入原料行数"须按本仓库口径统计，
            // 且没有任何本仓可领行的申请单不展示——否则用户点进去必然导出 0 行，白跑一趟。
            List<BillUseImportRow> list;
            if (!_depot.IsRestricted)
            {
                list = _db.t_PMS_BillUse
                    .OrderByDescending(u => u.FInterID)
                    .Select(u => new BillUseImportRow
                    {
                        FInterID = u.FInterID,
                        FBillNo = u.FBillNo,
                        FDate = u.FDate,
                        FDeptID = u.FDeptID,
                        FWorkShopID = u.FWorkShopID,
                        RawCount = _db.t_PMS_BillUseEntry.Count(e => e.FInterID == u.FInterID && e.FIsProduct == false
                                      && (e.FUseNum ?? 0m) > (e.FCurrentUseNum ?? 0m)),
                    })
                    .ToList();
            }
            else
            {
                var mine = _depot.DepotIds.ToList();
                // 两段式（先取本仓可领行的所属单据，再统计）比在投影里嵌 Any 子查询更稳，
                // 且与 ImportFromBillUse 的实际取行条件逐字一致——两边口径必须能对上。
                var eligible = _db.t_PMS_BillUseEntry
                    .Where(e => e.FIsProduct == false && e.FItemID != null
                                && (e.FUseNum ?? 0m) > (e.FCurrentUseNum ?? 0m))
                    .Where(e => _db.t_ERP_ITEM.Any(i => i.ID == e.FItemID.Value
                                && i.WarehouseId != null && mine.Contains(i.WarehouseId.Value)))
                    .Select(e => e.FInterID)
                    .ToList();
                var countByBill = eligible.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
                // 只把「本仓确有可领行」的申请单取出：过滤下推到 SQL，
                // 原实现是 `_db.t_PMS_BillUse...ToList()` 把整张 BillUse 拉进内存再 Where(RawCount>0)——
                // 单量增长后是纯浪费（全表 + 全量物化），且与上面的两段式收窄口径重复。
                var eligibleIds = countByBill.Keys.ToList();
                list = _db.t_PMS_BillUse
                    .Where(u => eligibleIds.Contains(u.FInterID))
                    .OrderByDescending(u => u.FInterID)
                    .Select(u => new BillUseImportRow
                    {
                        FInterID = u.FInterID,
                        FBillNo = u.FBillNo,
                        FDate = u.FDate,
                        FDeptID = u.FDeptID,
                        FWorkShopID = u.FWorkShopID,
                    })
                    .ToList();
                // RawCount 来自内存字典（GroupBy 只会为真实存在的行建组 ⇒ 计数恒 > 0，无需再过滤）
                foreach (var r in list)
                    r.RawCount = countByBill.TryGetValue(r.FInterID, out var c) ? c : 0;
            }
            var deptIds = list.Select(x => x.FDeptID).Where(i => i.HasValue).Select(i => i.Value).Distinct().ToList();
            var depts = _db.t_ERP_Department.Where(d => deptIds.Contains(d.ID)).ToDictionary(d => d.ID, d => d.DEPName);
            foreach (var r in list)
                r.DeptName = (r.FDeptID.HasValue && depts.ContainsKey(r.FDeptID.Value)) ? depts[r.FDeptID.Value] : "";
            return View(list);
        }

        // ------------------------------------------------------------ import (build prefilled form)

        [HttpGet]
        public IActionResult ImportFromBillUse(int id)
        {

            var use = _db.t_PMS_BillUse.FirstOrDefault(u => u.FInterID == id);
            if (use == null) return NotFound();

            // 数据闸门②-b（选择器）：导入源只能取本仓库的商品行——否则用户能把别仓的货
            // 领进自己的领料单（列表看不见、导入却能选到，就是典型的假权限）。
            var useQ = _db.t_PMS_BillUseEntry
                .Where(e => e.FInterID == id && e.FIsProduct == false && e.FItemID != null);
            if (_depot.IsRestricted)
            {
                var mine = _depot.DepotIds.ToList();
                useQ = useQ.Where(e => _db.t_ERP_ITEM.Any(i => i.ID == e.FItemID.Value
                                        && i.WarehouseId != null && mine.Contains(i.WarehouseId.Value)));
            }
            var useItems = useQ.ToList();

            // batch the joins to avoid N+1
            var itemIds = useItems.Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);
            var stepIds = useItems.Select(e => e.FStepID).Where(s => s > 0).Distinct().ToList();
            var steps = _db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID))
                .ToDictionary(s => s.FItemID, s => s.FName ?? "");

            var warehouses = LoadWarehouseNames(items.Values.Select(i => i.WarehouseId));

            var rows = new List<WarehousePickRowInput>();
            var reserved = new Dictionary<string, decimal>();
            var shortRows = new List<string>();   // per-row shortfall reasons, in apply-bill order
            var taken = 0;                        // how many source lines actually produced rows

            foreach (var e in useItems)
            {
                decimal remaining = (e.FUseNum ?? 0m) - (e.FCurrentUseNum ?? 0m);
                if (remaining <= 0m) continue;                  // already fully issued
                var item = items.ContainsKey(e.FItemID.Value) ? items[e.FItemID.Value] : null;
                string stepName = steps.ContainsKey(e.FStepID) ? steps[e.FStepID] : "";

                List<(string batchNo, int? batchId, decimal num)> splits;
                try
                {
                    splits = _stock.AllocateFifo(_db, e.FItemID.Value, remaining, reserved);
                }
                catch (InvalidOperationException ex)
                {
                    // A shortage belongs to ONE row, not to the whole apply bill: keep the rows that
                    // can be served and report the rest, so one out-of-stock item no longer blocks an
                    // otherwise serviceable import. AllocateFifo leaves no reservations behind when it
                    // throws, so the next row still sees true availability.
                    shortRows.Add(ex.Message);
                    continue;
                }
                taken++;

                foreach (var s in splits)
                {
                    rows.Add(new WarehousePickRowInput
                    {
                        ItemId = e.FItemID.Value,
                        BomId = e.FBomId,
                        StepId = e.FStepID,
                        ProduceId = e.FProduceId,
                        ProduceNo = e.FProduceNo,
                        StepName = stepName,
                        ProductName = item?.ItemShortName ?? "",
                        Spec = item?.ItemSpec ?? "",
                        CategoryName = item?.ProductCategory ?? "",
                        Unit = item?.BaseUnit ?? "",
                        BatchStock = null,   // 实时账本口径，循环结束后按批号统一填充
                        UseNum = e.FUseNum,
                        OutNum = s.num,                          // default = FIFO-allocated; user can shrink
                        BatchNo = s.batchNo,
                        WarehouseName = WarehouseNameOf(warehouses, item?.WarehouseId),
                        Note = "",
                        BillUseEntryId = e.FEntryID,
                        BillUseNo = use.FBillNo,
                    });
                }
            }

            // 批次库存：实时账本口径（同一格不再有两套来源）。见 LoadBatchStock 注释。
            var batchStock = LoadBatchStock(rows.Select(r => r.BatchNo));
            foreach (var r in rows) r.BatchStock = BatchStockOf(batchStock, r.BatchNo);

            if (rows.Count == 0)
            {
                var msg = "\u8be5\u9886\u6599\u7533\u8bf7\u5355\u65e0\u53ef\u5bfc\u5165\u7684\u539f\u6599\u884c\uff08\u5df2\u5168\u90e8\u53d1\u6599\u6216\u5e93\u5b58\u4e0d\u8db3\uff09\u3002";   // 该领料申请单无可导入的原料行...
                if (shortRows.Count > 0) msg += string.Join("\uff1b", shortRows);
                TempData["Error"] = msg;
                return RedirectToAction(nameof(ImportList));
            }

            if (shortRows.Count > 0)
            {
                // Partial import: carry on into the form, but say plainly which lines were left out.
                ViewBag.ImportNotice = "\u5bfc\u5165\u5b8c\u6210\uff1a\u5df2\u5e26\u5165 " + taken + " \u884c\u539f\u6599\uff1b"
                    + shortRows.Count + " \u884c\u672a\u5e26\u5165 \u2014\u2014 " + string.Join("\uff1b", shortRows);
                // 导入完成：已带入 N 行原料；M 行未带入 —— 库存不足：...
            }

            BindFormExtras(use.FDeptID, use.FWorkShopID, use.FGroupID);
            var vm = new WarehousePickEditViewModel
            {
                BillNo = NextBillNo(),
                Date = DateTime.Today,
                DeptId = use.FDeptID,
                WorkShopId = use.FWorkShopID,
                GroupId = use.FGroupID,
                ManagerId = use.FWorkerID,
                Remark = "\u5bfc\u5165\u81ea\u9886\u6599\u7533\u8bf7\u5355 " + use.FBillNo,   // 导入自领料申请单 ...
                FromBillUseId = use.FInterID,
                FromBillUseNo = use.FBillNo,
                Rows = rows,
            };
            return View("Form", vm);
        }

        // ------------------------------------------------------------ create

        [HttpGet]
        public IActionResult Create()
        {
            BindFormExtras();
            var vm = new WarehousePickEditViewModel
            {
                BillNo = NextBillNo(),
                Date = DateTime.Today,
                Rows = new List<WarehousePickRowInput>(),
            };
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(WarehousePickEditViewModel vm)
        {

            var items = LoadItems(vm);
            DeriveServerFields(vm);                              // trust boundary: re-derive from BillUseEntry
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
                    FROB = -1,                          // red bill (out-stock)
                    FState = false,
                };
                SaveBill(vm, header, items);
            }

            TempData["Success"] = "\u4ed3\u5e93\u9886\u6599\u5355\u4fdd\u5b58\u6210\u529f\u3002";   // 仓库领料单保存成功。
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

            var entries = _db.t_PMS_StockBillEntry
                .Where(e => e.FInterID == id).OrderBy(e => e.FEntryID).ToList();

            // batch the joins needed to repopulate display fields
            var itemIds = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);
            var stepIds = entries.Select(e => e.FStepID).Where(s => s > 0).Distinct().ToList();
            var steps = _db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID))
                .ToDictionary(s => s.FItemID, s => s.FName ?? "");
            var useEntryIds = entries.Where(e => e.FBillUseEntryID.HasValue)
                .Select(e => e.FBillUseEntryID.Value).Distinct().ToList();
            var useEntries = _db.t_PMS_BillUseEntry.Where(u => useEntryIds.Contains(u.FEntryID))
                .ToDictionary(u => u.FEntryID);

            var warehouses = LoadWarehouseNames(items.Values.Select(i => i.WarehouseId));

            // 批次库存：实时账本口径（与批号下拉 / 前端失焦刷新同源）。见 LoadBatchStock 注释。
            var batchStock = LoadBatchStock(entries.Select(e => e.FBatchNo));

            var rows = new List<WarehousePickRowInput>();
            foreach (var e in entries)
            {
                if (e.FItemID == null || e.FItemID.Value == 0) continue;
                t_ERP_ITEM it = items.ContainsKey(e.FItemID.Value) ? items[e.FItemID.Value] : null;
                string stepName = steps.ContainsKey(e.FStepID) ? steps[e.FStepID] : "";
                decimal? useNum = (e.FBillUseEntryID.HasValue && useEntries.ContainsKey(e.FBillUseEntryID.Value))
                    ? useEntries[e.FBillUseEntryID.Value].FUseNum : (decimal?)null;

                rows.Add(new WarehousePickRowInput
                {
                    ItemId = e.FItemID.Value,
                    BomId = e.FBomId,
                    StepId = e.FStepID,
                    ProduceId = e.FProduceID,
                    ProduceNo = e.FProduceNo,
                    StepName = stepName,
                    ProductName = it?.ItemShortName ?? "",
                    Spec = it?.ItemSpec ?? "",
                    CategoryName = it?.ProductCategory ?? "",
                    Unit = it?.BaseUnit ?? "",
                    BatchStock = BatchStockOf(batchStock, e.FBatchNo),
                    UseNum = useNum,
                    OutNum = e.FNum,
                    BatchNo = e.FBatchNo,
                    WarehouseName = WarehouseNameOf(warehouses, it?.WarehouseId),
                    Note = e.FNote,
                    BillUseEntryId = e.FBillUseEntryID,
                    BillUseNo = e.FBillUseNo,
                });
            }

            var vm = new WarehousePickEditViewModel
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
                FromBillUseId = null,
                FromBillUseNo = rows.FirstOrDefault()?.BillUseNo,
                Rows = rows,
            };
            BindFormExtras(header.FDeptID, header.FWorkShopID, header.FGroupID);
            return View("Form", vm);
        }

        // GET /WarehousePick/Details/5  (read-only detail page for audited bills)
        public IActionResult Details(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门①-d（行级读）：只读详情只显示本仓库的明细行——"读可以少看"。
            // 这里刻意**不**施加 W1：查看是读，混合单据的可见行仍应可查（只有"动"才要求整套归你）。
            // 但一行都看不见时整页拒绝——否则表头（单号/部门/备注）会经直接构造 URL 泄露。
            var entryQ = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id);
            if (_depot.IsRestricted) entryQ = _depot.FilterEntries(entryQ);
            var entries = entryQ.OrderBy(e => e.FEntryID).ToList();
            if (_depot.IsRestricted && entries.Count == 0)
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            var itemIds = entries.Where(e => e.FItemID.HasValue).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);
            var warehouses = LoadWarehouseNames(items.Values.Select(i => i.WarehouseId));
            var rows = entries.Select(e =>
            {
                t_ERP_ITEM it = items.ContainsKey(e.FItemID ?? 0) ? items[e.FItemID ?? 0] : null;
                return new WarehousePickRowInput
                {
                    ItemId = e.FItemID ?? 0,
                    BomId = e.FBomId,
                    StepId = e.FStepID,
                    ProduceId = e.FProduceID,
                    ProduceNo = e.FProduceNo ?? "",
                    StepName = "",
                    ProductName = it?.ItemShortName ?? "",
                    Spec = it?.ItemSpec ?? "",
                    CategoryName = it?.ProductCategory ?? "",
                    Unit = it?.BaseUnit ?? "",
                    BatchStock = null,
                    UseNum = null,
                    OutNum = e.FNum,
                    BatchNo = e.FBatchNo ?? "",
                    WarehouseName = WarehouseNameOf(warehouses, it?.WarehouseId),
                    Note = e.FNote ?? "",
                    BillUseEntryId = e.FBillUseEntryID,
                    BillUseNo = e.FBillUseNo ?? "",
                };
            }).ToList();
            // 批次库存：实时账本口径（同 Edit；只读页同样展示当前库存，见 LoadBatchStock 注释）。
            var batchStock = LoadBatchStock(rows.Select(r => r.BatchNo));
            foreach (var r in rows) r.BatchStock = BatchStockOf(batchStock, r.BatchNo);
            var vm = new WarehousePickEditViewModel
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
                FromBillUseId = null,
                FromBillUseNo = rows.FirstOrDefault()?.BillUseNo,
                Rows = rows,
            };
            BindFormExtras(header.FDeptID, header.FWorkShopID, header.FGroupID);
            ViewBag.ReadOnly = true;
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, WarehousePickEditViewModel vm)
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
            DeriveServerFields(vm);
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
            SaveBill(vm, header, items);

            TempData["Success"] = "\u4ed3\u5e93\u9886\u6599\u5355\u4fee\u6539\u6210\u529f\u3002";   // 仓库领料单修改成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ delete

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门③-b（写=整单级 W1）：删除是整单级回滚，须整套归你。
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

            TempData["Success"] = "\u4ed3\u5e93\u9886\u6599\u5355\u5220\u9664\u6210\u529f\u3002";   // 仓库领料单删除成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ audit / unaudit

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Recalc()
        {
            // 唯一保留 admin 专属的动作：RecalcAll 是**全局**（跨全部仓库）的库存缓存重算，
            // 属于运维基础设施操作，而非"本仓数据"——按仓库授权对它没有定义，故不换成 CanAccess。
            // 该动作没有 UI 入口（列表页无按钮），仅供运维直接调用。
            // 服务端强制 admin（此前只写在注释里）：全库重算可被高频滥用造成全表 UPDATE 压力。
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
            // 数据闸门③-b（写=整单级 W1）：审核会按批扣减库存，混合单据不许单人审核。
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

            // Shared stock lock: concurrent audits of different bill types must not read the
            // same pre-audit balance (see StockService.BeginStockTransaction).
            using (var tx = _stock.BeginStockTransaction(_db))
            {
                if (!_stock.ApplyOutStock(_db, header, entries, out string error))
                {
                    TempData["Error"] = error;
                    return RedirectToAction(nameof(Index));       // tx disposed -> rolled back
                }
                // ApplyOutStock 内部已 SaveChanges + header.FState=true；勿再设避免空操作。

                // write back: each linked picking-application row got its issued qty
                WriteBackBillUse(entries, add: true);
                tx.Commit();
            }

            TempData["Success"] = "\u5ba1\u6838\u6210\u529f\uff0c\u5e93\u5b58\u5df2\u6309\u6279\u6b21\u51cf\u51cf\u3002";   // 审核成功，库存已按批次递减。
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UnAudit(int id)
        {
            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == id && h.FBillType == BillType);
            if (header == null) return NotFound();
            // 数据闸门③-b（写=整单级 W1）：反审核是**表头级整单回滚**（恢复库存 + 回退已发量），
            // 一个只有部分行权限的人触发整单回滚在语义上不自洽——所以要求整套归你。
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

            // Downstream guard: once the produce order this pick bill belongs to (or the pick bill
            // itself) has been stocked in as finished goods, the materials were physically consumed
            // and the pick record must be frozen. Un-auditing would re-inflate stock that is already
            // gone and desync the produce order's stock-in history.
            if (LoadLockedPickIds(new List<int> { id }).Contains(id))
            {
                TempData["Error"] = "\u5b58\u5728\u5df2\u5ba1\u6838\u7684\u751f\u4ea7\u5165\u5e93\u5355\u6216\u4ed3\u5e93\u9000\u6599\u5355\u5f15\u7528\u672c\u5355\uff08\u6216\u5176\u5bf9\u5e94\u751f\u4ea7\u5355\uff09\uff0c\u7981\u6b62\u53cd\u5ba1\u6838\uff01";
                return RedirectToAction(nameof(Index));
            }

            // Shared stock lock: concurrent audits of different bill types must not read the
            // same pre-audit balance (see StockService.BeginStockTransaction).
            using (var tx = _stock.BeginStockTransaction(_db))
            {
                _stock.RevertOutStock(_db, header, entries);   // 内部已置 FState=false 并 SaveChanges
                // reverse the earlier write-back
                WriteBackBillUse(entries, add: false);

                tx.Commit();
            }
            // 异常路径：using Dispose 自动 Rollback；catch 块手动 Rollback 会导致二次 Rollback 抛 InvalidOperationException。

            TempData["Success"] = "\u53cd\u5ba1\u6838\u6210\u529f\uff0c\u5e93\u5b58\u5df2\u6062\u590d\u3002";   // 反审核成功，库存已恢复。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ item / batch lookups

        [HttpGet]
        public IActionResult SearchItems(string keyword)
        {
            var kw = (keyword ?? string.Empty).Trim();
            if (kw.Length == 0) return Json(new List<object>());
            int idMatch = int.TryParse(kw, out var n) ? n : -1;
            var q = _db.t_ERP_ITEM
                .Where(i => i.IsEnabled == true)
                .Where(i => (i.ItemCode != null && i.ItemCode.Contains(kw))
                         || (i.ItemShortName != null && i.ItemShortName.Contains(kw))
                         || (i.ItemSpec != null && i.ItemSpec.Contains(kw))
                         || (i.ItemPinyinCode != null && i.ItemPinyinCode.Contains(kw))
                         || i.ID == idMatch);
            // 数据闸门②-c（选择器）：商品弹窗只能搜到本仓库的商品。这是"假权限"最容易漏的一环
            // ——列表看不见了，但弹窗照旧能选到别仓商品并提交。
            q = _depot.FilterItems(q);
            var items = q
                .OrderBy(i => i.ItemCode).Take(20).ToList();
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

        // ------------------------------------------------------------ BOM picker (modal)

        // GET /WarehousePick/GetBomSteps
        // Distinct process steps found across BOM bills, with a BOM count each — the left
        // panel of the "select BOM" modal.
        [HttpGet]
        public IActionResult GetBomSteps()
        {

            var bq = _db.t_PMS_StepProductBom.Where(b => b.FStepId != null && b.FStepId > 0);
            // 数据闸门②-d（选择器）：工序面板的 bomCount 只数"含本仓库物料"的 BOM，
            // 否则计数会暴露别仓有多少张 BOM，且点进去是空列表。
            if (_depot.IsRestricted)
            {
                var mine = _depot.DepotIds.ToList();
                bq = bq.Where(b => b.FPItemID != null && _db.t_ERP_ITEM.Any(i => i.ID == b.FPItemID.Value
                                    && i.WarehouseId != null && mine.Contains(i.WarehouseId.Value)));
            }
            var rows = bq
                .Select(b => new { b.FStepId, b.FStepName, b.FBomID })
                .ToList();

            var steps = rows
                .GroupBy(r => new { r.FStepId, r.FStepName })
                .OrderBy(g => g.Key.FStepId)
                .Select(g => new
                {
                    stepId = g.Key.FStepId ?? 0,
                    stepName = g.Key.FStepName ?? "",
                    bomCount = g.Select(x => x.FBomID).Distinct().Count()
                })
                .ToList();
            return Json(steps);
        }

        // GET /WarehousePick/SearchBoms?keyword=xxx&stepId=7
        // Returns one row per BOM bill (distinct FBomID), matching on BOM id / step name /
        // output product code-name-spec. Optional stepId narrows the list to one process step
        // (the picker modal shows steps on the left, BOMs of the selected step on the right).
        [HttpGet]
        public IActionResult SearchBoms(string keyword, int? stepId = null)
        {

            var kw = (keyword ?? string.Empty).Trim();
            int idMatch = int.TryParse(kw, out var n) ? n : -1;

            var all = _db.t_PMS_StepProductBom.AsQueryable();
            // 数据闸门②-e（选择器）：BOM 弹窗只列"含本仓库物料"的 BOM。
            List<int> mine = null;
            if (_depot.IsRestricted)
            {
                mine = _depot.DepotIds.ToList();
                all = all.Where(b => b.FPItemID != null && _db.t_ERP_ITEM.Any(i => i.ID == b.FPItemID.Value
                                    && i.WarehouseId != null && mine.Contains(i.WarehouseId.Value)));
            }
            if (stepId != null && stepId.Value > 0)
                all = all.Where(b => b.FStepId == stepId.Value);
            if (kw.Length > 0)
            {
                // Match the BOM id itself, or any of its rows' product info.
                // 关键字匹配也必须收窄到本仓库——否则可以用别仓的商品名"反查"出本仓某张 BOM 的存在。
                var joined = _db.t_PMS_StepProductBom
                    .Join(_db.t_ERP_ITEM, b => b.FPItemID, i => (int?)i.ID, (b, i) => new { b, i })
                    .Where(x => (x.i.ItemCode != null && x.i.ItemCode.Contains(kw))
                             || (x.i.ItemShortName != null && x.i.ItemShortName.Contains(kw))
                             || (x.i.ItemSpec != null && x.i.ItemSpec.Contains(kw))
                             || (x.b.FStepName != null && x.b.FStepName.Contains(kw))
                             || (x.b.FBomID != null && x.b.FBomID == idMatch));
                if (mine != null)
                    joined = joined.Where(x => x.i.WarehouseId != null && mine.Contains(x.i.WarehouseId.Value));
                var bomIds = joined.Select(x => x.b.FBomID).Distinct().ToList();
                all = all.Where(b => bomIds.Contains(b.FBomID));
            }

            var rows = all.ToList();
            var itemIds = rows.Where(r => r.FPItemID != null).Select(r => r.FPItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            var result = rows
                .Where(r => r.FBomID != null)
                .GroupBy(r => r.FBomID.Value)
                .OrderByDescending(g => g.Key)
                .Take(50)
                .Select(g =>
                {
                    var list = g.ToList();
                    var outRow = list.FirstOrDefault(r => r.FIsProduct == true) ?? list.FirstOrDefault();
                    t_ERP_ITEM outItem = null;
                    if (outRow != null && outRow.FPItemID != null && items.ContainsKey(outRow.FPItemID.Value))
                        outItem = items[outRow.FPItemID.Value];
                    return new
                    {
                        bomId = g.Key,
                        stepId = list.First().FStepId ?? 0,
                        stepName = list.First().FStepName ?? "",
                        outputCpbm = outItem?.ItemCode ?? "",
                        outputName = outItem?.ItemShortName ?? "",
                        outputSpec = outItem?.ItemSpec ?? "",
                        itemCount = list.Count(r => r.FPItemID != null && r.FPItemID.Value > 0),
                        rawCount = list.Count(r => r.FIsProduct != true && r.FPItemID != null && r.FPItemID.Value > 0),
                    };
                })
                .ToList();
            return Json(result);
        }

        // GET /WarehousePick/GetBomDetails?bomId=5&qty=10
        // Returns every NON-output (FIsProduct != true) row of the given BOM bill, joined with
        // the product master, ready to be appended as picking rows.
        // - For each item we pick its oldest batch with positive ledger stock (FIFO hint) so the
        //   operator doesn't have to type the batch no by hand.
        // - Warehouse name comes from t_ERP_ITEM.WarehouseId -> t_ERP_Department (IsDEP=1).DEPName;
        //   t_PMS_BatchNoStock.FName is the operator name, NOT the warehouse.
        // - Each row's outNum is computed as FBaseNum * qty (qty = "amount of finished goods
        //   to be produced this pick").
        [HttpGet]
        public IActionResult GetBomDetails(int bomId, decimal? qty = null)
        {

            decimal produceQty = (qty != null && qty.Value > 0m) ? qty.Value : 1m;

            // 数据闸门②-f（选择器）：BOM 明细只返回本仓库的物料行 —— 这是"行级读"在 BOM 侧的对应，
            // 也是"新建单据天然单仓"的地基：原材料仓管理员拿到的表单里不会出现包装物行。
            var rowsQ = _db.t_PMS_StepProductBom.Where(b => b.FBomID == bomId);
            if (_depot.IsRestricted)
            {
                var mine = _depot.DepotIds.ToList();
                rowsQ = rowsQ.Where(b => b.FPItemID != null && _db.t_ERP_ITEM.Any(i => i.ID == b.FPItemID.Value
                                        && i.WarehouseId != null && mine.Contains(i.WarehouseId.Value)));
            }
            var rows = rowsQ
                .OrderBy(b => b.FID)
                .ToList();
            if (rows.Count == 0) return Json(new List<object>());

            var itemIds = rows.Where(r => r.FPItemID != null).Select(r => r.FPItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            // 仓库名唯一口径（见 LoadWarehouseNames）。
            var warehouses = LoadWarehouseNames(items.Values.Select(i => i.WarehouseId));

            // For each item, find the oldest enabled batch (FBegDate, FInterID) whose true
            // ledger stock is > 0. FBatchNum cache may be stale; use BatchQty().
            var firstBatch = new Dictionary<int, t_PMS_BatchNoStock>();
            var candidates = _db.t_PMS_BatchNoStock
                .Where(b => b.FItemID != null && itemIds.Contains(b.FItemID.Value) && (b.FState ?? false))
                .OrderBy(b => b.FItemID).ThenBy(b => b.FBegDate).ThenBy(b => b.FInterID)
                .ToList();

            // one GROUP BY for every candidate batch instead of BatchQty per row (N+1)
            var batchQtyMap = _stock.BatchQtyMap(_db,
                candidates.Select(c => c.FBatchNo).Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList());

            foreach (var b in candidates)
            {
                int iid = b.FItemID ?? 0;
                if (iid == 0 || firstBatch.ContainsKey(iid)) continue;
                if (batchQtyMap.TryGetValue(b.FBatchNo, out var bq) && bq > 0m) firstBatch[iid] = b;
            }

            var detail = rows
                .Where(r => r.FIsProduct != true && r.FPItemID != null && r.FPItemID.Value > 0)
                .Select(r =>
                {
                    var it = items.ContainsKey(r.FPItemID.Value) ? items[r.FPItemID.Value] : null;
                    var baseNum = r.FBaseNum ?? 0m;
                    // Both the "should-pick" qty and the "actually picked" qty scale with the
                    // amount of finished goods being produced. OutNum may be less than UseNum
                    // (partial pick) but never greater -- see ValidateBill.
                    var useNum = Math.Round(baseNum * produceQty, 2, MidpointRounding.AwayFromZero);
                    var outNum = useNum;

                    string batchNo = "", warehouseName = "";
                    decimal batchStock = 0m;
                    if (firstBatch.TryGetValue(r.FPItemID.Value, out var b))
                    {
                        batchNo = b.FBatchNo ?? "";
                        batchStock = batchQtyMap.TryGetValue(b.FBatchNo, out var bq) ? bq : 0m;
                    }
                    // 仓库名唯一口径：item.WarehouseId -> 部门；取不到就留空。
                    // 绝不回落 batch.FName —— 那是建批操作员，不是仓库。
                    warehouseName = WarehouseNameOf(warehouses, it?.WarehouseId);

                    return new
                    {
                        bomId = bomId,
                        stepId = r.FStepId ?? 0,
                        stepName = r.FStepName ?? "",
                        itemId = r.FPItemID.Value,
                        cpbm = it?.ItemCode ?? "",
                        cpjc = it?.ItemShortName ?? "",
                        cpgg = it?.ItemSpec ?? "",
                        cplb = it?.ProductCategory ?? "",
                        unit = string.IsNullOrWhiteSpace(r.FBaseUnit) ? (it?.BaseUnit ?? "") : r.FBaseUnit.Trim(),
                        baseNum = baseNum,
                        useNum = useNum,
                        outNum = outNum,
                        batchStock = batchStock,
                        batchNo = batchNo,
                        warehouseName = warehouseName,
                        remark = r.FRemark ?? "",
                        stopped = it == null || !(it.IsEnabled ?? false),
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

        // Batch info lookup for the manual row's BatchNo blur. Returns the batch's available
        // stock (BatchQty ledger calc) and warehouse name (item.WarehouseId -> t_ERP_Department) so
        // the row can refresh its "批次库存" / "仓库名称" display cells.
        // t_PMS_BatchNoStock.FName is the operator name, NOT the warehouse.
        [HttpGet]
        public IActionResult GetBatchInfo(string batchNo)
        {
            if (string.IsNullOrWhiteSpace(batchNo)) return Json(new { batchStock = 0m, warehouseName = "" });
            var b = _db.t_PMS_BatchNoStock.FirstOrDefault(x => x.FBatchNo == batchNo);
            if (b == null) return Json(new { batchStock = 0m, warehouseName = "" });
            // 数据闸门②-g（选择器）：批号查询按仓库收窄——否则可用批号"试探"别仓的结存量。
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
        // dropdown (ledger truth, not the FBatchNum cache). Out-stock direction:
        // only positive-qty batches are switchable targets.
        [HttpGet]
        public IActionResult GetItemBatches(int itemId)
        {
            if (itemId <= 0) return Json(new List<object>());
            // 数据闸门②-h（选择器）：别仓商品的批次列表不可查。
            if (!_depot.AllowsItem(itemId)) return Json(new List<object>());
            var rows = _stock.BatchesOfItem(_db, itemId)
                .Select(r => new { batchNo = r.FBatchNo, qty = r.q })
                .ToList();
            return Json(rows);
        }

        // ------------------------------------------------------------ helpers

        // ── 仓库名唯一口径（单点定义，四处调用：BOM导入 / 申请单导入 / Edit / Details）──────
        // 仓库名 = 物料主档 t_ERP_ITEM.WarehouseId -> t_ERP_Department(IsDEP=1).DEPName。
        // ⚠ 绝不可取 t_PMS_BatchNoStock.FName：该列存的是**建批操作员**，本库实测恒为
        //   「超级管理员」，与仓库毫无关系。历史上 ImportFromBillUse / Edit / Details 误用
        //   batch.FName，导致仓库名称显示成管理员名 —— 故收口至此，防止口径再次漂移。
        private Dictionary<int, string> LoadWarehouseNames(IEnumerable<int?> ckids)
        {
            var ids = ckids.Where(c => c != null && c.Value > 0).Select(c => c.Value).Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, string>();
            return _db.t_ERP_Department
                .Where(d => ids.Contains(d.ID) && d.IsDEP == 1)
                .ToDictionary(d => d.ID, d => d.DEPName ?? "");
        }

        private static string WarehouseNameOf(Dictionary<int, string> warehouses, int? ckid)
        {
            return (ckid != null && ckid.Value > 0 && warehouses.TryGetValue(ckid.Value, out var name))
                ? name : "";
        }

        // ── 批次库存唯一口径（单点定义，三处调用：申请单导入 / Edit / Details）──────────
        // 批次库存 = **实时账本**口径 StockService.BatchQtyMap（= BatchQty，一次 GROUP BY）。
        // ⚠ 不可直读 t_PMS_BatchNoStock.FBatchNum：那只是 RecalcBatch 从账本写回的**快照缓存**
        //   （BatchQty 的「输出」，不是输入）。而「批号下拉 / GetBatchDetails / 前端失焦刷新」
        //   走的是实时 BatchQty → 同一格出现两个来源，用户点一下批号框数字就变
        //   （实测 Form.cshtml:503 失焦即用 batchStock 覆盖该格）。
        //   用户裁定 2026-09-12：一律实时。
        //   不变量守卫 tests/probe_batch_cache_consistency.js（A1 缓存==账本）。
        private Dictionary<string, decimal> LoadBatchStock(IEnumerable<string> batchNos)
            => _stock.BatchQtyMap(_db, batchNos);

        private static decimal BatchStockOf(Dictionary<string, decimal> batchStock, string batchNo)
        {
            if (string.IsNullOrEmpty(batchNo)) return 0m;
            return batchStock.TryGetValue(batchNo.Trim(), out var q) ? q : 0m;
        }

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

        private Dictionary<int, t_ERP_ITEM> LoadItems(WarehousePickEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<WarehousePickRowInput>())
                .Where(r => r.ItemId > 0).Select(r => r.ItemId).Distinct().ToList();
            return _db.t_ERP_ITEM.Where(i => ids.Contains(i.ID)).ToDictionary(i => i.ID);
        }

        /// <summary>
        /// Trust boundary: re-derive BomId/StepId/ProduceNo/ProduceId/UseNum from
        /// t_PMS_BillUseEntry (server-side) for every row that links to a picking application.
        /// Overrides any tampered POST values. Rows with no BillUseEntryId (manual add) keep
        /// the posted values as-is.
        /// </summary>
        private void DeriveServerFields(WarehousePickEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<WarehousePickRowInput>())
                .Where(r => r.BillUseEntryId.HasValue && r.BillUseEntryId.Value > 0)
                .Select(r => r.BillUseEntryId.Value).Distinct().ToList();
            if (ids.Count == 0) return;
            var useEntries = _db.t_PMS_BillUseEntry.Where(u => ids.Contains(u.FEntryID))
                .ToDictionary(u => u.FEntryID);
            foreach (var r in vm.Rows)
            {
                if (!r.BillUseEntryId.HasValue || r.BillUseEntryId.Value == 0) continue;
                if (!useEntries.ContainsKey(r.BillUseEntryId.Value)) continue;
                var u = useEntries[r.BillUseEntryId.Value];
                r.BomId = u.FBomId;
                r.StepId = u.FStepID;
                r.ProduceNo = u.FProduceNo;
                r.ProduceId = u.FProduceId;
                r.UseNum = u.FUseNum;             // server-trusted; stored as FPlanNum for traceability / UI display (no longer enforced as a cap; BOM-imported rows have no plan qty)
            }
        }

        private bool ValidateBill(WarehousePickEditViewModel vm, Dictionary<int, t_ERP_ITEM> items)
        {
            if (vm.Date == default || vm.Date.Year < 2000)
            {
                ModelState.AddModelError("Date", "\u8bf7\u586b\u5199\u6b63\u786e\u7684\u5355\u636e\u65e5\u671f\uff01");
                return false;
            }
            var rows = (vm.Rows ?? new List<WarehousePickRowInput>()).Where(r => r.ItemId > 0).ToList();
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
                // Identify the offending row by product name so the operator knows which one.
                var label = string.IsNullOrWhiteSpace(r.ProductName)
                    ? ("\u7b2c " + (rows.IndexOf(r) + 1) + " \u884c")          // 第 N 行
                    : r.ProductName;
                // Sign check is unconditional: the waste-row exemption below waives "must be > 0"
                // and "must have a batch", NOT the sign. A negative out-qty would pass the ledger
                // balance check (avail < negative is always false) and then be recorded with
                // FROB=-1, silently CREDITING the batch by |FNum| -- inventory out of thin air.
                if ((r.OutNum ?? 0m) < 0m)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u51fa\u5e93\u6570\u91cf\u4e0d\u80fd\u4e3a\u8d1f\u6570\uff01");   // 【xx】出库数量不能为负数！
                    return false;
                }
                // By-product / waste rows (product category contains "\u6742\u8d28") are allowed
                // to have zero out-qty and no batch no: they are recorded on the picking bill for
                // traceability and will later be returned to stock via the warehouse return module.
                // NOTE: the category is taken from the SERVER-side item master (cplb) only.
                // r.CategoryName is a client round-trip display field and must never be trusted:
                // forging it would let any row claim the waste exemption.
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

            // 数据闸门③-a（写入）：提交行的商品必须全部属于本仓库。置于存在性校验之后、批次校验
            // 之前——"商品不存在"比"不属于你的仓库"更根本，先说更自然。admin 旁路不进入此段。
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
        private void SaveBill(WarehousePickEditViewModel vm, t_PMS_StockBill header,
            Dictionary<int, t_ERP_ITEM> items)
        {
            using (var tx = _db.Database.BeginTransaction())
            {
                var old = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (old.Count > 0) _db.t_PMS_StockBillEntry.RemoveRange(old);

                if (header.FInterID == 0 || !_db.t_PMS_StockBill.Any(h => h.FInterID == header.FInterID))
                    _db.t_PMS_StockBill.Add(header);

                foreach (var r in (vm.Rows ?? new List<WarehousePickRowInput>()).Where(r => r.ItemId > 0))
                {
                    var entry = new t_PMS_StockBillEntry
                    {
                        FInterID = header.FInterID,
                        FStepID = r.StepId ?? 0,                 // NOT NULL: 0 for manual rows
                        FBomId = r.BomId ?? 0,                   // NOT NULL: 0 for manual rows
                        FProduceNo = r.ProduceNo,
                        FProduceID = r.ProduceId,
                        FItemID = r.ItemId,
                        FNum = r.OutNum ?? 0m,                   // 出库数量 -> FNum
                        // price/amount are derived from the source batch (StampBatchPrice below)
                        FNote = string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note.Trim(),
                        FBatchNo = r.BatchNo ?? "",
                        FBatchNoID = BatchInterIdOf(r.BatchNo),
                        FBillUseEntryID = r.BillUseEntryId,
                        FBillUseNo = r.BillUseNo ?? "",
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

        /// <summary>
        /// Subset of the given pick bills (FBillType=8) that must NOT be un-audited because their
        /// materials were already consumed downstream by an AUDITED bill. Three links count:
        ///   1) direct   - a production stock-in entry (FBillType=1) carries FPickBillID = the pick
        ///      bill (imported straight from the pick bill, for non-packaging steps);
        ///   2) indirect - a stock-in entry (FBillType=1) carries FProduceID equal to one of the
        ///      pick bill's produce orders (imported from the produce order it belongs to);
        ///   3) return   - an AUDITED warehouse-return bill (FBillType=3) sourced from the pick
        ///      bill. FB=3 does NOT stamp FPickBillID; its back-link is the source bill no
        ///      (entry.FBillUseNo == pick bill FBillNo, see WarehouseReturnController.SaveBill).
        /// Any of them would be left as an orphan by un-auditing (restoring stock that is already
        /// gone, or an un-sourced stock increase in the ledger), so the bill is locked. Shared by
        /// the list page (button grey-out) and UnAudit (hard server-side guard) so both use one
        /// single source of truth.
        /// </summary>
        private HashSet<int> LoadLockedPickIds(List<int> pickIds)
        {
            var locked = new HashSet<int>();
            if (pickIds == null || pickIds.Count == 0) return locked;
            var pickSet = new HashSet<int>(pickIds);

            // 3) Audited warehouse-RETURN bills (FBillType=3) sourced from these pick bills.
            //    MUST be evaluated BEFORE the early returns below: those short-circuit when there
            //    is no FB=1 link, and this rule is independent of FB=1. (UnAudit passes a single
            //    id, the list page passes the page's ids — both must reach this check, otherwise
            //    the grey-out and the hard guard would disagree.)
            foreach (var pid in LockedByAuditedReturn(pickSet)) locked.Add(pid);

            var entries = _db.t_PMS_StockBillEntry
                .Where(e => pickSet.Contains(e.FInterID))
                .Select(e => new { e.FInterID, e.FProduceID }).ToList();
            var produceIds = entries.Where(e => e.FProduceID.HasValue && e.FProduceID.Value > 0)
                .Select(e => e.FProduceID.Value).Distinct().ToList();

            // candidate stock-in bills: direct pick-bill link OR same produce order
            var linkedBillIds = _db.t_PMS_StockBillEntry
                .Where(e => e.FPickBillID != null && pickSet.Contains(e.FPickBillID.Value))
                .Select(e => e.FInterID).Distinct().ToList();
            if (produceIds.Count > 0)
            {
                var moreIds = _db.t_PMS_StockBillEntry
                    .Where(e => e.FProduceID != null && produceIds.Contains(e.FProduceID.Value))
                    .Select(e => e.FInterID).Distinct().ToList();
                linkedBillIds.AddRange(moreIds);
            }
            linkedBillIds = linkedBillIds.Distinct().ToList();
            if (linkedBillIds.Count == 0) return locked;

            var auditedSet = new HashSet<int>(_db.t_PMS_StockBill
                .Where(h => linkedBillIds.Contains(h.FInterID) && h.FBillType == 1 && h.FState == true)
                .Select(h => h.FInterID).ToList());
            if (auditedSet.Count == 0) return locked;

            // 1) direct back-map: audited stock-in row -> pick bill
            var directBack = _db.t_PMS_StockBillEntry
                .Where(e => e.FPickBillID != null && pickSet.Contains(e.FPickBillID.Value))
                .Select(e => new { e.FInterID, PickBill = e.FPickBillID.Value }).ToList();
            foreach (var x in directBack)
                if (auditedSet.Contains(x.FInterID)) locked.Add(x.PickBill);

            // 2) indirect back-map: audited stock-in row -> produce order -> pick bills of that order
            if (produceIds.Count > 0)
            {
                var via = _db.t_PMS_StockBillEntry
                    .Where(e => e.FProduceID != null && produceIds.Contains(e.FProduceID.Value))
                    .Select(e => new { e.FInterID, Produce = e.FProduceID.Value }).ToList();
                var lockedProduce = new HashSet<int>(
                    via.Where(x => auditedSet.Contains(x.FInterID)).Select(x => x.Produce));
                foreach (var e in entries)
                    if (e.FProduceID.HasValue && lockedProduce.Contains(e.FProduceID.Value))
                        locked.Add(e.FInterID);
            }

            return locked;
        }

        /// <summary>
        /// Pick bills that must not be un-audited because an AUDITED warehouse-return bill
        /// (FBillType=3) sources rows from them. FB=3 never stamps FPickBillID — its back-link is
        /// the source bill no (return entry FBillUseNo == pick bill FBillNo, see
        /// WarehouseReturnController.SaveBill) — so this cannot be expressed with the FPickBillID
        /// join used by the FB=1 links. Un-auditing the pick would orphan the already-audited
        /// return, leaving an un-sourced (+stock) movement in the ledger.
        /// </summary>
        private HashSet<int> LockedByAuditedReturn(HashSet<int> pickSet)
        {
            var locked = new HashSet<int>();

            var pickNos = _db.t_PMS_StockBill
                .Where(h => pickSet.Contains(h.FInterID) && h.FBillNo != null && h.FBillNo != "")
                .Select(h => new { h.FInterID, h.FBillNo }).ToList();
            if (pickNos.Count == 0) return locked;

            var noToPickId = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var x in pickNos)
                if (!noToPickId.ContainsKey(x.FBillNo)) noToPickId[x.FBillNo] = x.FInterID;
            var pickNoList = noToPickId.Keys.ToList();

            var returnEntries = _db.t_PMS_StockBillEntry
                .Where(e => e.FBillUseNo != null && pickNoList.Contains(e.FBillUseNo))
                .Select(e => new { e.FInterID, e.FBillUseNo }).ToList();
            if (returnEntries.Count == 0) return locked;

            var returnBillIds = returnEntries.Select(x => x.FInterID).Distinct().ToList();
            var auditedReturnIds = new HashSet<int>(_db.t_PMS_StockBill
                .Where(h => returnBillIds.Contains(h.FInterID)
                            && h.FBillType == ReturnBillType && h.FState == true)
                .Select(h => h.FInterID).ToList());
            foreach (var x in returnEntries)
                if (auditedReturnIds.Contains(x.FInterID)
                    && noToPickId.TryGetValue(x.FBillUseNo, out var pid))
                    locked.Add(pid);

            return locked;
        }

        /// <summary>Add (add=true) or subtract (add=false) issued qty on the linked t_PMS_BillUseEntry rows.
        /// Splits across multiple batches naturally sum because each row points to the same useEntry.</summary>
        private void WriteBackBillUse(List<t_PMS_StockBillEntry> entries, bool add)
        {
            var ids = entries.Where(e => e.FBillUseEntryID.HasValue)
                             .Select(e => e.FBillUseEntryID.Value).Distinct().ToList();
            if (ids.Count == 0) return;
            var useEntries = _db.t_PMS_BillUseEntry.Where(u => ids.Contains(u.FEntryID)).ToList();
            foreach (var e in entries)
            {
                if (!e.FBillUseEntryID.HasValue) continue;
                var u = useEntries.FirstOrDefault(x => x.FEntryID == e.FBillUseEntryID.Value);
                if (u == null) continue;
                var delta = e.FNum ?? 0m;
                u.FCurrentUseNum = (u.FCurrentUseNum ?? 0m) + (add ? delta : -delta);
                if (u.FCurrentUseNum < 0m) u.FCurrentUseNum = 0m;
                // keep FLastUseNum (remaining issuable) consistent with FCurrentUseNum
                u.FLastUseNum = (u.FUseNum ?? 0m) - u.FCurrentUseNum;
                if (u.FLastUseNum < 0m) u.FLastUseNum = 0m;
            }
            _db.SaveChanges();
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

        // GET /WarehousePick/Export?dateFrom=&dateTo=&billNo=&state=
        [HttpGet]
        [HttpGet]
        public IActionResult Export(DateTime? dateFrom, DateTime? dateTo, string billNo, int? state)
        {
            billNo = billNo?.Trim();
            var rows = StockBillExport.BuildRows(_db, _depot, BillType, dateFrom, dateTo, billNo, null, state);
            var cols = new List<string> { "审核", "单据日期", "单据编号", "来源领料申请单", "批号", "产品代码", "分类", "产品名称", "规格", "单位", "出库数量" };
            var data = rows.Select(r => new List<string>
            {
                r.State ? "已审" : "未审",
                r.Date?.ToString("yyyy-MM-dd") ?? "",
                r.BillNo ?? "",
                r.FromBillUseNo ?? "", r.BatchNo ?? "",
                r.Cpbm ?? "", r.Cplb ?? "", r.Cpjc ?? "", r.Cpgg ?? "", r.UnitName ?? "",
                (r.Qty ?? 0m).ToString("0.00"),
            }).ToList();
            return ExcelExport.HtmlTable("仓库领用单_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".xls", cols, data);
        }
    }
}
