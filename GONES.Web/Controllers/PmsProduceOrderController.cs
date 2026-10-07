using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using GONES.Model.Pg;
using GONES.Web.Models;
using GONES.Web.Services;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// Production order (sheng chan dan) - Web replacement of the old WinForms
    /// Production.FrmProductionOrderManager / FrmProductionOrderEdit.
    ///
    /// Header  : t_PMS_ProduceOrder
    /// Entries : t_PMS_ProduceOrderProductEntry      (output products, edited in the form)
    ///           t_PMS_ProduceOrderStepEntry         (process step,   expanded from BOM)
    ///           t_PMS_ProduceOrderRawMaterialEntry  (raw materials,  expanded from BOM)
    ///
    /// New-world rules (user decisions 2026-09-06):
    ///   - FDeptID points to t_ERP_Department.ID (the deprecated t_PMS_WorkShop is no longer used;
    ///     legacy rows with FDeptID=4 belong to the old workshop table and simply show no dept name).
    ///   - Products must have a BOM (t_PMS_StepProductBom output row), same rule as goods apply.
    ///   - Steps and raw materials are derived from the BOM server-side; the form only shows a preview.
    ///   - There is NO separate dispatch step: the picking bill (t_PMS_BillUse / t_PMS_BillUseEntry)
    ///     is generated right here when the produce order is created or edited. One picking bill is
    ///     written per BOM; on every save the previously generated bills of this order are dropped
    ///     and rebuilt (whole-bill replace), FOrderStatus becomes 1 (picked) and FPickNo is written.
    ///
    /// Old rules kept: FBillNo "SCD"+6 digits (max+1); FInterID max+1; FComment written on audit;
    /// FStatus 0/1; FOrderStatus 0/1/2; unaudit needs FStatus=1 and FOrderStatus=0;
    /// audited orders can neither be edited nor deleted.
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // standard MVC form redisplay, skip Furion 400 short-circuit
    public class PmsProduceOrderController : Controller, IMenuGuarded
    {
        private const short StatusUnaudited = 0;
        private const short StatusAudited = 1;
        private const short OrderNotStarted = 0;
        private const short OrderPicked = 1;        // materials picked (picking bill generated)
        private const short OrderStocked = 2;       // stocked in
        private const string BillUsePrefix = "LLD";
        private const string DefaultProduceType = "\u81ea\u5236";   // self-made

        private readonly GonesPgDbContext _db;
        private readonly MenuService _menu;
        private readonly DepotScope _depot;
        public PmsProduceOrderController(GonesPgDbContext db, MenuService menu, DepotScope depot)
        { _db = db; _menu = menu; _depot = depot; }



        /// <summary>整单明细未全部落在当前用户管理的仓库内时的提示语（与 8 个单据模块同源）。</summary>
        private const string OutOfScopeMsg = "\u8be5\u5355\u636e\u5305\u542b\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u4ed3\u5e93\u7684\u660e\u7ec6\uff0c\u7981\u6b62\u64cd\u4f5c\uff01";   // 该单据包含不属于您管理仓库的明细，禁止操作！

        /// <summary>
        /// 整单是否全部落在当前用户的仓库范围内（admin 恒 true）。
        /// 生产订单的产出明细在 t_PMS_ProduceOrderProductEntry（不是 t_PMS_StockBillEntry），
        /// 故不能用 DepotScope.BillFullyInScope，改为按产出商品 ckid 判定。
        /// </summary>
        private bool InDepotScope(int id)
        {
            var itemIds = _db.t_PMS_ProduceOrderProductEntry
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

        // GET /PmsProduceOrder?dateFrom=&dateTo=&billNo=&status=&orderStatus=&page=&pageSize=
        public IActionResult Index(DateTime? dateFrom, DateTime? dateTo, string billNo,
            int? status, int? orderStatus, int page = 1, int pageSize = 20)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);
            billNo = billNo?.Trim();

            var hq = _db.t_PMS_ProduceOrder.AsQueryable();
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
            if (status.HasValue && status.Value >= 0)
            {
                var st = (short)status.Value;
                hq = hq.Where(h => h.FStatus == st);
            }
            if (orderStatus.HasValue && orderStatus.Value >= 0)
            {
                var ost = (short)orderStatus.Value;
                hq = hq.Where(h => h.FOrderStatus == ost);
            }

            // 仓库数据权限（读收窄，W1）：非 admin 只能看到"至少有一行产出属于本仓库"的订单。
            // 写操作由下面的 depotLocked + 服务端硬拦整套锁死。
            if (_depot.IsRestricted)
            {
                var depotIds = _depot.DepotIds;
                var visible = _db.t_PMS_ProduceOrderProductEntry
                    .Where(e => e.FItemID != null
                        && _db.t_ERP_ITEM.Any(i => i.ID == e.FItemID.Value && i.WarehouseId != null && depotIds.Contains(i.WarehouseId.Value)))
                    .Select(e => e.FInterID);
                hq = hq.Where(h => visible.Contains(h.FInterID));
            }

            int total = hq.Count();
            page = PagerViewModel.ClampPage(page, total, pageSize);
            var headers = hq.OrderByDescending(h => h.FInterID)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();
            var headerIds = headers.Select(h => h.FInterID).ToList();

            var products = _db.t_PMS_ProduceOrderProductEntry
                .Where(e => headerIds.Contains(e.FInterID))
                .OrderBy(e => e.FInterID).ThenBy(e => e.FEntryID)
                .ToList();
            var productsByBill = products.GroupBy(e => e.FInterID)
                .ToDictionary(g => g.Key, g => g.ToList());

            // 仓库数据权限③-b（按钮灰化）：与服务端 InDepotScope 硬拦同源；一次查询拿全部 ckid。
            var depotLocked = new HashSet<int>();
            if (_depot.IsRestricted)
            {
                var scoped = productsByBill.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Where(p => p.FItemID != null).Select(p => p.FItemID.Value).Distinct().ToList());
                var allScopedIds = scoped.Values.SelectMany(x => x).Distinct().ToList();
                var ckidOf = _db.t_ERP_ITEM.Where(i => allScopedIds.Contains(i.ID))
                    .Select(i => new { i.ID, i.WarehouseId })
                    .ToDictionary(x => x.ID, x => x.WarehouseId);
                var depotIds = _depot.DepotIds.ToHashSet();
                foreach (var kv in scoped)
                {
                    if (kv.Value.Count == 0) continue;                       // 无产出行 ⇒ 可动
                    if (!kv.Value.All(iid => ckidOf.ContainsKey(iid)
                                             && ckidOf[iid].HasValue
                                             && depotIds.Contains(ckidOf[iid].Value)))
                        depotLocked.Add(kv.Key);
                }
            }

            var itemIds = products.Where(p => p.FItemID != null).Select(p => p.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);
            var depts = _db.t_ERP_Department.ToDictionary(d => d.ID, d => d.DEPName);
            var workers = _db.t_PMS_Worker.ToDictionary(w => w.ID, w => w.FName);

            var rows = new List<ProduceOrderListRow>();
            foreach (var h in headers)
            {
                var list = productsByBill.ContainsKey(h.FInterID) ? productsByBill[h.FInterID] : new List<t_PMS_ProduceOrderProductEntry>();
                decimal totalQty = 0m;
                var names = new List<string>();
                foreach (var p in list)
                {
                    totalQty += p.FPlanNum ?? 0m;
                    if (p.FItemID != null && items.ContainsKey(p.FItemID.Value))
                    {
                        var nm = items[p.FItemID.Value].ItemShortName;
                        if (!string.IsNullOrEmpty(nm)) names.Add(nm);
                    }
                }
                string summary;
                if (names.Count == 0) summary = list.Count == 0 ? "\u6682\u65e0\u4ea7\u54c1\u660e\u7ec6" : "\u5546\u54c1\u5df2\u5220\u9664";
                else if (names.Count <= 2) summary = string.Join("\u3001", names);
                else summary = names[0] + "\u3001" + names[1] + " \u7b49 " + names.Count + " \u9879";

                rows.Add(new ProduceOrderListRow
                {
                    InterId = h.FInterID,
                    BillNo = h.FBillNo,
                    Date = h.FDate,
                    Status = h.FStatus,
                    OrderStatus = h.FOrderStatus,
                    DeptName = (h.FDeptID != null && depts.ContainsKey(h.FDeptID.Value)) ? depts[h.FDeptID.Value] : "",
                    ApplyerName = (h.FApplyerID != null && workers.ContainsKey(h.FApplyerID.Value)) ? workers[h.FApplyerID.Value] : "",
                    ApplyNo = h.FApplyNo ?? "",
                    ProductCount = list.Count,
                    PlanTotal = totalQty,
                    ProductSummary = summary,
                    Remark = h.FRemark ?? "",
                    CanModify = !depotLocked.Contains(h.FInterID),
                });
            }

            ViewBag.Filter = new Dictionary<string, string>
            {
                ["dateFrom"] = dateFrom?.ToString("yyyy-MM-dd") ?? "",
                ["dateTo"] = dateTo?.ToString("yyyy-MM-dd") ?? "",
                ["billNo"] = billNo ?? "",
                ["status"] = status?.ToString() ?? "",
                ["orderStatus"] = orderStatus?.ToString() ?? "",
            };

            var extra = new Dictionary<string, object>();
            if (dateFrom.HasValue) extra["dateFrom"] = dateFrom.Value.ToString("yyyy-MM-dd");
            if (dateTo.HasValue) extra["dateTo"] = dateTo.Value.ToString("yyyy-MM-dd");
            if (!string.IsNullOrEmpty(billNo)) extra["billNo"] = billNo;
            if (status.HasValue) extra["status"] = status.Value;
            if (orderStatus.HasValue) extra["orderStatus"] = orderStatus.Value;
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

        // ------------------------------------------------------------ item search (BOM outputs only)

        // GET /PmsProduceOrder/SearchItems?keyword=xxx
        [HttpGet]
        public IActionResult SearchItems(string keyword)
        {

            var kw = (keyword ?? string.Empty).Trim();
            var bomMap = LoadBomMap();

            int idMatch = int.TryParse(kw, out var n) ? n : -1;
            // 数据闸门②-c（选择器）：商品弹窗只能搜到本仓库的商品。列表看不见了但弹窗还能选到
            // 别仓商品并提交，是"假权限"最容易漏的一环。
            IQueryable<t_ERP_ITEM> q = _depot.FilterItems(_db.t_ERP_ITEM).Where(i => i.IsEnabled == true);
            if (kw.Length > 0)
            {
                q = q.Where(i => (i.ItemCode != null && i.ItemCode.Contains(kw))
                                 || (i.ItemShortName != null && i.ItemShortName.Contains(kw))
                                 || (i.ItemSpec != null && i.ItemSpec.Contains(kw))
                                 || i.ID == idMatch);
            }
            var list = q
                .OrderBy(i => i.ItemCode)
                .Take(50)
                .ToList()
                .Where(i => bomMap.ContainsKey(i.ID))
                .Select(i => new
                {
                    id = i.ID,
                    cpbm = i.ItemCode,
                    cpjc = i.ItemShortName,
                    cpgg = i.ItemSpec,
                    cplb = i.ProductCategory,
                    unit = i.BaseUnit,
                    bomId = bomMap[i.ID],
                })
                .ToList();
            return Json(list);
        }

        // ------------------------------------------------------------ BOM expansion preview

        // GET /PmsProduceOrder/BomDetail?bomId=1&planNum=10
        [HttpGet]
        public IActionResult BomDetail(int bomId, int itemId, decimal? planNum)
        {
            if (bomId <= 0) return Json(new { step = (object)null, group = (object)null });

            var qty = planNum ?? 0m;
            var pair = BuildBomExpansion(new List<Tuple<int, int, decimal>> { Tuple.Create(itemId, bomId, qty) });
            return Json(new { step = pair.Item1.FirstOrDefault(), group = pair.Item2.FirstOrDefault() });
        }

        // ------------------------------------------------------------ pull from goods apply bill

        // GET /PmsProduceOrder/SearchApplyBills?keyword=JHD
        [HttpGet]
        public IActionResult SearchApplyBills(string keyword)
        {

            var kw = (keyword ?? string.Empty).Trim();
            var q = _db.t_PMS_GoodsApply.Where(h => h.FStatus == 1);   // audited only
            if (kw.Length > 0) q = q.Where(h => h.FBillNo.Contains(kw));

            var bills = q.OrderByDescending(h => h.FInterID).Take(20).ToList();
            // 剩余量 = SUM(FQty) - SUM(已被生产单计划占用量)（按单聚合）。
            // 计划占用来自 t_PMS_ProduceOrderProductEntry.FPlanNum，按 FBillApplyEntryID
            // 关联回要货申请行；只要被引入生产单（无论审核/入库与否）即计入。
            var billIds = bills.Select(h => h.FInterID).ToList();
            var applyEntries = _db.t_PMS_GoodsApplyEntry
                .Where(e => billIds.Contains(e.FInterID))
                .ToList();
            var refs = applyEntries.Select(e => (e.FInterID, e.FEntryID)).ToList();
            var usedMap = StockService.UsedPlanByApplyEntryIds(_db, refs, null);
            var remainMap = applyEntries
                .GroupBy(e => e.FInterID)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(e => e.FQty ?? 0m) - g.Sum(e => usedMap.ContainsKey((e.FInterID, e.FEntryID)) ? usedMap[(e.FInterID, e.FEntryID)] : 0m));

            var workers = _db.t_PMS_Worker.ToDictionary(w => w.ID, w => w.FName);
            var list = bills
                .Select(h => new
                {
                    interId = h.FInterID,
                    billNo = h.FBillNo,
                    date = h.FDate?.ToString("yyyy-MM-dd"),
                    empId = h.FEmpID,
                    empName = (h.FEmpID != null && workers.ContainsKey(h.FEmpID.Value)) ? workers[h.FEmpID.Value] : "",
                    remain = remainMap.ContainsKey(h.FInterID) ? remainMap[h.FInterID] : 0m,
                })
                .ToList();
            return Json(list);
        }

        // GET /PmsProduceOrder/ApplyBillEntries?interId=1[&excludeProduceInterId=2]
        [HttpGet]
        public IActionResult ApplyBillEntries(int interId, int? excludeProduceInterId)
        {

            var bomMap = LoadBomMap();
            var entries = _db.t_PMS_GoodsApplyEntry
                .Where(e => e.FInterID == interId)
                .OrderBy(e => e.FEntryID)
                .ToList();

            var ids = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => ids.Contains(i.ID)).ToDictionary(i => i.ID);

            var refs = entries.Select(e => (e.FInterID, e.FEntryID)).ToList();
            var usedMap = StockService.UsedPlanByApplyEntryIds(_db, refs, excludeProduceInterId);

            int skipped = 0;
            var list = new List<object>();
            foreach (var e in entries)
            {
                if (e.FItemID == null || !items.ContainsKey(e.FItemID.Value) || !bomMap.ContainsKey(e.FItemID.Value))
                {
                    skipped++;
                    continue;
                }
                var it = items[e.FItemID.Value];
                var applyQty = e.FQty ?? 0m;
                var used = usedMap.ContainsKey((e.FInterID, e.FEntryID)) ? usedMap[(e.FInterID, e.FEntryID)] : 0m;
                // 第二次（及以后）引入时，默认计划数量 = 剩余量 = Max(0, 申请量 - 已计划占用量)。
                // 已计划占用量来自所有引用了该要货行的生产单明细 FPlanNum（含草稿、未审核），
                // 编辑时通过 excludeProduceInterId 排除自身。
                var remain = Math.Max(0m, applyQty - used);
                // 行导入状态：0 未导入 / 1 部分导入 / 2 全部导入（以计划占用量与申请量比较；
                // 不防超产下 used 可≥FQty，此时记为 2=全部导入）。
                int entryStatus = used <= 0m ? 0 : (used >= applyQty ? 2 : 1);
                list.Add(new
                {
                    entryId = e.FEntryID,
                    itemId = it.ID,
                    cpbm = it.ItemCode,
                    cpjc = it.ItemShortName,
                    cpgg = it.ItemSpec,
                    cplb = it.ProductCategory,
                    unit = it.BaseUnit,
                    bomId = bomMap[it.ID],
                    qty = applyQty,        // 申请数量（展示用）
                    remain = remain,        // 剩余量（默认引入计划数量）
                    entryStatus = entryStatus,
                });
            }
            return Json(new { skipped = skipped, rows = list });
        }

        // ------------------------------------------------------------ create

        // GET /PmsProduceOrder/Create
        [HttpGet]
        public IActionResult Create()
        {

            BindFormExtras();
            var vm = new ProduceOrderEditViewModel
            {
                BillNo = NextBillNo(),
                Date = DateTime.Today,
                DeptId = DefaultDeptId(),
                Rows = new List<ProduceOrderRowInput>(),
            };
            ViewBag.StepPreview = new List<ProduceOrderStepPreview>();
            ViewBag.MaterialGroups = new List<ProduceOrderBomMaterialGroup>();
            BindRowInfo(vm);
            return View("Form", vm);
        }

        // POST /PmsProduceOrder/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(ProduceOrderEditViewModel vm)
        {

            var items = LoadItems(vm);
            var bomMap = LoadBomMap();
            if (!ValidateBill(vm, items, bomMap))
            {
                BindFormExtras();
                BindPreview(vm, bomMap);
                return View("Form", vm);
            }

            // Generate bill no + inter id AND persist under one lock so concurrent
            // creates cannot read the same max and collide on the primary key.
            // (lock is reentrant; NextInterId/NextBillNo also lock the same object.)
            lock (BillNoLock)
            {
                var header = new t_PMS_ProduceOrder
                {
                    FInterID = NextInterId(),
                    FBillNo = NextBillNo(),          // regenerate on save (old system behavior)
                    FDate = vm.Date,
                    FDeptID = vm.DeptId,
                    FCreaterID = CurrentUserId,
                    FApplyerID = ResolveApplyerId(vm.ApplyerId, vm.ApplyNo),
                    FApplyNo = vm.ApplyNo ?? "",
                    FApplyDate = string.IsNullOrEmpty(vm.ApplyNo) ? (DateTime?)null : DateTime.Today,
                    FComment = "",
                    FStatus = StatusUnaudited,
                    FAuditorID = null,
                    FAuditDate = null,
                    FRemark = vm.Remark ?? "",
                    FScheduleNo = vm.ScheduleNo ?? "",
                    FScheduleNoID = null,
                    FOrderStatus = OrderNotStarted,
                    FWorkShopID = null,
                    FPickNo = null,
                    FModifyerID = null,
                    FModifyDate = null,
                };
                SaveBill(header, vm, items, bomMap);

                var pickTip = string.IsNullOrEmpty(header.FPickNo)
                    ? ""
                    : "\uff0c\u5df2\u540c\u6b65\u751f\u6210\u9886\u6599\u5355 " + header.FPickNo;
                TempData["Success"] = "\u751f\u4ea7\u5355\u4fdd\u5b58\u6210\u529f\uff0c\u5355\u53f7 " + header.FBillNo + pickTip + "\u3002";
            }
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ edit

        // GET /PmsProduceOrder/Edit/5 (FInterID)
        [HttpGet]
        public IActionResult Edit(int id)
        {

            var header = _db.t_PMS_ProduceOrder.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            if (header.FStatus != StatusUnaudited)
            {
                TempData["Error"] = "\u751f\u4ea7\u5355\u5df2\u5ba1\u6838\uff0c\u7981\u6b62\u4fee\u6539\uff01";
                return RedirectToAction(nameof(Index));
            }

            var rows = _db.t_PMS_ProduceOrderProductEntry
                .Where(e => e.FInterID == id)
                .OrderBy(e => e.FEntryID)
                .ToList()
                .Select(e => new ProduceOrderRowInput
                {
                    ItemId = e.FItemID ?? 0,
                    PlanNum = e.FPlanNum,
                    ProduceType = string.IsNullOrEmpty(e.FProduceType) ? DefaultProduceType : e.FProduceType,
                    SrcEntryId = e.FBillApplyEntryID,
                })
                .Where(r => r.ItemId > 0)
                .ToList();

            var vm = new ProduceOrderEditViewModel
            {
                InterId = header.FInterID,
                BillNo = header.FBillNo,
                Date = header.FDate ?? DateTime.Today,
                DeptId = header.FDeptID,
                ApplyerId = header.FApplyerID,
                ApplyNo = header.FApplyNo,
                ScheduleNo = header.FScheduleNo,
                Remark = header.FRemark,
                Status = header.FStatus,
                OrderStatus = header.FOrderStatus,
                Rows = rows,
            };
            BindFormExtras();
            BindPickInfo(vm, id);
            BindSavedPreview(id);
            BindRowInfo(vm);
            return View("Form", vm);
        }

        // GET /PmsProduceOrder/Detail/5 - read-only view (audited orders cannot be edited)
        [HttpGet]
        public IActionResult Detail(int id)
        {

            var header = _db.t_PMS_ProduceOrder.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }

            var rows = _db.t_PMS_ProduceOrderProductEntry
                .Where(e => e.FInterID == id)
                .OrderBy(e => e.FEntryID)
                .ToList()
                .Select(e => new ProduceOrderRowInput
                {
                    ItemId = e.FItemID ?? 0,
                    PlanNum = e.FPlanNum,
                    ProduceType = string.IsNullOrEmpty(e.FProduceType) ? DefaultProduceType : e.FProduceType,
                    SrcEntryId = e.FBillApplyEntryID,
                })
                .Where(r => r.ItemId > 0)
                .ToList();

            var vm = new ProduceOrderEditViewModel
            {
                InterId = header.FInterID,
                BillNo = header.FBillNo,
                Date = header.FDate ?? DateTime.Today,
                DeptId = header.FDeptID,
                ApplyerId = header.FApplyerID,
                ApplyNo = header.FApplyNo,
                ScheduleNo = header.FScheduleNo,
                Remark = header.FRemark,
                Status = header.FStatus,
                OrderStatus = header.FOrderStatus,
                Rows = rows,
            };
            BindFormExtras();
            BindPickInfo(vm, id);
            BindSavedPreview(id);
            BindRowInfo(vm);
            ViewBag.ReadOnly = true;
            return View("Form", vm);
        }

        // POST /PmsProduceOrder/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, ProduceOrderEditViewModel vm)
        {

            var header = _db.t_PMS_ProduceOrder.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            if (header.FStatus != StatusUnaudited)
            {
                TempData["Error"] = "\u751f\u4ea7\u5355\u5df2\u5ba1\u6838\uff0c\u7981\u6b62\u4fee\u6539\uff01";
                return RedirectToAction(nameof(Index));
            }

            vm.InterId = id;
            vm.BillNo = header.FBillNo;   // bill no is immutable

            var items = LoadItems(vm);
            var bomMap = LoadBomMap();
            if (!ValidateBill(vm, items, bomMap))
            {
                BindFormExtras();
                BindPreview(vm, bomMap);
                return View("Form", vm);
            }

            // Whole save (order + picking bill) runs under the shared lock: the picking
            // bill no / inter id are allocated inside SaveBill from max+1.
            lock (BillNoLock)
            {
                header.FDate = vm.Date;
                header.FDeptID = vm.DeptId;
                header.FApplyerID = ResolveApplyerId(vm.ApplyerId, vm.ApplyNo);
                header.FApplyNo = vm.ApplyNo ?? "";
                header.FApplyDate = string.IsNullOrEmpty(vm.ApplyNo) ? (DateTime?)null : (header.FApplyDate ?? DateTime.Today);
                header.FRemark = vm.Remark ?? "";
                header.FScheduleNo = vm.ScheduleNo ?? "";
                header.FModifyerID = CurrentUserId;
                header.FModifyDate = DateTime.Now;
                header.FAuditorID = CurrentUserId;      // per user semantics: FAuditorID/FAuditDate = last modifier
                header.FAuditDate = DateTime.Now;
                SaveBill(header, vm, items, bomMap);

                var pickTip = string.IsNullOrEmpty(header.FPickNo)
                    ? ""
                    : "\uff0c\u5df2\u540c\u6b65\u751f\u6210\u9886\u6599\u5355 " + header.FPickNo;
                TempData["Success"] = "\u751f\u4ea7\u5355\u4fee\u6539\u6210\u529f" + pickTip + "\u3002";
            }
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ delete (whole bill)

        // POST /PmsProduceOrder/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {

            var header = _db.t_PMS_ProduceOrder.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            if (header.FStatus != StatusUnaudited)
            {
                TempData["Error"] = "\u751f\u4ea7\u5355\u5df2\u5ba1\u6838\uff0c\u7981\u6b62\u5220\u9664\uff01";
                return RedirectToAction(nameof(Index));
            }

            // tri_deletebilluse / tri_deleteBillUseEntry triggers dropped (2026-09-09):
            // picking-bill cleanup is explicit (DeleteGeneratedBillUse removes entries too),
            // so all child rows + header go in one SaveChanges inside the transaction.
            using (var tx = _db.Database.BeginTransaction())
            {
                // Drop the picking bills that were generated together with this order.
                DeleteGeneratedBillUse(id);

                var steps = _db.t_PMS_ProduceOrderStepEntry.Where(e => e.FInterID == id).ToList();
                if (steps.Count > 0) _db.t_PMS_ProduceOrderStepEntry.RemoveRange(steps);

                var mats = _db.t_PMS_ProduceOrderRawMaterialEntry.Where(e => e.FInterID == id).ToList();
                if (mats.Count > 0) _db.t_PMS_ProduceOrderRawMaterialEntry.RemoveRange(mats);

                var products = _db.t_PMS_ProduceOrderProductEntry.Where(e => e.FInterID == id).ToList();
                if (products.Count > 0) _db.t_PMS_ProduceOrderProductEntry.RemoveRange(products);

                // 本单占用的要货申请行（删除后占用消失，需回退其导入状态）。
                // 复合键 (FBillApplyInterID, FBillApplyEntryID) 精确回写，避免跨单同号误判。
                var affectedApply = products
                    .Where(e => e.FBillApplyEntryID > 0 && e.FBillApplyInterID > 0)
                    .Select(e => (e.FBillApplyInterID.Value, e.FBillApplyEntryID.Value))
                    .ToList();

                _db.t_PMS_ProduceOrder.Remove(header);
                _db.SaveChanges();

                if (affectedApply.Count > 0)
                {
                    StockService.RewriteApplyEntryStatus(_db, affectedApply);
                    _db.SaveChanges();
                }

                tx.Commit();
            }

            TempData["Success"] = "\u751f\u4ea7\u5355\u5220\u9664\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ audit / unaudit

        // POST /PmsProduceOrder/Audit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Audit(int id)
        {

            var header = _db.t_PMS_ProduceOrder.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            if (header.FStatus == StatusAudited)
            {
                TempData["Error"] = "\u751f\u4ea7\u5355\u5df2\u5ba1\u6838\uff0c\u65e0\u9700\u91cd\u590d\u5ba1\u6838\uff01";
                return RedirectToAction(nameof(Index));
            }

            header.FStatus = StatusAudited;
            header.FAuditorID = CurrentUserId;
            header.FAuditDate = DateTime.Now;
            header.FComment = "\u540c\u610f";
            _db.SaveChanges();

            // 审核不改变计划占用，但按统一约定刷新要货申请行导入状态（幂等）。
            // 复合键 (FBillApplyInterID, FBillApplyEntryID) 精确回写，避免跨单同号误判。
            var affectedApplyAudit = _db.t_PMS_ProduceOrderProductEntry
                .Where(e => e.FInterID == id && e.FBillApplyEntryID != null && e.FBillApplyInterID != null)
                .Select(e => new { e.FBillApplyInterID, e.FBillApplyEntryID })
                .ToList()
                .Select(e => (e.FBillApplyInterID.Value, e.FBillApplyEntryID.Value))
                .ToList();
            if (affectedApplyAudit.Count > 0)
            {
                StockService.RewriteApplyEntryStatus(_db, affectedApplyAudit);
                _db.SaveChanges();
            }

            TempData["Success"] = "\u5ba1\u6838\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index));
        }

        // POST /PmsProduceOrder/UnAudit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UnAudit(int id)
        {

            var header = _db.t_PMS_ProduceOrder.FirstOrDefault(h => h.FInterID == id);
            if (header == null) return NotFound();
            if (!InDepotScope(id))
            {
                TempData["Error"] = OutOfScopeMsg;
                return RedirectToAction(nameof(Index));
            }
            if (header.FStatus != StatusAudited)
            {
                TempData["Error"] = "\u751f\u4ea7\u5355\u672a\u5ba1\u6838\uff0c\u65e0\u9700\u53cd\u5ba1\u6838\uff01";
                return RedirectToAction(nameof(Index));
            }
            // Picking bills are generated together with the order now, so FOrderStatus is
            // already 1 (picked) right after saving; only a stocked-in order blocks unaudit.
            if ((header.FOrderStatus ?? 0) == OrderStocked)
            {
                TempData["Error"] = "\u751f\u4ea7\u5355\u5df2\u5165\u5e93\uff0c\u4e0d\u80fd\u53cd\u5ba1\u6838\uff01";
                return RedirectToAction(nameof(Index));
            }

            header.FStatus = StatusUnaudited;
            header.FAuditorID = 0;
            header.FAuditDate = DateTime.Now;
            header.FComment = "";
            _db.SaveChanges();

            // 反审核不改变计划占用，但按统一约定刷新要货申请行导入状态（幂等）。
            // 复合键 (FBillApplyInterID, FBillApplyEntryID) 精确回写，避免跨单同号误判。
            var affectedApplyUnAudit = _db.t_PMS_ProduceOrderProductEntry
                .Where(e => e.FInterID == id && e.FBillApplyEntryID != null && e.FBillApplyInterID != null)
                .Select(e => new { e.FBillApplyInterID, e.FBillApplyEntryID })
                .ToList()
                .Select(e => (e.FBillApplyInterID.Value, e.FBillApplyEntryID.Value))
                .ToList();
            if (affectedApplyUnAudit.Count > 0)
            {
                StockService.RewriteApplyEntryStatus(_db, affectedApplyUnAudit);
                _db.SaveChanges();
            }

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

        private Dictionary<int, t_ERP_ITEM> LoadItems(ProduceOrderEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<ProduceOrderRowInput>())
                .Where(r => r.ItemId > 0)
                .Select(r => r.ItemId)
                .Distinct()
                .ToList();
            return _db.t_ERP_ITEM.Where(i => ids.Contains(i.ID)).ToDictionary(i => i.ID);
        }

        /// <summary>Business validation: department, at least one product, BOM required, qty &gt; 0, no duplicates.</summary>
        // Fallback: when no applicant was chosen on the form, take the applicant
        // (FEmpID) from the referenced goods-apply bill so FApplyerID is never lost.
        private int? ResolveApplyerId(int? applyerId, string applyNo)
        {
            if (applyerId != null) return applyerId;
            if (string.IsNullOrWhiteSpace(applyNo)) return null;
            var ga = _db.t_PMS_GoodsApply.FirstOrDefault(h => h.FBillNo == applyNo);
            return ga?.FEmpID;
        }

        private bool ValidateBill(ProduceOrderEditViewModel vm, Dictionary<int, t_ERP_ITEM> items, Dictionary<int, int> bomMap)
        {
            if (vm.DeptId == null || vm.DeptId.Value <= 0)
                ModelState.AddModelError("DeptId", "\u8bf7\u9009\u62e9\u751f\u4ea7\u90e8\u95e8\uff01");

            var rows = (vm.Rows ?? new List<ProduceOrderRowInput>()).Where(r => r.ItemId > 0).ToList();
            if (rows.Count == 0)
            {
                ModelState.AddModelError("Rows", "\u8bf7\u81f3\u5c11\u6dfb\u52a0\u4e00\u4e2a\u4ea7\u54c1\uff01");
                return false;
            }

            if (rows.Select(r => r.ItemId).Distinct().Count() != rows.Count)
            {
                ModelState.AddModelError("Rows", "\u4ea7\u54c1\u660e\u7ec6\u4e2d\u5b58\u5728\u91cd\u590d\u5546\u54c1\uff01");
                return false;
            }

            // 仓库数据权限（W1）：整单产出商品必须全部落在当前用户管理的仓库内。
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
                if ((r.PlanNum ?? 0m) <= 0m)
                {
                    ModelState.AddModelError("Rows", "\u8ba1\u5212\u6570\u91cf\u5fc5\u987b\u5927\u4e8e 0\uff01");
                    return false;
                }
            }

            // 分次生产：同一要货计划单（FApplyNo）允许被多张生产单引用，累计量不限制。
            // 业务裁定 2026-09-10：允许“一分多次”、不防超产，故此处不对该来源单号做唯一拦截；
            // 重复导入的弹窗取数见 ApplyBillEntries（仅按 FInterID 列出要货申请全部行，不过滤已引用行）。
            // 已审核生产单的领用历史保护见下方 GeneratedBillUseAlreadyUsed 守卫（L722 附近）。

            // The picking bill is written per product on save, so each product row must
            // carry its own picking dept / workshop / worker.
            foreach (var r in rows)
            {
                var nm = items.ContainsKey(r.ItemId) ? items[r.ItemId].ItemShortName : ("#" + r.ItemId);
                if (r.PickDeptId == null || r.PickDeptId.Value <= 0)
                    ModelState.AddModelError("Rows", "\u5546\u54c1\u201c" + nm + "\u201d \u672a\u9009\u62e9\u9886\u6599\u90e8\u95e8\uff01");
                if (r.PickWorkShopId == null || r.PickWorkShopId.Value <= 0)
                    ModelState.AddModelError("Rows", "\u5546\u54c1\u201c" + nm + "\u201d \u672a\u9009\u62e9\u9886\u6599\u8f66\u95f4\uff01");
                if (r.PickWorkerId == null || r.PickWorkerId.Value <= 0)
                    ModelState.AddModelError("Rows", "\u5546\u54c1\u201c" + nm + "\u201d \u672a\u9009\u62e9\u9886\u6599\u4eba\uff01");
            }

            // Rebuilding the picking bill would silently drop picking history, so refuse
            // to touch an order whose generated bill was already consumed.
            if (vm.InterId > 0 && GeneratedBillUseAlreadyUsed(vm.InterId))
                ModelState.AddModelError("", "\u8be5\u751f\u4ea7\u5355\u751f\u6210\u7684\u9886\u6599\u5355\u5df2\u88ab\u9886\u7528\uff0c\u4e0d\u80fd\u518d\u4fee\u6539\u751f\u4ea7\u5355\uff01");

            return ModelState.IsValid;
        }

        /// <summary>Replace-all save: delete old entries, re-insert products + BOM-derived steps and raw materials.</summary>
        private void SaveBill(t_PMS_ProduceOrder header, ProduceOrderEditViewModel vm,
            Dictionary<int, t_ERP_ITEM> items, Dictionary<int, int> bomMap)
        {
            var rows = (vm.Rows ?? new List<ProduceOrderRowInput>()).Where(r => r.ItemId > 0).ToList();

            // BOM expansion input: (itemId, bomId, planQty)
            var plan = rows.Select(r => Tuple.Create(r.ItemId, bomMap[r.ItemId], r.PlanNum ?? 0m)).ToList();
            var expansion = BuildBomExpansion(plan);

            using (var tx = _db.Database.BeginTransaction())
            {
                bool exists = _db.t_PMS_ProduceOrder.Any(h => h.FInterID == header.FInterID);

                // Whole-bill replace: drop the picking bills generated by the previous save
                // (their entries go away through the AFTER DELETE trigger on t_PMS_BillUse).
                if (exists) DeleteGeneratedBillUse(header.FInterID);

                var oldSteps = _db.t_PMS_ProduceOrderStepEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (oldSteps.Count > 0) _db.t_PMS_ProduceOrderStepEntry.RemoveRange(oldSteps);
                var oldMats = _db.t_PMS_ProduceOrderRawMaterialEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (oldMats.Count > 0) _db.t_PMS_ProduceOrderRawMaterialEntry.RemoveRange(oldMats);
                var oldProducts = _db.t_PMS_ProduceOrderProductEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (oldProducts.Count > 0) _db.t_PMS_ProduceOrderProductEntry.RemoveRange(oldProducts);

                if (!exists) _db.t_PMS_ProduceOrder.Add(header);

                var productEntries = new List<t_PMS_ProduceOrderProductEntry>();

                // 预载 要货行FEntryID -> 要货单FInterID，用于回填新生产行 FBillApplyInterID（与 FBillApplyEntryID 组成复合键）。
                var srcEntryIds = rows.Where(r => r.SrcEntryId > 0).Select(r => r.SrcEntryId.Value).Distinct().ToList();
                var applyEntryToBill = srcEntryIds.Count == 0
                    ? new Dictionary<int, int>()
                    : _db.t_PMS_GoodsApplyEntry.Where(e => srcEntryIds.Contains(e.FEntryID))
                        .Select(e => new { e.FEntryID, e.FInterID })
                        .ToDictionary(e => e.FEntryID, e => e.FInterID);

                foreach (var r in rows)
                {
                    var type = string.IsNullOrWhiteSpace(r.ProduceType) ? DefaultProduceType : r.ProduceType.Trim();
                    var pe = new t_PMS_ProduceOrderProductEntry
                    {
                        FInterID = header.FInterID,
                        FItemID = r.ItemId,
                        FPlanNum = r.PlanNum ?? 0m,
                        FPlanDate = vm.Date,
                        FBomId = bomMap[r.ItemId],
                        FBatchNumber = header.FBillNo,
                        FProduceType = type,
                        FOutFactory = "",
                        FOutPrice = 0m,
                        FOutAmount = 0m,
                        FPickingStatus = 0,
                        FProductionPickingStatus = 0,
                        FStockStatus = 0,
                        FBillUseFInterID = null,
                        // Traceability back to the goods-apply row: production in-stock uses
                        // this to accumulate FFetchNum (finished qty) on the apply entry.
                        FBillApplyEntryID = (r.SrcEntryId ?? 0) > 0 ? r.SrcEntryId : null,
                        FBillApplyInterID = (r.SrcEntryId > 0 && applyEntryToBill.ContainsKey(r.SrcEntryId.Value))
                            ? applyEntryToBill[r.SrcEntryId.Value] : (int?)null,
                    };
                    _db.t_PMS_ProduceOrderProductEntry.Add(pe);
                    productEntries.Add(pe);
                }

                foreach (var s in expansion.Item1)
                {
                    _db.t_PMS_ProduceOrderStepEntry.Add(new t_PMS_ProduceOrderStepEntry
                    {
                        FInterID = header.FInterID,
                        FStepID = s.StepId,
                        FAlgo = s.Algo,
                        FHour = s.Hour,
                        FNum = s.Num,
                        FSingleArti = s.SingleArti,
                        FHourArti = s.HourArti,
                        FPrice = s.Price,
                        FWorkHourUnit = s.WorkHourUnit,
                        FPriceUnit = s.PriceUnit,
                        FFixedLoss = s.FixedLoss,
                        FLossStandValue = s.LossStandValue,
                        FLossRate = s.LossRate,
                        FLossValue = s.LossValue,
                        FLossUnit = s.LossUnit,
                        FOutPutNum = 0m,
                        FPlanOutPutNum = 0m,
                        FBomId = s.BomId,
                        FItemID = s.ItemId,
                    });
                }

                foreach (var grp in expansion.Item2)
                {
                    foreach (var m in grp.Materials)
                    {
                        _db.t_PMS_ProduceOrderRawMaterialEntry.Add(new t_PMS_ProduceOrderRawMaterialEntry
                        {
                            FInterID = header.FInterID,
                            FStepID = m.StepId,
                            FNum = m.BaseNum,
                            FWorkHourUnit = m.WorkHourUnit,
                            FPriceUnit = m.PriceUnit,
                            FFixedLoss = m.FixedLoss,
                            FLossStandValue = m.LossStandValue,
                            FLossRate = m.LossRate,
                            FLossValue = m.LossValue,
                            FLossUnit = m.LossUnit,
                            FNeedNum = m.NeedNum,
                            FBomId = m.BomId,
                            FRemark = "",
                            FItemID = m.ItemId,
                        });
                    }
                }

                // Picking bill(s) in the very same transaction as the order itself.
                GenerateBillUse(header, vm, expansion.Item2, productEntries);

                _db.SaveChanges();

                // 统一回写要货申请行"导入状态" FStatus（0 未导入/1 部分导入/2 全部导入）：
                // 基于本单提交后的最新计划占用（含草稿），与审核态无关。受影响行 = 本单
                // 旧产品行(编辑时被替换) + 本次新建产品行的来源要货行。
                // 复合键 (FBillApplyInterID, FBillApplyEntryID) 精确回写（旧行取自库、新行取自表单 SrcEntryId 映射）。
                var affectedApply = oldProducts
                    .Where(e => e.FBillApplyEntryID > 0 && e.FBillApplyInterID > 0)
                    .Select(e => (e.FBillApplyInterID.Value, e.FBillApplyEntryID.Value))
                    .Concat(rows.Where(r => r.SrcEntryId > 0 && applyEntryToBill.ContainsKey(r.SrcEntryId.Value))
                        .Select(r => (applyEntryToBill[r.SrcEntryId.Value], r.SrcEntryId.Value)))
                    .Where(k => k.Item1 > 0 && k.Item2 > 0)
                    .ToList();
                if (affectedApply.Count > 0)
                {
                    StockService.RewriteApplyEntryStatus(_db, affectedApply);
                    _db.SaveChanges();
                }

                tx.Commit();
            }
        }

        // ------------------------------------------------------------ picking bill (generated together with the order)

        /// <summary>
        /// Write one t_PMS_BillUse per BOM group plus its entries, then push the result back
        /// onto the order (FPickNo / FOrderStatus) and onto the product rows.
        /// </summary>
        private void GenerateBillUse(t_PMS_ProduceOrder header, ProduceOrderEditViewModel vm,
            List<ProduceOrderBomMaterialGroup> groups, List<t_PMS_ProduceOrderProductEntry> productEntries)
        {
            if (groups == null || groups.Count == 0) return;

            // Only UseNum is trusted from the client; BomId/ItemId/IsProduct/StepId are
            // re-derived here so a tampered POST cannot invent picking detail rows.
            var posted = BuildPostedUseNumMap(vm);
            var pickNos = new List<string>();

            // Map each product to its posted row so the picking bill inherits that
            // product's own dept / workshop / group / worker (picking follows the product).
            var rowByItem = (vm.Rows ?? new List<ProduceOrderRowInput>())
                .Where(r => r.ItemId > 0)
                .GroupBy(r => r.ItemId)
                .ToDictionary(g => g.Key, g => g.First());

            // Seed the id / bill-no sequence ONCE, then increment locally. The DB-backed
            // Max() cannot see the rows we are about to add in this same SaveChanges, so
            // querying it per-iteration would hand every bill the same FInterID and collide
            // on the primary key. The lock guards against a concurrent request reading the
            // seed before our rows are committed.
            int nextId, nextNo;
            lock (BillNoLock)
            {
                nextId = _db.t_PMS_BillUse.Max(h => (int?)h.FInterID) ?? 0;
                nextNo = 0;
                foreach (var bn in _db.t_PMS_BillUse.Select(h => h.FBillNo))
                {
                    if (string.IsNullOrEmpty(bn) || bn.Length != BillUsePrefix.Length + 8 || !bn.StartsWith(BillUsePrefix)) continue;
                    int no;
                    if (int.TryParse(bn.Substring(BillUsePrefix.Length), out no) && no > nextNo) nextNo = no;
                }
            }

            foreach (var g in groups)
            {
                // Each picking bill follows its product row: the picking dept / workshop /
                // group / worker are taken from that product's row, not a shared order field.
                ProduceOrderRowInput row = null;
                if (g.Output != null && g.Output.ItemId != null)
                    rowByItem.TryGetValue(g.Output.ItemId.Value, out row);
                if (row == null) continue;   // defensive: a group without a matching posted row

                nextId += 1;
                nextNo += 1;
                int billUseId = nextId;
                string billUseNo = BillUsePrefix + nextNo.ToString("00000000");
                pickNos.Add(billUseNo);

                _db.t_PMS_BillUse.Add(new t_PMS_BillUse
                {
                    FInterID = billUseId,
                    FBillNo = billUseNo,
                    FBillType = "1",
                    FDate = row.PickDate ?? header.FDate,
                    FDeptID = row.PickDeptId,
                    FCreaterID = CurrentUserId,
                    FRemark = row.PickRemark ?? "",
                    FWorkShopID = row.PickWorkShopId,
                    FWorkerID = row.PickWorkerId,
                    FGroupID = row.PickGroupId,
                    FWorkPeople = row.PickWorkPeople ?? "",
                });

                if (g.Output != null && g.Output.ItemId != null)
                {
                    // Output qty is server-owned: always the planned qty, never the posted one.
                    AddBillUseEntry(billUseId, g.BomId, g.Output.StepId ?? g.BomId,
                        g.Output.ItemId.Value, g.Output.PlanNum ?? 0m, true, header);
                }
                foreach (var m in g.Materials)
                {
                    if (m.ItemId == null) continue;
                    var use = TakeUseNum(posted, g.BomId ?? 0, m.ItemId.Value, false, m.NeedNum ?? 0m);
                    AddBillUseEntry(billUseId, g.BomId, m.StepId ?? g.BomId, m.ItemId.Value, use, false, header);
                }

                var pe = productEntries.FirstOrDefault(p => p.FBomId == g.BomId);
                if (pe != null)
                {
                    pe.FPickingStatus = 1;
                    pe.FBillUseFInterID = billUseId;
                }
            }

            if (productEntries.Count > 0 && productEntries.All(p => p.FPickingStatus == 1))
                header.FOrderStatus = OrderPicked;

            if (pickNos.Count > 0)
            {
                var joined = string.Join(",", pickNos);
                header.FPickNo = joined.Length > 30 ? joined.Substring(0, 30) : joined;
            }
        }

        private void AddBillUseEntry(int billUseId, int? bomId, int? stepId, int itemId, decimal useNum,
            bool isProduct, t_PMS_ProduceOrder header)
        {
            _db.t_PMS_BillUseEntry.Add(new t_PMS_BillUseEntry
            {
                FInterID = billUseId,
                FStepID = stepId ?? 0,
                FBomId = bomId ?? 0,
                FUseNum = useNum,
                FProduceNo = header.FBillNo,
                FProduceId = header.FInterID,
                FItemID = itemId,
                FCurrentUseNum = 0m,
                FLastUseNum = 0m,
                FScheduleNo = "",
                FScheduleNoID = 0,
                FIsProduct = isProduct,
                FIsUse = false,
            });
        }

        /// <summary>Posted picking quantities keyed by BomId|ItemId|IsProduct (FIFO per key).</summary>
        private static Dictionary<string, Queue<decimal?>> BuildPostedUseNumMap(ProduceOrderEditViewModel vm)
        {
            var map = new Dictionary<string, Queue<decimal?>>();
            foreach (var r in vm.PickRows ?? new List<ProduceOrderPickRowInput>())
            {
                if (r == null || r.ItemId <= 0) continue;
                var key = PickRowKey(r.BomId, r.ItemId, r.IsProduct);
                Queue<decimal?> q;
                if (!map.TryGetValue(key, out q)) { q = new Queue<decimal?>(); map[key] = q; }
                q.Enqueue(r.UseNum);
            }
            return map;
        }

        private static string PickRowKey(int? bomId, int? itemId, bool isProduct)
        {
            return (bomId ?? 0) + "|" + (itemId ?? 0) + "|" + (isProduct ? "1" : "0");
        }

        private static decimal TakeUseNum(Dictionary<string, Queue<decimal?>> posted,
            int bomId, int itemId, bool isProduct, decimal fallback)
        {
            Queue<decimal?> q;
            if (posted != null && posted.TryGetValue(PickRowKey(bomId, itemId, isProduct), out q) && q.Count > 0)
            {
                var v = q.Dequeue();
                if (v.HasValue && v.Value >= 0m) return v.Value;
            }
            return fallback < 0m ? 0m : fallback;
        }

        /// <summary>Keep whatever the user typed when a validation failure redisplays the form.</summary>
        private static void ApplyUseNum(List<ProduceOrderBomMaterialGroup> groups, ProduceOrderEditViewModel vm)
        {
            if (groups == null || groups.Count == 0) return;
            var posted = BuildPostedUseNumMap(vm);
            foreach (var g in groups)
            {
                if (g.Output != null)
                    g.Output.UseNum = g.Output.PlanNum ?? 0m;   // server-owned
                foreach (var m in g.Materials)
                    m.UseNum = TakeUseNum(posted, g.BomId ?? 0, m.ItemId ?? 0, false, m.NeedNum ?? 0m);
            }
        }

        /// <summary>
        /// Drop the picking bills generated for this order. Entry cleanup is explicit:
        /// the tri_deleteBillUseEntry cascade trigger was dropped (2026-09-09).
        /// </summary>
        private void DeleteGeneratedBillUse(int produceOrderId)
        {
            var ids = _db.t_PMS_BillUseEntry
                .Where(e => e.FProduceId == produceOrderId)
                .Select(e => e.FInterID)
                .Distinct()
                .ToList();
            if (ids.Count == 0) return;

            var entries = _db.t_PMS_BillUseEntry.Where(e => ids.Contains(e.FInterID)).ToList();
            if (entries.Count > 0) _db.t_PMS_BillUseEntry.RemoveRange(entries);

            var heads = _db.t_PMS_BillUse.Where(h => ids.Contains(h.FInterID)).ToList();
            if (heads.Count == 0) return;   // defensive: caller's SaveChanges flushes the entry deletes
            _db.t_PMS_BillUse.RemoveRange(heads);
            _db.SaveChanges();
        }

        /// <summary>True when a generated picking bill was already consumed (rebuilding would lose history).</summary>
        private bool GeneratedBillUseAlreadyUsed(int produceOrderId)
        {
            var ids = _db.t_PMS_BillUseEntry
                .Where(e => e.FProduceId == produceOrderId)
                .Select(e => e.FInterID)
                .Distinct()
                .ToList();
            if (ids.Count == 0) return false;

            return _db.t_PMS_BillUseEntry.Any(e => ids.Contains(e.FInterID)
                && (e.FIsUse == true || (e.FCurrentUseNum ?? 0m) > 0m || (e.FLastUseNum ?? 0m) > 0m));
        }

        // ------------------------------------------------------------ picking bill form helpers

        /// <summary>Cascading drop-downs for the picking bill: department -&gt; workshop -&gt; group -&gt; worker.</summary>

        /// <summary>Restore the picking-bill fields and per-row qty from the bills already generated.</summary>
        private void BindPickInfo(ProduceOrderEditViewModel vm, int produceOrderId)
        {
            var ids = _db.t_PMS_BillUseEntry
                .Where(e => e.FProduceId == produceOrderId)
                .Select(e => e.FInterID)
                .Distinct()
                .ToList();

            var heads = ids.Count == 0
                ? new List<t_PMS_BillUse>()
                : _db.t_PMS_BillUse.Where(h => ids.Contains(h.FInterID)).OrderBy(h => h.FInterID).ToList();

            // Display-only: the generated picking bill numbers.
            vm.PickNo = heads.Count > 0 ? string.Join(",", heads.Select(h => h.FBillNo)) : "";

            // Per-product picking info: map each bill use header back to its output product row.
            var rowByItem = (vm.Rows ?? new List<ProduceOrderRowInput>())
                .Where(r => r.ItemId > 0)
                .GroupBy(r => r.ItemId)
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var h in heads)
            {
                var outEntry = _db.t_PMS_BillUseEntry
                    .FirstOrDefault(e => e.FInterID == h.FInterID && e.FIsProduct == true);
                if (outEntry?.FItemID != null && rowByItem.ContainsKey(outEntry.FItemID.Value))
                {
                    var row = rowByItem[outEntry.FItemID.Value];
                    row.PickDeptId = h.FDeptID;
                    row.PickWorkShopId = h.FWorkShopID;
                    row.PickGroupId = h.FGroupID;
                    row.PickWorkerId = h.FWorkerID;
                    row.PickDate = h.FDate ?? vm.Date;
                    row.PickWorkPeople = h.FWorkPeople ?? "";
                    row.PickRemark = h.FRemark ?? "";
                }
            }

            var entries = ids.Count == 0
                ? new List<t_PMS_BillUseEntry>()
                : _db.t_PMS_BillUseEntry.Where(e => ids.Contains(e.FInterID)).ToList();

            var map = new Dictionary<string, decimal>();
            foreach (var e in entries)
            {
                var key = PickRowKey(e.FBomId, e.FItemID, e.FIsProduct == true);
                if (!map.ContainsKey(key)) map[key] = e.FUseNum ?? 0m;
            }
            ViewBag.PickUseNum = map;
        }

        /// <summary>
        /// Expand BOMs into step rows + raw material rows.
        /// <summary>
        /// Input: (itemId, bomId, planQty) per output product.
        /// Step row comes from t_PMS_Step (PK = FItemID) referenced by the BOM output row's FStepId;
        /// material rows come from the BOM input rows (FIsProduct = 0) of the same FBomID.
        /// Output product rows are added per group to match the old WinForms product-structure view.
        /// </summary>
        private Tuple<List<ProduceOrderStepPreview>, List<ProduceOrderBomMaterialGroup>> BuildBomExpansion(
            List<Tuple<int, int, decimal>> plan)
        {
            var steps = new List<ProduceOrderStepPreview>();
            var groups = new List<ProduceOrderBomMaterialGroup>();
            if (plan == null || plan.Count == 0) return Tuple.Create(steps, groups);

            var bomIds = plan.Select(p => p.Item2).Distinct().ToList();
            var bomRows = _db.t_PMS_StepProductBom
                .Where(b => bomIds.Contains(b.FBomID ?? 0) && (b.FDelete ?? 0) == 0)
                .ToList();

            var stepIds = bomRows.Where(b => b.FStepId != null).Select(b => b.FStepId.Value).Distinct().ToList();
            var stepMap = _db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID)).ToDictionary(s => s.FItemID);

            var matItemIds = bomRows.Where(b => b.FIsProduct != true && b.FPItemID != null)
                .Select(b => b.FPItemID.Value).Distinct().ToList();
            var outputItemIds = plan.Select(p => p.Item1).Distinct().ToList();
            var itemIds = outputItemIds.Union(matItemIds).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            foreach (var p in plan)
            {
                var itemId = p.Item1;
                var bomId = p.Item2;
                var qty = p.Item3;

                var rows = bomRows.Where(b => (b.FBomID ?? 0) == bomId).ToList();
                var outRow = rows.FirstOrDefault(b => b.FIsProduct == true) ?? rows.FirstOrDefault();
                string stepName = null;
                int? outStepId = null;
                if (outRow != null && outRow.FStepId != null && stepMap.ContainsKey(outRow.FStepId.Value))
                {
                    var st = stepMap[outRow.FStepId.Value];
                    steps.Add(new ProduceOrderStepPreview
                    {
                        StepId = st.FItemID,
                        StepName = st.FName,
                        Algo = st.FAlgo,
                        Hour = st.FHour,
                        Num = st.FNum,
                        SingleArti = st.FSingleArti,
                        HourArti = st.FHourArti,
                        Price = st.FPrice,
                        WorkHourUnit = st.FWorkHourUnit,
                        PriceUnit = st.FPriceUnit,
                        FixedLoss = st.FFixedLoss,
                        LossStandValue = st.FLossStandValue,
                        LossRate = st.FLossRate,
                        LossValue = st.FLossValue,
                        LossUnit = st.FLossUnit,
                        BomId = bomId,
                        ItemId = itemId,
                    });
                    stepName = st.FName;
                    outStepId = st.FItemID;
                }

                var group = new ProduceOrderBomMaterialGroup { BomId = bomId, StepName = stepName };
                t_ERP_ITEM outputItem = null;
                if (items.ContainsKey(itemId)) outputItem = items[itemId];
                group.Output = new ProduceOrderMaterialPreview
                {
                    BomId = bomId,
                    ItemId = itemId,
                    ItemNumber = outputItem?.ItemCode,
                    ItemName = outputItem?.ItemShortName,
                    ItemModel = outputItem?.ItemSpec,
                    ItemCategory = outputItem?.ProductCategory,
                    Unit = outputItem?.BaseUnit,
                    StockQty = outputItem?.StockQuantity,
                    PlanNum = qty,
                    NeedNum = qty,
                    UseNum = qty,
                    StepId = outStepId,
                    StepName = stepName,
                };

                foreach (var b in rows.Where(x => x.FIsProduct != true))
                {
                    if (b.FPItemID == null) continue;
                    t_ERP_ITEM it = null;
                    items.TryGetValue(b.FPItemID.Value, out it);
                    var baseNum = b.FBaseNum ?? 0m;
                    t_PMS_Step matStep = null;
                    if (b.FStepId != null && stepMap.ContainsKey(b.FStepId.Value))
                        matStep = stepMap[b.FStepId.Value];

                    var needNum = baseNum * qty;
                    group.Materials.Add(new ProduceOrderMaterialPreview
                    {
                        BomId = bomId,
                        ItemId = b.FPItemID,
                        ItemNumber = it?.ItemCode,
                        ItemName = it?.ItemShortName,
                        ItemModel = it?.ItemSpec,
                        ItemCategory = it?.ProductCategory,
                        Unit = string.IsNullOrEmpty(b.FBaseUnit) ? (it?.BaseUnit) : b.FBaseUnit,
                        StockQty = it?.StockQuantity,
                        BaseNum = baseNum,
                        PlanNum = 0m,
                        NeedNum = needNum,
                        UseNum = needNum,
                        StepId = b.FStepId,
                        StepName = matStep?.FName,
                        WorkHourUnit = matStep?.FWorkHourUnit,
                        PriceUnit = matStep?.FPriceUnit,
                        FixedLoss = matStep?.FFixedLoss,
                        LossStandValue = matStep?.FLossStandValue,
                        LossRate = b.FLossRate,
                        LossValue = b.FLossValue,
                        LossUnit = b.FLossUnit,
                    });
                }

                groups.Add(group);
            }

            return Tuple.Create(steps, groups);
        }

        /// <summary>Preview tables from the posted rows (validation redisplay).</summary>
        private void BindPreview(ProduceOrderEditViewModel vm, Dictionary<int, int> bomMap)
        {
            var rows = (vm.Rows ?? new List<ProduceOrderRowInput>()).Where(r => r.ItemId > 0 && bomMap.ContainsKey(r.ItemId)).ToList();
            var plan = rows.Select(r => Tuple.Create(r.ItemId, bomMap[r.ItemId], r.PlanNum ?? 0m)).ToList();
            var exp = BuildBomExpansion(plan);
            ApplyUseNum(exp.Item2, vm);   // keep the picking qty the user already typed
            ViewBag.StepPreview = exp.Item1;
            ViewBag.MaterialGroups = exp.Item2;
            BindRowInfo(vm);
        }

        /// <summary>Preview tables loaded from the entries already saved in the database (edit / view).</summary>
        private void BindSavedPreview(int interId)
        {
            var pickUse = (Dictionary<string, decimal>)ViewBag.PickUseNum ?? new Dictionary<string, decimal>();
            Func<int?, int?, bool, decimal?> GetUse = (bomId, itemId, isProduct) =>
            {
                decimal v;
                return pickUse.TryGetValue(PickRowKey(bomId, itemId, isProduct), out v) ? v : (decimal?)null;
            };

            var stepRows = _db.t_PMS_ProduceOrderStepEntry
                .Where(e => e.FInterID == interId)
                .OrderBy(e => e.FEntryID)
                .ToList();
            var stepIds = stepRows.Where(e => e.FStepID != null).Select(e => e.FStepID.Value).Distinct().ToList();
            var stepMap = _db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID)).ToDictionary(s => s.FItemID);

            var steps = stepRows.Select(e => new ProduceOrderStepPreview
            {
                StepId = e.FStepID,
                StepName = (e.FStepID != null && stepMap.ContainsKey(e.FStepID.Value)) ? stepMap[e.FStepID.Value].FName : "",
                Algo = e.FAlgo,
                Hour = e.FHour,
                Num = e.FNum,
                SingleArti = e.FSingleArti,
                HourArti = e.FHourArti,
                Price = e.FPrice,
                WorkHourUnit = e.FWorkHourUnit,
                PriceUnit = e.FPriceUnit,
                FixedLoss = e.FFixedLoss,
                LossStandValue = e.FLossStandValue,
                LossRate = e.FLossRate,
                LossValue = e.FLossValue,
                LossUnit = e.FLossUnit,
                BomId = e.FBomId,
                ItemId = e.FItemID,
            }).ToList();

            var productRows = _db.t_PMS_ProduceOrderProductEntry
                .Where(e => e.FInterID == interId)
                .OrderBy(e => e.FEntryID)
                .ToList();
            var matRows = _db.t_PMS_ProduceOrderRawMaterialEntry
                .Where(e => e.FInterID == interId)
                .OrderBy(e => e.FEntryID)
                .ToList();

            var itemIds = productRows.Where(e => e.FItemID != null).Select(e => e.FItemID.Value)
                .Union(matRows.Where(e => e.FItemID != null).Select(e => e.FItemID.Value))
                .Distinct()
                .ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            var stepNameByBom = stepRows
                .Where(e => e.FBomId != null && e.FStepID != null && stepMap.ContainsKey(e.FStepID.Value))
                .GroupBy(e => e.FBomId.Value)
                .ToDictionary(g => g.Key, g => stepMap[g.First().FStepID.Value].FName);

            var groups = productRows.Select(pe =>
            {
                t_ERP_ITEM it = null;
                if (pe.FItemID != null) items.TryGetValue(pe.FItemID.Value, out it);
                string stepName = null;
                if (stepNameByBom.ContainsKey(pe.FBomId))
                    stepName = stepNameByBom[pe.FBomId];

                return new ProduceOrderBomMaterialGroup
                {
                    BomId = pe.FBomId,
                    StepName = stepName,
                    Output = new ProduceOrderMaterialPreview
                    {
                        BomId = pe.FBomId,
                        ItemId = pe.FItemID,
                        ItemNumber = it?.ItemCode,
                        ItemName = it?.ItemShortName,
                        ItemModel = it?.ItemSpec,
                        ItemCategory = it?.ProductCategory,
                        Unit = it?.BaseUnit,
                        StockQty = it?.StockQuantity,
                        PlanNum = pe.FPlanNum,
                        NeedNum = pe.FPlanNum,
                        UseNum = GetUse(pe.FBomId, pe.FItemID, true) ?? pe.FPlanNum ?? 0m,
                        StepName = stepName,
                    },
                    Materials = matRows
                        .Where(e => e.FBomId == pe.FBomId)
                        .Select(e =>
                        {
                            t_ERP_ITEM mit = null;
                            if (e.FItemID != null) items.TryGetValue(e.FItemID.Value, out mit);
                            return new ProduceOrderMaterialPreview
                            {
                                BomId = e.FBomId,
                                ItemId = e.FItemID,
                                ItemNumber = mit?.ItemCode,
                                ItemName = mit?.ItemShortName,
                                ItemModel = mit?.ItemSpec,
                                ItemCategory = mit?.ProductCategory,
                                Unit = mit?.BaseUnit,
                                StockQty = mit?.StockQuantity,
                                BaseNum = e.FNum,
                                PlanNum = 0m,
                                NeedNum = e.FNeedNum,
                                UseNum = GetUse(e.FBomId, e.FItemID, false) ?? e.FNeedNum ?? 0m,
                                StepId = e.FStepID,
                                StepName = (e.FStepID != null && stepMap.ContainsKey(e.FStepID.Value)) ? stepMap[e.FStepID.Value].FName : "",
                                WorkHourUnit = e.FWorkHourUnit,
                                PriceUnit = e.FPriceUnit,
                                FixedLoss = e.FFixedLoss,
                                LossStandValue = e.FLossStandValue,
                                LossRate = e.FLossRate,
                                LossValue = e.FLossValue,
                                LossUnit = e.FLossUnit,
                            };
                        })
                        .ToList()
                };
            }).ToList();

            ViewBag.StepPreview = steps;
            ViewBag.MaterialGroups = groups;
        }

        /// <summary>Item snapshots + BOM ids for rows already in the form.</summary>
        private void BindRowInfo(ProduceOrderEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<ProduceOrderRowInput>())
                .Where(r => r.ItemId > 0)
                .Select(r => r.ItemId)
                .Distinct()
                .ToList();
            ViewBag.RowItems = _db.t_ERP_ITEM.Where(i => ids.Contains(i.ID)).ToDictionary(i => i.ID, i => i);

            var bomMap = LoadBomMap();
            ViewBag.RowBom = ids.ToDictionary(id => id, id => bomMap.ContainsKey(id) ? bomMap[id] : 0);
        }

        // Process-wide lock serializes "read max + 1" so concurrent creates/dispatch cannot
        // yield the same bill no / inter id (single Kestrel process per deploy).
        private static readonly object BillNoLock = new object();

        /// <summary>"SCD" + 6-digit sequence = max(FBillNo)+1 (old GetProduceOrderBillNo).</summary>
        private string NextBillNo()
        {
            lock (BillNoLock)
            {
                // Only consider well-formed "SCD"+6-digit bill numbers; dirty/legacy
                // values are ignored so they cannot poison the sequence or collide.
                int maxNo = 0;
                foreach (var bn in _db.t_PMS_ProduceOrder.Select(h => h.FBillNo))
                {
                    if (string.IsNullOrEmpty(bn) || bn.Length != 9 || !bn.StartsWith("SCD")) continue;
                    int no;
                    if (int.TryParse(bn.Substring(3), out no) && no > maxNo) maxNo = no;
                }
                // If the sequence is exhausted (>=999999) the 7-digit result is still
                // unique (never a duplicate); widening is preferable to colliding.
                return "SCD" + (maxNo + 1).ToString("000000");
            }
        }

        private int NextInterId()
        {
            lock (BillNoLock)
            {
                var max = _db.t_PMS_ProduceOrder.Max(h => (int?)h.FInterID) ?? 0;
                return max + 1;
            }
        }

        /// <summary>Default department: the one named production dept, else the first active department.</summary>
        private int? DefaultDeptId()
        {
            var prod = _db.t_ERP_Department.FirstOrDefault(d => d.DEPName == "\u751f\u4ea7\u90e8" && d.Status == 1);
            if (prod != null) return prod.ID;
            var first = _db.t_ERP_Department.FirstOrDefault(d => d.Status == 1 && d.ParentID == 1);
            return first?.ID;
        }

        /// <summary>Form data: departments, workers (applicant).</summary>
        private void BindFormExtras()
        {
            // Used only by the per-product "picking dept" drop-down. It must list real
            // departments (IsDEP=0) - workshops (2) / groups (3) / warehouses (1) belong to
            // the next cascade levels, letting them appear here produced invalid picks.
            ViewBag.DeptOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.ID != 1 && d.IsDEP == 0)
                .OrderBy(d => d.ParentID).ThenBy(d => d.ID)
                .ToList();

            ViewBag.DepTree = BuildDeptTree();

            ViewBag.WorkerOptions = _db.t_PMS_Worker
                .Where(w => w.Status == true)
                .OrderBy(w => w.ID)
                .ToList();

            var deptIds = _db.t_ERP_Department.Where(d => d.Status == 1).Select(d => d.ID).ToList();
            ViewBag.DeptNames = _db.t_ERP_Department
                .Where(d => deptIds.Contains(d.ID))
                .ToDictionary(d => d.ID, d => d.DEPName);
        }

        // ------------------------------------------------------------ dept tree + worker cascade

        /// <summary>Build a nested department tree from the given department set (ParentID self-reference).</summary>
        private static List<DepartmentNode> BuildTree(List<t_ERP_Department> all)
        {
            var nodes = all.ToDictionary(
                d => d.ID,
                d => new DepartmentNode
                {
                    Id = d.ID,
                    Name = d.DEPName,
                    ParentId = d.ParentID,
                    IsDep = d.IsDEP,
                    Enabled = d.Status == 1,
                });

            var roots = new List<DepartmentNode>();
            foreach (var node in nodes.Values)
            {
                bool hasParent = node.ParentId.HasValue
                                 && node.ParentId.Value != 0
                                 && nodes.ContainsKey(node.ParentId.Value);
                if (hasParent)
                    nodes[node.ParentId.Value].Children.Add(node);
                else
                    roots.Add(node);
            }
            SetDepth(roots, 0);
            return roots;
        }

        private static void SetDepth(List<DepartmentNode> nodes, int depth)
        {
            foreach (var n in nodes)
            {
                n.Depth = depth;
                n.ChildCount = n.Children.Count;
                SetDepth(n.Children, depth + 1);
            }
        }

        /// <summary>Active departments (exclude synthetic root ID=1) as a selectable tree.</summary>
        private List<DepartmentNode> BuildDeptTree()
        {
            var all = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.ID != 1)
                .OrderBy(d => d.ParentID).ThenBy(d => d.ID)
                .ToList();
            return BuildTree(all);
        }

        /// <summary>Collect a department and all of its descendant IDs (visited set guards against cycles).</summary>
        private static HashSet<int> CollectDescendantDeptIds(List<t_ERP_Department> all, int rootId)
        {
            var set = new HashSet<int> { rootId };
            var queue = new Queue<int>();
            queue.Enqueue(rootId);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                foreach (var child in all.Where(d => d.ParentID == cur))
                    if (set.Add(child.ID))
                        queue.Enqueue(child.ID);
            }
            return set;
        }

        // GET /PmsProduceOrder/GetWorkersByDept?deptId=3
        // Returns active workers belonging to the selected department and its descendants (t_PMS_Worker.DEPID).
        [HttpGet]
        public JsonResult GetWorkersByDept(int deptId)
        {

            var all = _db.t_ERP_Department.ToList();
            if (!all.Any(d => d.ID == deptId))
                return Json(new { items = new List<object>() });

            var scope = CollectDescendantDeptIds(all, deptId);
            var list = _db.t_PMS_Worker
                .Where(w => w.Status == true && scope.Contains(w.DEPID))
                .OrderBy(w => w.ID)
                .Select(w => new { id = w.ID, name = w.FName })
                .ToList();
            return Json(new { items = list });
        }

        // GET /PmsProduceOrder/GetWorkShops?deptId=65  -> workshops (IsDEP=2) directly under the dept
        [HttpGet]
        public JsonResult GetWorkShops(int deptId)
        {
            var list = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 2 && d.ParentID == deptId)
                .OrderBy(d => d.ID)
                .Select(d => new { id = d.ID, name = d.DEPName })
                .ToList();
            return Json(new { items = list });
        }

        // GET /PmsProduceOrder/GetGroups?workShopId=66  -> groups (children) directly under the workshop
        [HttpGet]
        public JsonResult GetGroups(int workShopId)
        {
            var list = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.ParentID == workShopId)
                .OrderBy(d => d.ID)
                .Select(d => new { id = d.ID, name = d.DEPName })
                .ToList();
            return Json(new { items = list });
        }

        // GET /PmsProduceOrder/GetWorkers?groupOrWorkShopId=70  -> workers in the dept subtree
        [HttpGet]
        public JsonResult GetWorkers(int groupOrWorkShopId)
        {
            var all = _db.t_ERP_Department.ToList();
            if (!all.Any(d => d.ID == groupOrWorkShopId))
                return Json(new { items = new List<object>() });

            var scope = CollectDescendantDeptIds(all, groupOrWorkShopId);
            var list = _db.t_PMS_Worker
                .Where(w => w.Status == true && scope.Contains(w.DEPID))
                .OrderBy(w => w.ID)
                .Select(w => new { id = w.ID, name = w.FName })
                .ToList();
            return Json(new { items = list });
        }
    }
}
