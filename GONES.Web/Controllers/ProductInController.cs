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
    /// Production in-stock bill (SCRKD) - mirrors the legacy WinForms
    /// Stock.FrmProductionInStockManager (t_ERP_Menu.ID=1291). Header
    /// t_PMS_StockBill (FBillType=1) + entries t_PMS_StockBillEntry.
    /// Rows are IMPORTED from produce orders (t_PMS_ProduceOrderProductEntry,
    /// FStockStatus != 1): product output rows only, qty prefilled with the
    /// plan number. The server re-derives item / BOM / step / plan fields from
    /// the produce-order rows (client is trusted only for qty and note).
    /// Audit reuses StockService.ApplyInStock: batch numbers follow the SAME
    /// rule as the purchase in-stock bill (yyyyMMdd + class code + daily seq),
    /// one batch master row per entry; then the produce-order product rows are
    /// stamped FStockStatus=1 (imported) so they cannot be imported twice.
    /// Un-audit reverts the stock (Refuses when a batch was consumed) and
    /// clears FStockStatus back to 0.
    /// Header cascade: department (IsDEP=0) -> workshop (IsDEP=2) -> stocker.
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // standard MVC form redisplay, skip Furion 400 short-circuit
    public class ProductInController : Controller, IMenuGuarded
    {
        private const int BillType = 1;
        private const string BillNoPrefix = "SCRKD";
        private const string BillTypeExName = "\u751f\u4ea7\u5165\u5e93";   // 生产入库
        /// <summary>W1 越权提示：读可以少看，动必须整套归你（编辑/审核/反审核/删除共用一份文案）。</summary>
        private const string OutOfScopeMsg = "\u8be5\u5355\u636e\u5305\u542b\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u4ed3\u5e93\u7684\u660e\u7ec6\uff0c\u7981\u6b62\u64cd\u4f5c\uff01";   // 该单据包含不属于您管理仓库的明细，禁止操作！

        private readonly GonesPgDbContext _db;
        private readonly StockService _stock;
        private readonly MenuService _menu;
        private readonly DepotScope _depot;
        public ProductInController(GonesPgDbContext db, StockService stock, MenuService menu, DepotScope depot)
        {
            _db = db;
            _stock = stock;
            _menu = menu;
            _depot = depot;
        }

        private bool IsAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        /// <summary>
        /// 入口闸门：admin 旁路恒 true（行为逐字节不变），非 admin 须由其角色的
        /// t_ERP_RoleMenu 授权覆盖本模块（1291）。行级判定按产成品的 item.WarehouseId（仓库维度）。
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
            // 数据闸门①-b（合计口径）：合计必须与"可见行"同口径，否则把别仓的数量/金额算进来=越权泄露。
            var sumQ = _db.t_PMS_StockBillEntry.Where(e => matchedIds.Contains(e.FInterID));
            if (_depot.IsRestricted) sumQ = _depot.FilterEntries(sumQ);
            var sums = sumQ
                .GroupBy(e => 1)
                .Select(g => new { Qty = g.Sum(x => x.FNum ?? 0m), Amount = g.Sum(x => x.FAmount ?? 0m) })
                .FirstOrDefault();
            ViewBag.TotalQty = sums?.Qty ?? 0m;
            ViewBag.TotalAmount = sums?.Amount ?? 0m;

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

            // step names: entry FStepID -> t_PMS_Step (key = FItemID) -> FName
            var stepIds = entries.Select(e => e.FStepID).Where(x => x > 0).Distinct().ToList();
            var stepMap = _db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID)).ToDictionary(s => s.FItemID, s => s.FName);

            string DeptName(int? id) => (id != null && depts.ContainsKey(id.Value)) ? depts[id.Value] : "";
            string WorkerName(int? id) => (id != null && workers.ContainsKey(id.Value)) ? workers[id.Value] : "";
            string StepName(int id) => (id > 0 && stepMap.ContainsKey(id)) ? stepMap[id] : "";

            // 数据闸门③-b（按钮灰化）：与服务端 W1 硬拦同源。
            var lockedByDepot = _depot.BillsNotFullyInScope(headerIds);

            var rows = new List<ProductInListRow>();
            foreach (var h in headers)
            {
                var bills = entriesByBill.ContainsKey(h.FInterID) ? entriesByBill[h.FInterID] : new List<t_PMS_StockBillEntry>();
                var first = true;
                if (bills.Count == 0)
                {
                    rows.Add(new ProductInListRow
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
                    // 单价：取明细落库价。**已审行的价在审核时点冻结（快照哲学，用户裁定 2026-09-11），
                    // 不得随主档改价漂移**；`?? it?.FactoryPrice` 仅为改造前历史空值行准备的兼容垫片，
                    // 已审行因审核盖章恒非空而永不触发该分支（由 tests/probe_price_freeze.js 断言守卫）。
                    // 金额按 数量×单价 现算，保证「单价×数量=金额」在页面上自洽。
                    decimal? price = e.FPrice ?? it?.FactoryPrice;
                    decimal? amount = price.HasValue ? (e.FNum ?? 0m) * price.Value : (decimal?)null;
                    rows.Add(new ProductInListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = first, DeptName = DeptName(h.FDeptID), WorkShopName = DeptName(h.FWorkShopID),
                        ManagerName = WorkerName(h.FManagerID),
                        EntryId = e.FEntryID, ProduceNo = e.FProduceNo ?? "", StepName = StepName(e.FStepID),
                        BatchNo = e.FBatchNo,
                        Cpbm = it?.ItemCode ?? "", Cplb = it?.ProductCategory ?? "", Cpjc = it?.ItemShortName ?? "",
                        Cpgg = it?.ItemSpec ?? "", UnitName = it?.BaseUnit ?? "",
                        PlanNum = e.FPlanNum, Qty = e.FNum, Price = price, Amount = amount, Note = e.FNote,
                        CanModify = !lockedByDepot.Contains(h.FInterID),
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

        // ------------------------------------------------------------ produce-order import modal

        /// <summary>
        /// BillUse ids (领料申请单 generated with the produce order) that were ACTUALLY issued:
        /// an audited warehouse pick bill (t_PMS_StockBill FBillType=8, FState=1) exists whose
        /// entries reference the application's entries via FBillUseEntryID. An order whose
        /// application was never picked must not be stocked in.
        /// </summary>
        private HashSet<int> LoadPickedBillUseIds(List<int> billUseIds)
        {
            var picked = new HashSet<int>();
            if (billUseIds == null || billUseIds.Count == 0) return picked;

            var useEntryIds = _db.t_PMS_BillUseEntry
                .Where(e => billUseIds.Contains(e.FInterID))
                .Select(e => e.FEntryID).ToList();
            if (useEntryIds.Count == 0) return picked;

            var pickBillIds = _db.t_PMS_StockBillEntry
                .Where(e => e.FBillUseEntryID != null && useEntryIds.Contains(e.FBillUseEntryID.Value))
                .Select(e => e.FInterID).Distinct().ToList();
            if (pickBillIds.Count == 0) return picked;

            var hitUseEntryIds = _db.t_PMS_StockBill
                .Where(b => b.FBillType == 8 && b.FState == true && pickBillIds.Contains(b.FInterID))
                .Join(_db.t_PMS_StockBillEntry, b => b.FInterID, e => e.FInterID,
                    (b, e) => e.FBillUseEntryID)
                .Where(id => id != null).Select(id => id.Value).Distinct().ToList();

            var useIdByEntryId = _db.t_PMS_BillUseEntry
                .Where(e => hitUseEntryIds.Contains(e.FEntryID))
                .ToDictionary(e => e.FEntryID, e => e.FInterID);
            foreach (var id in hitUseEntryIds)
            {
                if (useIdByEntryId.TryGetValue(id, out var useId)) picked.Add(useId);
            }
            return picked;
        }

        // GET /ProductIn/GetProduceOrders?billNo=SCD0007012
        // Produce-order product rows (FStockStatus != 1) that can still be imported.
        [HttpGet]
        // excludeInterId：编辑已有入库单时由前端传入本单 ID，使弹窗显示「扣掉别人占用后」
        // 的剩余量，而不把本单自己的量算成已占用（否则重新导入时可用量会凭空变少）。
        public IActionResult GetProduceOrders(string billNo, string dateFrom, string dateTo, int? excludeInterId = null)
        {

            billNo = billNo?.Trim();
            var hq = _db.t_PMS_ProduceOrder.AsQueryable();
            if (!string.IsNullOrEmpty(billNo)) hq = hq.Where(h => h.FBillNo.Contains(billNo));
            DateTime dFrom, dTo;
            if (DateTime.TryParse(dateFrom, out dFrom)) hq = hq.Where(h => h.FDate >= dFrom.Date);
            if (DateTime.TryParse(dateTo, out dTo)) hq = hq.Where(h => h.FDate < dTo.Date.AddDays(1));
            var headers = hq.OrderByDescending(h => h.FInterID).Take(200).ToList();
            var headerIds = headers.Select(h => h.FInterID).ToList();
            var headerMap = headers.ToDictionary(h => h.FInterID);

            // Partial stock-in support: a produce line can be re-imported until its cumulative
            // stock-in qty reaches the plan. Derive the already-stocked amount per line
            // (keyed by produce order + BOM + item) and keep only lines with a positive remainder.
            // 用「已占用额度」（含草稿）：草稿也要占额度，否则两张草稿可各导入同一生产单行，
            // 审核后合计超出计划量。
            var stocked = LoadCumulativeStocked(headerIds, excludeInterId, includeDraft: true);

            var products = _db.t_PMS_ProduceOrderProductEntry
                .Where(e => headerIds.Contains(e.FInterID))
                .OrderBy(e => e.FInterID).ThenBy(e => e.FEntryID)
                .ToList()
                .Where(pe =>
                {
                    decimal plan = pe.FPlanNum ?? 0m;
                    decimal done = stocked.TryGetValue(Key(pe.FInterID, pe.FBomId, pe.FItemID ?? 0), out var c) ? c : 0m;
                    return plan - done > 0.0001m;
                })
                .ToList();

            // BomId -> step name for these produce orders (StepEntry.FStepID -> Step.FItemID)
            var stepRows = _db.t_PMS_ProduceOrderStepEntry
                .Where(e => headerIds.Contains(e.FInterID) && e.FBomId != null && e.FStepID != null)
                .ToList();
            var stepIds = stepRows.Select(e => e.FStepID.Value).Distinct().ToList();
            var stepMap = _db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID)).ToDictionary(s => s.FItemID, s => s.FName);
            var stepNameByBom = stepRows
                .GroupBy(e => e.FBomId.Value)
                .ToDictionary(g => g.Key, g => stepMap.ContainsKey(g.First().FStepID.Value) ? stepMap[g.First().FStepID.Value] : "");

            var itemIds = products.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            // pick gate: rows whose picking application has not been issued yet (no audited FB=8
            // pick bill consumed it) stay visible but disabled in the modal
            var useIds = products.Where(p => p.FBillUseFInterID.HasValue)
                .Select(p => p.FBillUseFInterID.Value).Distinct().ToList();
            var pickedUseIds = LoadPickedBillUseIds(useIds);

            var rows = new List<ProductInPickRow>();
            foreach (var pe in products)
            {
                if (!pe.FItemID.HasValue || !headerMap.ContainsKey(pe.FInterID)) continue;
                var h = headerMap[pe.FInterID];
                var it = items.ContainsKey(pe.FItemID.Value) ? items[pe.FItemID.Value] : null;
                // 数据闸门②（选择器收窄）：只展示本仓库的产成品行。
                if (!_depot.Allows(it?.WarehouseId)) continue;
                string stepName = pe.FBomId != 0 && stepNameByBom.ContainsKey(pe.FBomId) ? stepNameByBom[pe.FBomId] : "";
                decimal plan = pe.FPlanNum ?? 0m;
                decimal done = stocked.TryGetValue(Key(pe.FInterID, pe.FBomId, pe.FItemID ?? 0), out var c) ? c : 0m;
                decimal rest = plan - done;
                if (rest < 0m) rest = 0m;
                rows.Add(new ProductInPickRow
                {
                    ProduceId = pe.FInterID,
                    ProduceNo = h.FBillNo ?? "",
                    PickState = pe.FBillUseFInterID.HasValue && pickedUseIds.Contains(pe.FBillUseFInterID.Value),
                    ProduceDate = h.FDate,
                    ProductEntryId = pe.FEntryID,
                    BomId = pe.FBomId,
                    StepName = stepName,
                    ItemId = pe.FItemID.Value,
                    Cpbm = it?.ItemCode ?? "",
                    Cpjc = it?.ItemShortName ?? "",
                    Cpgg = it?.ItemSpec ?? "",
                    Cplb = it?.ProductCategory ?? "",
                    Unit = it?.BaseUnit ?? "",
                    PlanNum = pe.FPlanNum,
                    Ccdj = it?.FactoryPrice,          // 出厂价：导入后即时显示单价/金额
                    RestNum = rest,
                });
            }
            return Json(rows);
        }

        // ------------------------------------------------------------ pick-bill import modal (non-packaging steps)

        // GET /ProductIn/GetPickBills?billNo=SCLLD&dateFrom=&dateTo=
        // Warehouse picking bills (FBillType=8, already audited) whose step is NOT a packaging
        // process. For each bill we expose one row per BOM -> its output product (BOM FIsProduct=1
        // row), with the produced qty (pick qty / BOM base, multi-input takes the representative
        // row) and the remaining stockable qty (produced - cumulative in-stock from this
        // pick bill). Fully stocked bills are excluded so they cannot be re-imported.
        // excludeInterId：编辑已有入库单时由前端传入本单 ID，排除本单自身的占用。
        [HttpGet]
        public IActionResult GetPickBills(string billNo, string dateFrom, string dateTo, int? excludeInterId = null)
        {

            billNo = billNo?.Trim();
            var hq = _db.t_PMS_StockBill.Where(h => h.FBillType == 8 && h.FState == true);
            // 数据闸门②-a（来源单选择器收窄）：只列"至少有一行明细属于本仓库"的领料单。
            if (_depot.IsRestricted) hq = _depot.FilterBills(hq);
            if (!string.IsNullOrEmpty(billNo)) hq = hq.Where(h => h.FBillNo.Contains(billNo));
            DateTime dFrom, dTo;
            if (DateTime.TryParse(dateFrom, out dFrom)) hq = hq.Where(h => h.FDate >= dFrom.Date);
            if (DateTime.TryParse(dateTo, out dTo)) hq = hq.Where(h => h.FDate < dTo.Date.AddDays(1));
            var headers = hq.OrderByDescending(h => h.FInterID).Take(200).ToList();
            var headerIds = headers.Select(h => h.FInterID).ToList();

            var entries = _db.t_PMS_StockBillEntry
                .Where(e => headerIds.Contains(e.FInterID)).ToList();
            var entryByBill = entries.GroupBy(e => e.FInterID).ToDictionary(g => g.Key, g => g.ToList());

            // step name cache + packaging-step detection
            var stepIdsAll = entries.Select(e => e.FStepID).Where(x => x > 0).Distinct().ToList();
            var stepNames = _db.t_PMS_Step.Where(s => stepIdsAll.Contains(s.FItemID))
                .ToDictionary(s => s.FItemID, s => s.FName ?? "");
            bool IsPack(int id) => id > 0 && stepNames.ContainsKey(id) && stepNames[id].Contains("\u5305\u88c5");   // 包装

            var rows = new List<ProductInPickRow>();
            foreach (var h in headers)
            {
                if (!entryByBill.ContainsKey(h.FInterID)) continue;
                var bills = entryByBill[h.FInterID];
                if (bills.Count == 0) continue;
                if (bills.Any(e => IsPack(e.FStepID))) continue;   // exclude packaging-step pick bills

                foreach (var g in bills.Where(e => e.FBomId > 0).GroupBy(e => e.FBomId))
                {
                    int bomId = g.Key;
                    var outItems = BomOutputItemIds(bomId);   // 待入库产成品 (FIsProduct=1)
                    if (outItems.Count == 0) continue;

                    // produced qty per output product (same for every output of this BOM)
                    decimal producedFull = 0m;
                    int repEntryId = 0;
                    foreach (var e in g)
                    {
                        decimal b = BomInputBase(bomId, e.FItemID ?? 0);
                        decimal prod = b > 0.0001m ? (e.FNum ?? 0m) / b : (e.FNum ?? 0m);
                        if (prod > producedFull) { producedFull = prod; repEntryId = e.FEntryID; }
                    }
                    if (producedFull <= 0.0001m || repEntryId == 0) continue;

                    // 出库产品 = 该领料单在本 BOM 下实际出库的原材料（BOM 输入行）
                    var outStockIds = g.Select(e => e.FItemID ?? 0).Where(id => id > 0).Distinct().ToList();
                    int repStockItem = outStockIds.FirstOrDefault();
                    decimal outQty = g.Sum(e => e.FNum ?? 0m);

                    string stepName = g.Select(x => x.FStepID).Where(x => x > 0)
                                       .Select(x => stepNames.ContainsKey(x) ? stepNames[x] : "")
                                       .FirstOrDefault() ?? "";

                    // 已入库累计（按产成品汇总），用于"整单是否全满"判定
                    // 已占用额度（含草稿）：草稿也要占额度，防止两张草稿各导入同一领料单。
                    var cumByItem = LoadPickCumulative(h.FInterID, excludeInterId, includeDraft: true);
                    decimal cumAll = 0m;
                    foreach (var oi in outItems) cumAll += cumByItem.TryGetValue(oi, out var c) ? c : 0m;
                    decimal restAll = producedFull * outItems.Count - cumAll;
                    if (restAll <= 0.0001m) continue;   // 整单已全满，不再可选
                    if (restAll < 0m) restAll = 0m;

                    var row = new ProductInPickRow
                    {
                        Source = 1,
                        PickBillId = h.FInterID,
                        PickBillNo = h.FBillNo ?? "",
                        PickEntryId = repEntryId,
                        BomId = bomId,
                        StepId = 0,
                        StepName = stepName,
                        ItemId = repStockItem,   // 代表出库物料（原材料），仅用于弹窗展示
                        Cpbm = "", Cpjc = "", Cpgg = "", Cplb = "", Unit = "",
                        PlanNum = producedFull,
                        RestNum = restAll,
                    };
                    row.OutQty = outQty;
                    row.Outputs = new List<ProductInPickOutput>();
                    foreach (var oi in outItems)
                    {
                        decimal oCum = cumByItem.TryGetValue(oi, out var oc) ? oc : 0m;
                        decimal oRest = producedFull - oCum;
                        if (oRest < 0m) oRest = 0m;
                        row.Outputs.Add(new ProductInPickOutput
                        {
                            ItemId = oi,
                            Cpbm = "", Cpjc = "", Cpgg = "", Cplb = "", Unit = "",
                            PlanNum = producedFull,
                            RestNum = oRest,
                        });
                    }
                    rows.Add(row);
                }
            }

            // batch the item master joins for display cells (out-stock row + every output product)
            var itemIds = rows.Select(r => r.ItemId)
                              .Concat(rows.SelectMany(r => r.Outputs.Select(o => o.ItemId)))
                              .Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);
            foreach (var r in rows)
            {
                if (r.ItemId > 0 && items.ContainsKey(r.ItemId))
                {
                    var it = items[r.ItemId];
                    r.Cpbm = it.ItemCode ?? "";
                    r.Cpjc = it.ItemShortName ?? "";   // 出库原材料名称
                    r.Cpgg = it.ItemSpec ?? "";
                    r.Cplb = it.ProductCategory ?? "";
                    r.Unit = it.BaseUnit ?? "";
                }
                foreach (var o in r.Outputs)
                {
                    if (o.ItemId > 0 && items.ContainsKey(o.ItemId))
                    {
                        var it = items[o.ItemId];
                        o.Cpbm = it.ItemCode ?? "";
                        o.Cpjc = it.ItemShortName ?? "";   // 待入库产成品名称
                        o.Cpgg = it.ItemSpec ?? "";
                        o.Cplb = it.ProductCategory ?? "";
                        o.Unit = it.BaseUnit ?? "";
                        o.Ccdj = it.FactoryPrice;         // 出厂价：导入后即时显示单价/金额
                    }
                }
            }
            return Json(rows);
        }

        // ------------------------------------------------------------ create

        [HttpGet]
        public IActionResult Create()
        {
            BindFormExtras();
            var vm = new ProductInEditViewModel
            {
                BillNo = NextBillNo(),
                Date = DateTime.Today,
                Rows = new List<ProductInRowInput>(),
            };
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(ProductInEditViewModel vm)
        {

            var rows = DeriveServerFields(vm);
            if (!ValidateBill(vm, rows))
            {
                BindFormExtras(vm.DeptId, vm.WorkShopId);
                BindRowDisplay(vm, rows);
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
                    FGroupID = null,
                    FManagerID = vm.ManagerId,
                    FInspectors = vm.Inspectors ?? "",
                    FRemark = vm.Remark ?? "",
                    FCreaterID = CurrentUserId,
                    FROB = 1,                           // blue bill (in-stock)
                    FState = false,
                };
                SaveBill(vm, header, rows);
            }

            TempData["Success"] = "\u751f\u4ea7\u5165\u5e93\u5355\u4fdd\u5b58\u6210\u529f\u3002";   // 生产入库单保存成功。
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

        // GET /ProductIn/Details/5 —— 已审单只读查看（与 Edit 同一套装载逻辑）
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
            var entryQ = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id);
            if (_depot.IsRestricted) entryQ = _depot.FilterEntries(entryQ);
            var entries = entryQ.OrderBy(e => e.FEntryID).ToList();
            var itemIds = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);
            var stepIds = entries.Select(e => e.FStepID).Where(x => x > 0).Distinct().ToList();
            var stepMap = _db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID)).ToDictionary(s => s.FItemID, s => s.FName);

            // Re-resolve the source produce-order product entry id on edit so the POST path
            // can use the exact (produce, entry) match instead of the (produce, bom, item)
            // triple fallback. t_PMS_StockBillEntry has no FProduceEntryID column, so this is a
            // best-effort restore: unimported rows first, then smallest FEntryID. It stabilizes
            // the multi-row same-triple boundary case.
            var prodIds = entries.Where(e => e.FProduceID.HasValue)
                .Select(e => e.FProduceID.Value).Distinct().ToList();
            var prodRowMap = _db.t_PMS_ProduceOrderProductEntry
                .Where(x => prodIds.Contains(x.FInterID))
                .ToList()
                .GroupBy(x => new { FInterID = x.FInterID, Bom = x.FBomId, Item = x.FItemID ?? 0 })
                .ToDictionary(g => g.Key, g => g
                    .OrderBy(x => x.FStockStatus ?? 0).ThenBy(x => x.FEntryID).ToList());

            var rows = new List<ProductInRowInput>();
            foreach (var e in entries)
            {
                if (e.FItemID == null || e.FItemID.Value == 0) continue;
                t_ERP_ITEM it = items.ContainsKey(e.FItemID.Value) ? items[e.FItemID.Value] : null;
                // 单价：明细落库价（已审行冻结于审核时点）；`?? it?.FactoryPrice` 仅历史空值行兼容垫片。
                // 金额 = 数量 × 单价。不变量见 tests/probe_price_freeze.js。
                decimal? rowPrice = e.FPrice ?? it?.FactoryPrice;
                decimal? rowAmount = rowPrice.HasValue ? (e.FNum ?? 0m) * rowPrice.Value : (decimal?)null;

                // pick-bill sourced row (non-packaging-step in-stock)
                if (e.FPickEntryID.HasValue && e.FPickEntryID.Value > 0)
                {
                    rows.Add(new ProductInRowInput
                    {
                        Source = 1,
                        PickBillId = e.FPickBillID ?? 0,
                        PickEntryId = e.FPickEntryID.Value,
                        PickBillNo = e.FProduceNo ?? "",
                        BomId = e.FBomId,
                        StepId = e.FStepID,
                        ItemId = e.FItemID.Value,
                        Cpbm = it?.ItemCode ?? "",
                        ProductName = it?.ItemShortName ?? "",
                        Spec = it?.ItemSpec ?? "",
                        CategoryName = it?.ProductCategory ?? "",
                        Unit = it?.BaseUnit ?? "",
                        StepName = (e.FStepID > 0 && stepMap.ContainsKey(e.FStepID)) ? stepMap[e.FStepID] : "",
                        PlanNum = e.FPlanNum,
                        InNum = e.FNum,
                        Price = rowPrice,
                        Amount = rowAmount,
                        Note = e.FNote,
                    });
                    continue;
                }

                int prodEntryId = 0;
                if (e.FProduceID.HasValue && e.FBomId > 0 && e.FItemID.HasValue)
                {
                    var pkey = new { FInterID = e.FProduceID.Value, Bom = e.FBomId, Item = e.FItemID.Value };
                    if (prodRowMap.TryGetValue(pkey, out var cand) && cand.Count > 0)
                        prodEntryId = cand[0].FEntryID;
                }
                rows.Add(new ProductInRowInput
                {
                    ProductEntryId = prodEntryId,       // re-resolved on edit (best-effort)
                    ProduceId = e.FProduceID ?? 0,
                    ProduceNo = e.FProduceNo ?? "",
                    BomId = e.FBomId,
                    ItemId = e.FItemID.Value,
                    Cpbm = it?.ItemCode ?? "",
                    ProductName = it?.ItemShortName ?? "",
                    Spec = it?.ItemSpec ?? "",
                    CategoryName = it?.ProductCategory ?? "",
                    Unit = it?.BaseUnit ?? "",
                    StepName = (e.FStepID > 0 && stepMap.ContainsKey(e.FStepID)) ? stepMap[e.FStepID] : "",
                    PlanNum = e.FPlanNum,
                    InNum = e.FNum,
                    Price = rowPrice,
                    Amount = rowAmount,
                    Note = e.FNote,
                });
            }

            var vm = new ProductInEditViewModel
            {
                InterId = header.FInterID,
                BillNo = header.FBillNo,
                Date = header.FDate ?? DateTime.Today,
                DeptId = header.FDeptID,
                WorkShopId = header.FWorkShopID,
                ManagerId = header.FManagerID,
                Inspectors = header.FInspectors,
                Remark = header.FRemark,
                State = header.FState ?? false,
                Rows = rows,
            };
            BindFormExtras(header.FDeptID, header.FWorkShopID);
            BindRowDisplay(vm, rows);
            ViewBag.ReadOnly = readOnly;
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, ProductInEditViewModel vm)
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

            var rows = DeriveServerFields(vm);
            if (!ValidateBill(vm, rows))
            {
                BindFormExtras(vm.DeptId, vm.WorkShopId);
                BindRowDisplay(vm, rows);
                return View("Form", vm);
            }

            header.FDate = vm.Date;
            header.FDeptID = vm.DeptId;
            header.FWorkShopID = vm.WorkShopId;
            header.FManagerID = vm.ManagerId;
            header.FInspectors = vm.Inspectors ?? "";
            header.FRemark = vm.Remark ?? "";
            header.FModifyID = CurrentUserId;
            header.FModifyTime = DateTime.Now;
            SaveBill(vm, header, rows);

            TempData["Success"] = "\u751f\u4ea7\u5165\u5e93\u5355\u4fee\u6539\u6210\u529f\u3002";   // 生产入库单修改成功。
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

            // tri_deleteStockBillEntry trigger dropped (2026-09-09): entries + header go in
            // a single SaveChanges inside the transaction.
            using (var tx = _db.Database.BeginTransaction())
            {
                var entries = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id).ToList();
                _db.t_PMS_StockBillEntry.RemoveRange(entries);
                _db.t_PMS_StockBill.Remove(header);
                _db.SaveChanges();
                tx.Commit();
            }

            TempData["Success"] = "\u751f\u4ea7\u5165\u5e93\u5355\u5220\u9664\u6210\u529f\u3002";   // 生产入库单删除成功。
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

            // Same batch-number rule as the purchase in-stock bill: yyyyMMdd + class
            // code + daily sequence, one batch master row per entry, caches re-derived.
            // Transaction: the batch stamps, the produce-order status write-back and the cache
            // re-derivation must land together -- otherwise the order looks imported while the
            // stock was never raised (or the other way round).
            using (var tx = _stock.BeginStockTransaction(_db))
            {
                // 并发守卫（同 StockCheckController.Audit）：上面的 FState 检查在事务外完成，
                // 两个「审核」请求可能都已通过它；此刻重读 DB（投影查询绕过 EF 标识缓存）
                // 并在 advisory lock 保护下原子判定，防止批次被生成两次、留下孤儿批次主数据。
                var freshState = _db.t_PMS_StockBill.Where(h => h.FInterID == id && h.FBillType == BillType)
                                                    .Select(h => h.FState).FirstOrDefault();
                if ((freshState ?? false))
                {
                    TempData["Error"] = "\u5355\u636e\u5df2\u5ba1\u6838\uff0c\u65e0\u9700\u91cd\u590d\u5ba1\u6838\uff01";
                    return RedirectToAction(nameof(Index));
                }
                if (!_stock.ApplyInStock(_db, header, entries, User.Identity.Name, out string error))
                {
                    TempData["Error"] = error;
                    return RedirectToAction(nameof(Index));       // tx disposed -> rolled back (G1: ccdj 缺失拒审)
                }
                // ApplyInStock 内部已 SaveChanges + header.FState=true；下方 _db.SaveChanges 仍需保留，
                // StampProduceOrderStatus 改动了 ProduceOrderProductEntry.FStockStatus / ProduceOrder.FOrderStatus / GoodsApplyEntry.FFetchNum，
                // 自身不 SaveChanges。
                StampProduceOrderStatus(entries, true, header.FDate, header.FInterID);   // stocked qty + apply fetch num + order status
                _db.SaveChanges();
                tx.Commit();
            }

            TempData["Success"] = "\u5ba1\u6838\u6210\u529f\uff0c\u5df2\u751f\u6210\u4ea7\u6210\u54c1\u6279\u6b21\u5e76\u66f4\u65b0\u5e93\u5b58\u3002";   // 审核成功，已生成产成品批次并更新库存。
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

            // Refuses when a batch was already consumed by a later bill.
            string revertError;
            using (var tx = _stock.BeginStockTransaction(_db))
            {
                if (!_stock.RevertInStock(_db, header, entries, out revertError))
                {
                    TempData["Error"] = revertError;
                    return RedirectToAction(nameof(Index));       // tx disposed -> rolled back
                }

                StampProduceOrderStatus(entries, false, null, header.FInterID);   // recompute stocked qty + order status
                // FState 已由 RevertInStock 内部置 false 并 SaveChanges（见 StockService.RevertInStock:953）；
                // 此处只需保存 StampProduceOrderStatus 对生产单/要货单的改动。
                _db.SaveChanges();
                tx.Commit();
            }

            TempData["Success"] = "\u53cd\u5ba1\u6838\u6210\u529f\uff0c\u6279\u6b21\u4e0e\u5e93\u5b58\u5df2\u56de\u6eda\u3002";   // 反审核成功，批次与库存已回滚。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ lookups

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

        /// <summary>
        /// Re-derive every row from the produce order: the client is trusted only for
        /// InNum and Note. Rows whose produce-order product entry is missing, already
        /// imported (FStockStatus=1) or not yet picked (no audited pick bill consumed the
        /// line's picking application) are rejected with a model error.
        /// </summary>
        private List<ProductInRowInput> DeriveServerFields(ProductInEditViewModel vm)
        {
            var rows = (vm.Rows ?? new List<ProductInRowInput>())
                .Where(r => r.ItemId > 0 || r.ProductEntryId > 0).ToList();

            var produceIds = rows.Select(r => r.ProduceId).Where(x => x > 0).Distinct().ToList();
            var headers = _db.t_PMS_ProduceOrder.Where(h => produceIds.Contains(h.FInterID))
                .ToDictionary(h => h.FInterID, h => h.FBillNo ?? "");

            var productKeys = rows.Select(r => new { r.ProduceId, r.ProductEntryId }).Distinct().ToList();
            var produceInterIds = productKeys.Select(k => k.ProduceId).Distinct().ToList();
            var productRows = _db.t_PMS_ProduceOrderProductEntry
                .Where(e => produceInterIds.Contains(e.FInterID)).ToList();

            // Cumulative audited stock-in per produce line, used to block re-importing a line
            // that is already fully stocked (while still allowing partial lines to be topped up).
            // 已占用额度（含草稿），防止两张草稿各导入同一生产单行造成重复入库。
            // 编辑本单时排除自身，否则自己的量会被算成已用、导致本单无法保存。
            var stocked = LoadCumulativeStocked(
                produceInterIds, vm.InterId > 0 ? vm.InterId : (int?)null, includeDraft: true);

            // BomId -> step name across the involved produce orders
            var stepRows = _db.t_PMS_ProduceOrderStepEntry
                .Where(e => produceInterIds.Contains(e.FInterID) && e.FBomId != null && e.FStepID != null).ToList();
            var stepIds = stepRows.Select(e => e.FStepID.Value).Distinct().ToList();
            var stepMap = _db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID)).ToDictionary(s => s.FItemID, s => s.FName);
            var stepNameByBom = stepRows
                .GroupBy(e => e.FBomId.Value)
                .ToDictionary(g => g.Key, g => stepMap.ContainsKey(g.First().FStepID.Value) ? stepMap[g.First().FStepID.Value] : "");

            var consumed = new HashSet<int>();   // row-level dedup: one produce entry per bill row

            // Pick gate: a produce row can only be stocked in after its picking application
            // (t_PMS_BillUse, linked by the product entry) was actually issued — i.e. consumed
            // by an AUDITED warehouse pick bill (FB=8). Computed once for all produce rows.
            var useIds = productRows.Where(e => e.FBillUseFInterID.HasValue)
                .Select(e => e.FBillUseFInterID.Value).Distinct().ToList();
            var pickedUseIds = LoadPickedBillUseIds(useIds);

            // Pre-load pick-source entries. EF6 cannot translate an .Any() over an in-memory
            // anonymous-type list, so we materialize by the primitive PickBillId list first
            // (translatable) and then do the exact (bill, entry) pair match in LINQ-to-Objects.
            var pickKeyPairs = rows.Where(r => r.Source == 1 && r.PickEntryId > 0)
                .Select(r => new { r.PickBillId, r.PickEntryId }).Distinct().ToList();
            var pickBillIds = pickKeyPairs.Select(p => p.PickBillId).Distinct().ToList();
            var pickEntries = pickBillIds.Count > 0
                ? _db.t_PMS_StockBillEntry.Where(e => pickBillIds.Contains(e.FInterID)).ToList()
                    .Where(e => pickKeyPairs.Any(p => e.FInterID == p.PickBillId && e.FEntryID == p.PickEntryId))
                    .ToList()
                : new List<t_PMS_StockBillEntry>();
            var pickStepIds = pickEntries.Select(e => e.FStepID).Where(x => x > 0).Distinct().ToList();
            var stepNames = _db.t_PMS_Step.Where(s => pickStepIds.Contains(s.FItemID))
                .ToDictionary(s => s.FItemID, s => s.FName ?? "");

            foreach (var r in rows)
            {
                // ----- source: warehouse pick bill (non-packaging-step in-stock) -----
                if (r.Source == 1 && r.PickEntryId > 0)
                {
                    var pickEntry = pickEntries.FirstOrDefault(e => e.FInterID == r.PickBillId && e.FEntryID == r.PickEntryId);
                    var pickHeader = pickEntry == null ? null
                        : _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == pickEntry.FInterID && h.FBillType == 8);
                    if (pickEntry == null || pickHeader == null || !(pickHeader.FState ?? false))
                    {
                        r.ItemId = 0; continue;   // invalid / un-audited source
                    }
                    // A BOM may declare multiple output products; the user-selected ItemId takes
                    // precedence (GetPickBills emits one import row per output). Fall back to the
                    // first output only when the row carries no ItemId (manual legacy rows).
                    var outItems = BomOutputItemIds(pickEntry.FBomId);
                    if (outItems.Count == 0) { r.ItemId = 0; continue; }
                    int outItem = (r.ItemId > 0 && outItems.Contains(r.ItemId))
                        ? r.ItemId : outItems[0];
                    decimal b = BomInputBase(pickEntry.FBomId, pickEntry.FItemID ?? 0);
                    decimal producedFull = b > 0.0001m ? (pickEntry.FNum ?? 0m) / b : (pickEntry.FNum ?? 0m);
                    // 已占用额度（含草稿）；编辑本单时排除自身。
                    decimal cum = LoadPickCumulative(
                        r.PickBillId, vm.InterId > 0 ? vm.InterId : (int?)null, includeDraft: true)
                        .TryGetValue(outItem, out var cumQty) ? cumQty : 0m;
                    if (cum >= producedFull - 0.0001m) { r.ItemId = 0; continue; }   // already fully stocked in for THIS output
                    r.ItemId = outItem;
                    r.BomId = pickEntry.FBomId;
                    r.StepId = pickEntry.FStepID;
                    r.PlanNum = producedFull;
                    r.StepName = (pickEntry.FStepID > 0 && stepNames.ContainsKey(pickEntry.FStepID))
                        ? stepNames[pickEntry.FStepID] : "";
                    r.ProduceNo = pickHeader.FBillNo ?? "";
                    r.PickBillNo = pickHeader.FBillNo ?? "";
                    continue;
                }

                // ----- source: produce order (existing) -----
                // Create posts the source ProductEntry.FEntryID; Edit re-posts rows that no
                // longer carry it, so fall back to the (produce, bom, item) triple. The resolver
                // prefers unimported rows and the smallest FEntryID, and excludes already-consumed
                // entries, which stabilizes the multi-row boundary case.
                t_PMS_ProduceOrderProductEntry pe = r.ProductEntryId > 0
                    ? productRows.FirstOrDefault(x => x.FInterID == r.ProduceId
                        && x.FEntryID == r.ProductEntryId && x.FItemID.HasValue)
                    : ResolveProduceEntry(productRows, r.ProduceId, r.BomId, r.ItemId, consumed, false);
                if (pe == null)
                {
                    r.ItemId = 0;       // marks the row invalid
                    continue;
                }
                // Pick gate: this produce line's picking application must have been issued
                // (audited pick bill) before stock-in. Marks the row invalid; ValidateBill
                // reports it with a dedicated message.
                if (!pe.FBillUseFInterID.HasValue || !pickedUseIds.Contains(pe.FBillUseFInterID.Value))
                {
                    r.ItemId = 0;
                    r.Unpicked = true;
                    r.ProduceNo = headers.ContainsKey(r.ProduceId) ? headers[r.ProduceId] : "";
                    continue;
                }
                // Reject only lines that are already fully stocked (cumulative >= plan). Partial
                // lines (status 0) remain importable so the remainder can be stocked later.
                decimal done = stocked.TryGetValue(Key(pe.FInterID, pe.FBomId, pe.FItemID ?? 0), out var c) ? c : 0m;
                if (done >= (pe.FPlanNum ?? 0m) - 0.0001m)
                {
                    r.ItemId = 0;       // already fully stocked in
                    continue;
                }
                consumed.Add(pe.FEntryID);
                r.ItemId = pe.FItemID.Value;
                r.BomId = pe.FBomId;
                r.StepId = StepIdOf(r.ProduceId, r.BomId);
                r.PlanNum = pe.FPlanNum;
                r.StepName = (pe.FBomId != 0 && stepNameByBom.ContainsKey(pe.FBomId)) ? stepNameByBom[pe.FBomId] : "";
                r.ProduceNo = headers.ContainsKey(r.ProduceId) ? headers[r.ProduceId] : "";
            }
            return rows;
        }

        /// <summary>
        /// Best-effort resolve of a produce-order product entry from the (produce, bom, item)
        /// triple. Prefers unimported rows (status 0) unless preferImported is set, tie-broken by
        /// the smallest FEntryID for deterministic hits, and skips entries already mapped by
        /// another row of the same bill. Used by the edit path where the exact entry id is not
        /// persisted (t_PMS_StockBillEntry has no FProduceEntryID column).
        /// </summary>
        private static t_PMS_ProduceOrderProductEntry ResolveProduceEntry(
            List<t_PMS_ProduceOrderProductEntry> rows, int produceId, int bomId, int itemId,
            HashSet<int> consumed, bool preferImported)
        {
            return rows
                .Where(x => x.FInterID == produceId && x.FBomId == bomId
                    && x.FItemID == itemId && x.FItemID.HasValue && !consumed.Contains(x.FEntryID))
                .OrderBy(x => preferImported ? -(x.FStockStatus ?? 0) : (x.FStockStatus ?? 0))
                .ThenBy(x => x.FEntryID)
                .FirstOrDefault();
        }

        private bool ValidateBill(ProductInEditViewModel vm, List<ProductInRowInput> rows)
        {
            if (vm.Date == default || vm.Date.Year < 2000)
            {
                ModelState.AddModelError("Date", "\u8bf7\u586b\u5199\u6b63\u786e\u7684\u5355\u636e\u65e5\u671f\uff01");
                return false;
            }
            if (string.IsNullOrWhiteSpace(vm.Inspectors))
            {
                ModelState.AddModelError("Inspectors", "\u8bf7\u9009\u62e9\u68c0\u9a8c\u4eba\uff01");   // 请选择检验人！
                return false;
            }
            if (rows.Count == 0)
            {
                ModelState.AddModelError("Rows", "\u8bf7\u81f3\u5c11\u5bfc\u5165\u4e00\u4e2a\u4ea7\u6210\u54c1\uff01");   // 请至少导入一个产成品！
                return false;
            }
            if (rows.Any(r => r.ItemId <= 0 && !r.Unpicked))
            {
                ModelState.AddModelError("Rows", "\u5b58\u5728\u65e0\u6548\u6216\u5df2\u5165\u5e93\u7684\u660e\u7ec6\u884c\uff0c\u8bf7\u91cd\u65b0\u5bfc\u5165\u3002");   // 存在无效或已入库的明细行，请重新导入。
                return false;
            }
            // pick gate message (rows were already rejected in DeriveServerFields)
            var unpickedNos = rows.Where(r => r.Unpicked).Select(r => r.ProduceNo).Distinct().ToList();
            if (unpickedNos.Count > 0)
            {
                ModelState.AddModelError("Rows",
                    "\u751f\u4ea7\u5355 " + string.Join("\u3001", unpickedNos) +
                    " \u7684\u9886\u6599\u5355\u5c1a\u672a\u9886\u6599\uff0c\u4e0d\u80fd\u5165\u5e93\uff01");   // 生产单 X 的领料单尚未领料，不能入库！
                return false;
            }

            // 数据闸门③-a（写入）：导入行的产成品必须全部属于本仓库。此处行已通过
            // 无效/未领料校验（ItemId > 0），admin 旁路不进入此段。
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
            // duplicate source-row check: pick rows key on (pick bill, bom, item); produce rows use
            // the entry id when present, else the (produce, bom, item) identity
            var dupKeys = rows.Select(r => r.Source == 1
                ? "pk-" + r.PickBillId + "-" + r.BomId + "-" + r.ItemId
                : (r.ProductEntryId > 0
                    ? r.ProduceId + "-" + r.ProductEntryId + "-p"
                    : r.ProduceId + "-" + r.BomId + "-" + r.ItemId)).ToList();
            if (dupKeys.Count != dupKeys.Distinct().Count())
            {
                ModelState.AddModelError("Rows", "\u5165\u5e93\u660e\u7ec6\u4e2d\u5b58\u5728\u91cd\u590d\u7684\u884c\uff01");   // 入库明细中存在重复的行！
                return false;
            }
            // 来源行已占用额度（含草稿），用于把 InNum 卡在剩余可入库量内，
            // 防止两张草稿各导入同一来源行、审核后重复入库。
            // 必须排除本单自身：编辑时本单的量不该被算成已被占用，否则本单存不下。
            var selfId = vm.InterId > 0 ? vm.InterId : (int?)null;
            var prodIds = rows.Where(r => r.Source == 0 && r.ProduceId > 0).Select(r => r.ProduceId).Distinct().ToList();
            var prodCum = prodIds.Count > 0
                ? LoadCumulativeStocked(prodIds, selfId, includeDraft: true)
                : new Dictionary<(int, int, int), decimal>();
            var pickIds = rows.Where(r => r.Source == 1 && r.PickBillId > 0).Select(r => r.PickBillId).Distinct().ToList();
            var pickCum = new Dictionary<int, decimal>();
            foreach (var pid in pickIds)
            {
                var d = LoadPickCumulative(pid, selfId, includeDraft: true);
                foreach (var kv in d) pickCum[kv.Key] = (pickCum.ContainsKey(kv.Key) ? pickCum[kv.Key] : 0m) + kv.Value;
            }
            foreach (var r in rows)
            {
                var label = string.IsNullOrWhiteSpace(r.ProductName)
                    ? ("\u7b2c " + (rows.IndexOf(r) + 1) + " \u884c")          // 第 N 行
                    : r.ProductName;
                if ((r.InNum ?? 0) <= 0)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u5165\u5e93\u6570\u91cf\u5fc5\u987b\u5927\u4e8e 0\uff01");   // 【xx】入库数量必须大于 0！
                    return false;
                }
                decimal remaining = r.Source == 1
                    ? (r.PlanNum ?? 0m) - (pickCum.TryGetValue(r.ItemId, out var pc) ? pc : 0m)
                    : (r.PlanNum ?? 0m) - (prodCum.TryGetValue(Key(r.ProduceId, r.BomId, r.ItemId), out var cc) ? cc : 0m);
                if (remaining < 0m) remaining = 0m;
                if ((r.InNum ?? 0) > remaining + 0.0001m)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u5165\u5e93\u6570\u91cf\u4e0d\u80fd\u8d85\u8fc7\u5269\u4f59\u53ef\u5165\u5e93\u91cf\uff08" + remaining.ToString("0.##") + "\uff09\uff01");   // 【xx】入库数量不能超过剩余可入库量（x）！
                    return false;
                }
            }
            return true;
        }

        /// <summary>Replace-all save: delete old entries then insert new ones, inside a transaction.</summary>
        private void SaveBill(ProductInEditViewModel vm, t_PMS_StockBill header,
            List<ProductInRowInput> rows)
        {
            using (var tx = _db.Database.BeginTransaction())
            {
                var old = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (old.Count > 0) _db.t_PMS_StockBillEntry.RemoveRange(old);

                if (header.FInterID == 0 || !_db.t_PMS_StockBill.Any(h => h.FInterID == header.FInterID))
                    _db.t_PMS_StockBill.Add(header);

                // 单价 = 商品出厂价（t_ERP_ITEM.FactoryPrice），与审核生成批号时的取价同源
                // （StockService.ApplyInStock 对 FB=1 亦取 ccdj）。金额 = 数量 × 单价。
                // 服务端从 DB 重载，客户端提交的任何价格字段一律不采信。
                var priceItemIds = rows.Where(r => r.ItemId > 0).Select(r => r.ItemId).Distinct().ToList();
                var ccdjByItem = _db.t_ERP_ITEM.Where(i => priceItemIds.Contains(i.ID))
                                               .ToDictionary(i => i.ID, i => i.FactoryPrice);

                foreach (var r in rows)
                {
                    int stepId = r.StepId > 0 ? r.StepId : StepIdOf(r.ProduceId, r.BomId);
                    bool isPick = r.Source == 1;
                    decimal qty = r.InNum ?? 0m;
                    decimal? price = ccdjByItem.ContainsKey(r.ItemId) ? ccdjByItem[r.ItemId] : (decimal?)null;
                    _db.t_PMS_StockBillEntry.Add(new t_PMS_StockBillEntry
                    {
                        FInterID = header.FInterID,
                        FStepID = stepId,    // derived server-side
                        FBomId = r.BomId,
                        FProduceNo = isPick ? (r.PickBillNo ?? "") : (r.ProduceNo ?? ""),
                        FProduceID = isPick ? (int?)null : r.ProduceId,
                        FItemID = r.ItemId,
                        FPlanDate = vm.Date,
                        FPlanNum = r.PlanNum ?? 0m,                  // 计划数量
                        FNum = qty,                                  // 入库数量
                        FPrice = price,                              // 单价 = 出厂价 ccdj
                        FAmount = price.HasValue ? qty * price.Value : (decimal?)null,   // 金额 = 数量 × 单价
                        // 生产入库不涉及采购含税价：FAfterTax* 保持 NULL（与 FB=8 领料单一致）
                        FNote = string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note.Trim(),
                        FBatchNo = "",                               // stamped on audit (purchase-bill rule)
                        FBatchNoID = null,
                        FBillUseEntryID = 0,
                        FBillUseNo = "",
                        FPickBillID = isPick && r.PickBillId > 0 ? (int?)r.PickBillId : null,
                        FPickEntryID = isPick && r.PickEntryId > 0 ? (int?)r.PickEntryId : null,
                        FROB = GONES.Web.Services.StockService.DirIn,   // +1, in-stock (NOT NULL column)
                    });
                }
                _db.SaveChanges();
                tx.Commit();
            }
        }

        /// <summary>Produce-order step lookup: StepEntry(FInterID, FBomId) -> FStepID.</summary>
        private int StepIdOf(int produceId, int bomId)
        {
            if (produceId <= 0 || bomId <= 0) return 0;
            var se = _db.t_PMS_ProduceOrderStepEntry
                .FirstOrDefault(e => e.FInterID == produceId && e.FBomId == bomId && e.FStepID != null);
            return se?.FStepID ?? 0;
        }

        /// <summary>
        /// On audit (isAudit=true) mark the source produce-order product rows as fully stocked
        /// (FStockStatus=1) only when their CUMULATIVE audited stock-in qty reaches the plan, and
        /// accumulate the finished quantity (FFetchNum) on the originating goods-apply entry. On
        /// un-audit (isAudit=false) recompute the same, this bill's qty already excluded.
        ///
        /// A produce line can be stocked in multiple partial bills: each bill adds its own qty, and
        /// FStockStatus flips to 1 only once the plan is met. The produce-order header is advanced
        /// to "已入库" (FOrderStatus=2) when every line is fully stocked, "已领料" (1) when at least
        /// one line has been stocked, otherwise "未处理" (0).
        /// </summary>
        private void StampProduceOrderStatus(List<t_PMS_StockBillEntry> entries, bool isAudit, DateTime? billDate, int? thisBillId)
        {
            var produceIds = entries.Where(e => e.FProduceID != null).Select(e => e.FProduceID.Value).Distinct().ToList();
            if (produceIds.Count == 0) return;
            var productRows = _db.t_PMS_ProduceOrderProductEntry
                .Where(e => produceIds.Contains(e.FInterID)).ToList();

            // Cumulative audited stock-in per line, EXCLUDING this bill (it is mid-transaction and
            // not yet committed as audited on audit; already reverted on un-audit).
            // 这里要的是「实际已入库量」而非「占用额度」：只有审核才真正移动库存，
            // 草稿不该让生产单变成已入库。故 includeDraft=false。
            var stocked = LoadCumulativeStocked(produceIds, thisBillId, includeDraft: false);

            // Load the affected produce-order headers once so we can recompute their status.
            var orderIds = productRows.Select(e => e.FInterID).Distinct().ToList();
            var orders = _db.t_PMS_ProduceOrder.Where(h => orderIds.Contains(h.FInterID))
                .ToDictionary(h => h.FInterID);

            int sign = isAudit ? 1 : -1;
            bool preferImported = !isAudit;     // on un-audit revert the exact line this bill stamped
            var consumed = new HashSet<int>();
            // Per-line cumulative AFTER this bill's effect, keyed by (produce, bom, item). Used to
            // decide the line's fully-stocked flag and to recompute the order header status.
            var afterDict = new Dictionary<(int, int, int), decimal>();
            foreach (var e in entries)
            {
                if (e.FProduceID == null || !e.FItemID.HasValue) continue;
                var pe = ResolveProduceEntry(productRows, e.FProduceID.Value,
                    e.FBomId, e.FItemID.Value, consumed, preferImported);
                if (pe == null) continue;
                consumed.Add(pe.FEntryID);

                decimal before = stocked.TryGetValue(Key(pe.FInterID, pe.FBomId, pe.FItemID ?? 0), out var c) ? c : 0m;
                // On audit this bill's qty is being added now; on un-audit the bill is already
                // excluded from `stocked`, so the residual is already `before`.
                decimal after = isAudit ? before + StockService.RowQty(e) : before;
                var k = Key(pe.FInterID, pe.FBomId, pe.FItemID ?? 0);
                afterDict[k] = after;
                decimal plan = pe.FPlanNum ?? 0m;
                pe.FStockStatus = (after >= plan - 0.0001m) ? (byte)1 : (byte)0;

                ApplyFetchNumDelta(pe.FBillApplyEntryID, StockService.RowQty(e), sign, billDate);
            }

            // Recompute each affected produce order's header status from all of its lines, using the
            // post-bill cumulative (this bill's contribution overlaid on the prior aggregate).
            var cur = new Dictionary<(int, int, int), decimal>(stocked);
            foreach (var kv in afterDict) cur[kv.Key] = kv.Value;
            foreach (var ord in orders.Values)
            {
                var lines = productRows.Where(p => p.FInterID == ord.FInterID).ToList();
                if (lines.Count == 0) continue;
                bool allFull = lines.All(p =>
                {
                    var k = Key(p.FInterID, p.FBomId, p.FItemID ?? 0);
                    decimal a = cur.TryGetValue(k, out var av) ? av : 0m;
                    return a >= (p.FPlanNum ?? 0m) - 0.0001m;
                });
                bool anyStocked = lines.Any(p =>
                {
                    var k = Key(p.FInterID, p.FBomId, p.FItemID ?? 0);
                    decimal a = cur.TryGetValue(k, out var av) ? av : 0m;
                    return a > 0.0001m;
                });
                ord.FOrderStatus = allFull ? (short)2 : (anyStocked ? (short)1 : (short)0);
            }
        }

        /// <summary>
        /// Accumulate (delta=+1, on audit) or roll back (delta=-1, on un-audit) the finished
        /// quantity of the goods-apply entry the produce row came from.
        ///
        /// The old WinForms system OVERWROTE FFetchNum with the last receipt
        /// (FrmProductionInStock.cs: "SET FFetchDate=.., FFetchNum=.."), so a partially
        /// received order always lost every earlier receipt and "outstanding = FQty - FFetchNum"
        /// was wrong. We accumulate instead, and un-audit subtracts exactly what was added.
        ///
        /// Rows added by hand have no FBillApplyEntryID and are silently skipped.
        /// </summary>
        private void ApplyFetchNumDelta(int? applyEntryId, decimal qty, int delta, DateTime? billDate)
        {
            if (applyEntryId == null || applyEntryId.Value <= 0 || qty == 0m) return;

            var row = _db.t_PMS_GoodsApplyEntry.FirstOrDefault(e => e.FEntryID == applyEntryId.Value);
            if (row == null) return;

            var next = (row.FFetchNum ?? 0m) + delta * qty;
            if (next < 0m) next = 0m;
            row.FFetchNum = next;

            if (next <= 0m) row.FFetchDate = null;                      // nothing received any more
            else if (delta > 0 && billDate.HasValue) row.FFetchDate = billDate;
        }

        /// <summary>Composite key for a produce-order product line.</summary>
        private static (int, int, int) Key(int produceId, int bomId, int itemId) => (produceId, bomId, itemId);

        /// <summary>
        /// Sum of audited (FState=true) production in-stock (FBillType=1) quantities per produce line,
        /// keyed by (produce order id, BOM id, item id). Excludes a given bill when provided.
        /// Drives partial stock-in: how much of a produce line is still outstanding.
        /// </summary>
        private Dictionary<(int, int, int), decimal> LoadCumulativeStocked(
            List<int> produceIds, int? excludeBillId, bool includeDraft)
        {
            // 口径统一在 StockService「来源行占用额度」小节。两种语义不可混用：
            //   includeDraft=true  → 已占用额度（含草稿），用于防重复导入与上限校验
            //   includeDraft=false → 实际已入库量（仅已审），用于推进生产单状态
            return StockService.UsageByProduceRow(_db, BillType, produceIds, excludeBillId, includeDraft);
        }

        /// <summary>
        /// Output product (FIsProduct=1) item ids of a BOM, in row order. Empty list when the BOM
        /// has no output rows or only one output (single-product BOM is the common case). A BOM
        /// can define multiple outputs (e.g. 拣选 split-grade 手拣: 一/二/三级生干籽); each output
        /// must appear as its own pick-bill import row so the user can stock each grade
        /// independently.
        /// </summary>
        private List<int> BomOutputItemIds(int bomId)
        {
            if (bomId <= 0) return new List<int>();
            return _db.t_PMS_StepProductBom
                .Where(b => b.FBomID == bomId && b.FIsProduct == true
                            && b.FPItemID != null && b.FPItemID.Value > 0)
                .Select(b => b.FPItemID.Value)
                .ToList();
        }

        /// <summary>Output product item id (first one) of a BOM; null if none. Used by the produce-order branch which expects single output.</summary>
        private int? BomOutputItemId(int bomId)
        {
            var ids = BomOutputItemIds(bomId);
            return ids.Count == 0 ? (int?)null : ids[0];
        }

        /// <summary>Base quantity (FBaseNum) of a BOM input row (FIsProduct!=true) for the given item.</summary>
        private decimal BomInputBase(int bomId, int itemId)
        {
            if (bomId <= 0 || itemId <= 0) return 0m;
            var b = _db.t_PMS_StepProductBom
                .Where(x => x.FBomID == bomId && x.FPItemID == itemId && x.FIsProduct != true)
                .FirstOrDefault();
            return b?.FBaseNum ?? 0m;
        }

        /// <summary>
        /// Sum of audited (FState=true) production in-stock (FBillType=1) quantities per output
        /// item, for rows that reference the given warehouse-pick bill (FPickBillID). Drives
        /// partial stock-in of the pick-bill source: how much of its produced output is still
        /// outstanding.
        /// </summary>
        private Dictionary<int, decimal> LoadPickCumulative(int pickBillId, int? excludeBillId, bool includeDraft)
        {
            // 口径统一在 StockService「来源行占用额度」小节。两种语义不可混用：
            //   includeDraft=true  → 已占用额度（含草稿），用于防重复导入与上限校验
            //   includeDraft=false → 实际已入库量（仅已审），用于推进生产单状态
            return StockService.UsageByPickItem(_db, BillType, pickBillId, excludeBillId, includeDraft);
        }

        /// <summary>Item snapshots for display cells (create/edit redisplay).</summary>
        private void BindRowDisplay(ProductInEditViewModel vm, List<ProductInRowInput> rows)
        {
            var ids = rows.Where(r => r.ItemId > 0).Select(r => r.ItemId).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => ids.Contains(i.ID)).ToDictionary(i => i.ID);
            foreach (var r in rows)
            {
                t_ERP_ITEM it = items.ContainsKey(r.ItemId) ? items[r.ItemId] : null;
                if (string.IsNullOrEmpty(r.Cpbm)) r.Cpbm = it?.ItemCode ?? "";
                if (string.IsNullOrEmpty(r.ProductName)) r.ProductName = it?.ItemShortName ?? "";
                if (string.IsNullOrEmpty(r.Spec)) r.Spec = it?.ItemSpec ?? "";
                if (string.IsNullOrEmpty(r.CategoryName)) r.CategoryName = it?.ProductCategory ?? "";
                if (string.IsNullOrEmpty(r.Unit)) r.Unit = it?.BaseUnit ?? "";
                // 单价/金额为服务端派生展示值：仅在尚未赋值时取当前出厂价 ccdj 兜底。
                // ViewForm 已在构造时写入明细落库价，此处不得覆盖（否则历史单据金额会随
                // 商品出厂价变动而漂移）。客户端提交的价格字段一律忽略。
                if (!r.Price.HasValue) r.Price = it?.FactoryPrice;
                if (!r.Amount.HasValue && r.Price.HasValue)
                    r.Amount = (r.InNum ?? 0m) * r.Price.Value;
            }
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
            // 检验人下拉：码表 t_erp_data_dict 中 f_parent_id=8 的字典项（检验人名单）。
            ViewBag.Inspectors = _db.t_ERP_DataDict
                .Where(d => d.FParentID == 8)
                .OrderBy(d => d.ID)
                .ToList();
            var creater = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == CurrentUserId);
            ViewBag.CreaterName = creater?.UserName ?? "";
        }

        // GET /ProductIn/Export?dateFrom=&dateTo=&billNo=&state=
        [HttpGet]
        [HttpGet]
        public IActionResult Export(DateTime? dateFrom, DateTime? dateTo, string billNo, int? state)
        {
            billNo = billNo?.Trim();
            var rows = StockBillExport.BuildRows(_db, _depot, BillType, dateFrom, dateTo, billNo, null, state);
            var cols = new List<string> { "审核", "单据日期", "单据编号", "入库部门", "车间", "入库人", "生产单号", "批号", "产品代码", "分类", "产品名称", "规格", "单位", "计划数量", "入库数量", "单价", "金额", "备注" };
            var data = rows.Select(r => new List<string>
            {
                r.State ? "已审" : "未审",
                r.Date?.ToString("yyyy-MM-dd") ?? "",
                r.BillNo ?? "",
                r.DeptName ?? "", r.WorkShopName ?? "", r.ManagerName ?? "",
                r.ProduceNo ?? "", r.BatchNo ?? "",
                r.Cpbm ?? "", r.Cplb ?? "", r.Cpjc ?? "", r.Cpgg ?? "", r.UnitName ?? "",
                (r.PlanNum ?? 0m).ToString("0.00"),
                (r.Qty ?? 0m).ToString("0.00"),
                (r.Price ?? 0m).ToString("0.00##"),
                ((r.Price ?? 0m) * (r.Qty ?? 0m)).ToString("0.00"),
                r.Note ?? "",
            }).ToList();
            return ExcelExport.HtmlTable("生产入库单_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".xls", cols, data);
        }
    }
}
