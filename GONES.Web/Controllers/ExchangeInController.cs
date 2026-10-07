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
    /// 换货入库单 (HHRK) - 迁移自旧 WinForms
    /// Stock.FrmPackingPrintingProblemInStockManager / FrmPackingPrintingProblemInStock
    /// (t_ERP_Menu.ID=1299「换货入库维护」)。
    /// 表头 t_PMS_StockBill (FBillType=6, 前缀 HHRK, FBillTypeEx=换货入库, FROB=+1 蓝字)，
    /// 明细 t_PMS_StockBillEntry。表头字段与采购入库单(FBillType=0)完全一致。
    ///
    /// 明细只能从「已审核的仓库退料单」(FBillType=3, FState=1) 导入，且只取
    /// 「厂家原因」退料数量 FNumExt1 —— 仓库退料四列口径：
    /// FNum=正常 / FNumExt1=厂家 / FNumExt2=人为 / FNumExt3=其它。
    ///
    /// 信任边界：(ReturnBillId, ReturnEntryId) 定位来源退料明细行，ItemId / 商品名称 /
    /// 规格 / 单位 / 厂家退料量 / 剩余可换量 / 原批号 全部由服务端重新推导，
    /// 表单只接受「本次入库数量 + 单价 + 备注」。
    ///
    /// 超额防护：同一来源行，已审核换货入库单的累计数量 + 本次 不得超过该行厂家退料量。
    ///
    /// 审核：StockService.ApplyInStock（与采购入库同规则新建批次 yyyyMMdd+分类码+日序号，
    /// 并重算 t_ERP_ITEM.StockQuantity / 批次余额）；反审核 RevertInStock（批次已被下游消耗则拒绝）。
    /// 删除：明细 + 表头单次 SaveChanges（tri_deleteStockBillEntry 触发器已于 2026-09-09 删除）。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 标准 MVC 表单回显，跳过 Furion 400 短路
    public class ExchangeInController : Controller, IMenuGuarded
    {
        private const int BillType = 6;             // 换货入库
        private const int ReturnBillType = 3;       // 仓库退料（导入源）
        private const string BillNoPrefix = "HHRK";
        private const string BillTypeExName = "\u6362\u8d27\u5165\u5e93";   // 换货入库
        /// <summary>W1 越权提示：读可以少看，动必须整套归你（编辑/审核/反审核/删除共用一份文案）。</summary>
        private const string OutOfScopeMsg = "\u8be5\u5355\u636e\u5305\u542b\u4e0d\u5c5e\u4e8e\u60a8\u7ba1\u7406\u4ed3\u5e93\u7684\u660e\u7ec6\uff0c\u7981\u6b62\u64cd\u4f5c\uff01";   // 该单据包含不属于您管理仓库的明细，禁止操作！

        private readonly GonesPgDbContext _db;
        private readonly StockService _stock;
        private readonly MenuService _menu;
        private readonly DepotScope _depot;
        public ExchangeInController(GonesPgDbContext db, StockService stock, MenuService menu, DepotScope depot)
        {
            _db = db;
            _stock = stock;
            _menu = menu;
            _depot = depot;
        }

        private bool IsAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        /// <summary>
        /// 入口闸门：admin 旁路恒 true（行为逐字节不变），非 admin 须由其角色的
        /// t_ERP_RoleMenu 授权覆盖本模块（1299）。行级判定只用 item.WarehouseId（仓库维度）。
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

        // ------------------------------------------------------------ 列表

        // GET /ExchangeIn?dateFrom=&dateTo=&billNo=&pym=&state=&page=&pageSize=
        public IActionResult Index(DateTime? dateFrom, DateTime? dateTo, string billNo, string pym,
            int? state, int page = 1, int pageSize = 20)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);
            billNo = billNo?.Trim();
            pym = pym?.Trim();

            // 只在表头上过滤，再按「单」分页（一单的明细不会被拆到两页）
            var hq = _db.t_PMS_StockBill.Where(h => h.FBillType == BillType);
            // 数据闸门①-a（行级读）：只保留"至少有一行明细属于本仓库"的单据。
            if (_depot.IsRestricted) hq = _depot.FilterBills(hq);
            // 把 .Date 提到 lambda 外：EF6 无法翻译 Nullable<DateTime>.Date
            var dFrom = dateFrom.HasValue ? dateFrom.Value.Date : (DateTime?)null;
            var dTo = dateTo.HasValue ? dateTo.Value.Date.AddDays(1) : (DateTime?)null;
            if (dFrom.HasValue) hq = hq.Where(h => h.FDate >= dFrom.Value);
            if (dTo.HasValue) hq = hq.Where(h => h.FDate < dTo.Value);
            if (!string.IsNullOrEmpty(billNo)) hq = hq.Where(h => h.FBillNo.Contains(billNo));
            if (state.HasValue && state.Value >= 0) hq = hq.Where(h => h.FState == (state.Value == 1));
            if (!string.IsNullOrEmpty(pym))
            {
                // 拼音码：单据里含有该拼音码商品的单
                var pymItemIds = _db.t_ERP_ITEM
                    .Where(i => i.ItemPinyinCode != null && i.ItemPinyinCode.Contains(pym))
                    .Select(i => i.ID)
                    .ToList();
                var hitInterIds = _db.t_PMS_StockBillEntry
                    .Where(e => e.FItemID != null && pymItemIds.Contains(e.FItemID.Value))
                    .Select(e => e.FInterID)
                    .Distinct()
                    .ToList();
                hq = hq.Where(h => hitInterIds.Contains(h.FInterID));
            }

            int total = hq.Count();                     // 单据数，不是行数
            page = PagerViewModel.ClampPage(page, total, pageSize);
            var headers = hq.OrderByDescending(h => h.FInterID)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();
            var headerIds = headers.Select(h => h.FInterID).ToList();

            // 全部命中单据的合计（旧网格页脚风格）
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

            // 数据闸门③-b（按钮灰化）：与服务端 W1 硬拦同源。
            var lockedByDepot = _depot.BillsNotFullyInScope(headerIds);

            var rows = new List<ExchangeInListRow>();
            foreach (var h in headers)
            {
                var hid = h.FInterID;
                var billEntries = entriesByBill.ContainsKey(hid) ? entriesByBill[hid] : new List<t_PMS_StockBillEntry>();
                var first = true;
                if (billEntries.Count == 0)
                {
                    rows.Add(new ExchangeInListRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate,
                        State = h.FState ?? false, BuyingUnit = h.FBuyingUnit,
                        DeptName = DeptName(h.FDeptID),
                        ManagerName = (h.FManagerID != null && workers.ContainsKey(h.FManagerID.Value)) ? workers[h.FManagerID.Value] : "",
                        FirstOfBill = true,
                        CanModify = !lockedByDepot.Contains(h.FInterID),
                    });
                    continue;
                }
                foreach (var e in billEntries)
                {
                    t_ERP_ITEM it = null;
                    if (e.FItemID != null && items.ContainsKey(e.FItemID.Value)) it = items[e.FItemID.Value];
                    rows.Add(new ExchangeInListRow
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
                        ReturnBillNo = e.FBillUseNo,
                        Cpbm = it?.ItemCode ?? "",
                        Cplb = it?.ProductCategory ?? "",
                        Cpjc = it?.ItemShortName ?? "",
                        Cpgg = it?.ItemSpec ?? "",
                        UnitName = it?.BaseUnit ?? "",
                        Qty = e.FNum,
                        Price = e.FPrice,
                        Amount = e.FAmount,
                        Note = e.FNote,
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

        // ------------------------------------------------------------ 新建

        [HttpGet]
        public IActionResult Create()
        {

            BindFormExtras();
            var vm = new ExchangeInEditViewModel
            {
                BillNo = NextBillNo(),
                Date = DateTime.Today,
                Rows = new List<ExchangeInRowInput>(),
            };
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(ExchangeInEditViewModel vm)
        {

            if (!DeriveFromReturnBill(vm) || !ValidateBill(vm))
            {
                BindFormExtras(vm.DeptId, vm.WorkShopId);
                return View("Form", vm);
            }

            // 单号 + FInterID 在同一次锁内生成并落库，避免并发新建读到同一个 max
            lock (StockService.BillNoLock)
            {
                var header = new t_PMS_StockBill
                {
                    FInterID = NextInterId(),
                    FBillNo = NextBillNo(),          // 保存时重新取号（旧系统行为）
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
                    FROB = GONES.Web.Services.StockService.DirIn,   // +1，蓝字入库
                    FState = false,
                    FModifyID = null,
                    FModifyTime = null,
                };
                SaveBill(vm, header);
            }

            TempData["Success"] = "\u6362\u8d27\u5165\u5e93\u5355\u4fdd\u5b58\u6210\u529f\u3002";   // 换货入库单保存成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ 编辑

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

        // GET /ExchangeIn/Details/5 —— 已审单只读查看（与 Edit 同一套装载逻辑）
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
            var entries = entryQ
                .OrderBy(e => e.FEntryID)
                .ToList();

            // 来源退料行（用于回显厂家退料量 / 剩余可换量 / 原批号）
            var srcBillIds = entries
                .Where(e => e.FPickBillID != null && e.FPickBillID.Value > 0)
                .Select(e => e.FPickBillID.Value)
                .Distinct()
                .ToList();
            var srcRows = LoadReturnRows(srcBillIds);
            var usedMap = LoadExchangedMap(srcBillIds, id);

            var itemIds = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            var rows = new List<ExchangeInRowInput>();
            foreach (var e in entries)
            {
                if (e.FItemID == null || e.FItemID.Value == 0) continue;
                var it = items.ContainsKey(e.FItemID.Value) ? items[e.FItemID.Value] : null;
                var key = ((e.FPickBillID ?? 0), (e.FPickEntryID ?? 0));
                var src = srcRows.ContainsKey(key) ? srcRows[key] : null;
                decimal used = usedMap.ContainsKey(key) ? usedMap[key] : 0m;
                decimal factoryNum = src?.FNumExt1 ?? 0m;

                rows.Add(new ExchangeInRowInput
                {
                    ReturnBillId = e.FPickBillID,
                    ReturnEntryId = e.FPickEntryID,
                    ReturnBillNo = e.FBillUseNo,
                    SrcBatchNo = src?.FBatchNo ?? "",
                    ItemId = e.FItemID.Value,
                    ProductName = it?.ItemShortName ?? "",
                    Spec = it?.ItemSpec ?? "",
                    CategoryName = it?.ProductCategory ?? "",
                    Unit = it?.BaseUnit ?? "",
                    FactoryNum = factoryNum,
                    RestNum = Math.Max(0m, factoryNum - used),
                    Qty = e.FNum,
                    Price = e.FPrice,
                    Note = e.FNote,
                });
            }

            var vm = new ExchangeInEditViewModel
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
            var creater = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == header.FCreaterID);
            var modifier = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == header.FModifyID);
            ViewBag.CreaterName = creater?.UserName ?? "";
            ViewBag.ModifyName = (header.FModifyID != null) ? (modifier?.UserName ?? "") : "";
            ViewBag.ModifyTime = header.FModifyTime;
            ViewBag.ReadOnly = readOnly;
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, ExchangeInEditViewModel vm)
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
            vm.BillNo = header.FBillNo;   // 单号不可变

            if (!DeriveFromReturnBill(vm) || !ValidateBill(vm))
            {
                BindFormExtras(vm.DeptId, vm.WorkShopId);
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
            SaveBill(vm, header);

            TempData["Success"] = "\u6362\u8d27\u5165\u5e93\u5355\u4fee\u6539\u6210\u529f\u3002";   // 换货入库单修改成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ 删除

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

            // tri_deleteStockBillEntry 触发器已删（2026-09-09）：明细 + 表头单次提交
            using (var tx = _db.Database.BeginTransaction())
            {
                var entries = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == id).ToList();
                _db.t_PMS_StockBillEntry.RemoveRange(entries);
                _db.t_PMS_StockBill.Remove(header);
                _db.SaveChanges();
                tx.Commit();
            }

            TempData["Success"] = "\u6362\u8d27\u5165\u5e93\u5355\u5220\u9664\u6210\u529f\u3002";   // 换货入库单删除成功。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ 审核 / 反审核

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
                // 空单绝不允许变成「已审核」：会污染台账对账
                TempData["Error"] = "\u5355\u636e\u6ca1\u6709\u660e\u7ec6\uff0c\u65e0\u6cd5\u5ba1\u6838\u3002";   // 单据没有明细，无法审核。
                return RedirectToAction(nameof(Index));
            }

            // 审核 = 建批次 + 抬高 t_ERP_ITEM.StockQuantity + 重算批次余额；整段在一个事务里
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

                // 来源单复验：换货行引用来源退料单（FPickBillID → FB=3）。本单还是草稿时，
                // 来源退料单可被反审核/删除；不复查就按失效来源建批次入库 = 幻影库存。
                var srcIds = entries.Select(e => e.FPickBillID).Where(v => v.HasValue && v.Value > 0)
                                    .Select(v => v.Value).Distinct().ToList();
                if (srcIds.Count > 0)
                {
                    var auditedSrc = _db.t_PMS_StockBill
                        .Where(h => h.FBillType == ReturnBillType && (h.FState ?? false) && srcIds.Contains(h.FInterID))
                        .Select(h => h.FInterID).ToList();
                    var missing = srcIds.Where(v => !auditedSrc.Contains(v)).ToList();
                    if (missing.Count > 0)
                    {
                        TempData["Error"] = "\u6765\u6e90\u9000\u6599\u5355\u5df2\u88ab\u53cd\u5ba1\u6838\u6216\u5220\u9664\uff0c\u65e0\u6cd5\u5ba1\u6838\u672c\u6362\u8d27\u5165\u5e93\u5355\u3002";   // 来源退料单已被反审核或删除，无法审核本换货入库单。
                        return RedirectToAction(nameof(Index));
                    }
                }

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

            // 反审核：清标记 + 删掉本单建的批次 + 按台账重算缓存；
            // 批次已被下游出库消耗时拒绝（会变负库存）
            string revertError;
            using (var tx = _stock.BeginStockTransaction(_db))
            {
                if (!_stock.RevertInStock(_db, header, entries, out revertError))
                {
                    TempData["Error"] = revertError;
                    return RedirectToAction(nameof(Index));       // tx disposed -> 回滚
                }

                // FState 已由 RevertInStock 内部置 false 并 SaveChanges，此处无需重复赋值。
                tx.Commit();
            }

            TempData["Success"] = "\u53cd\u5ba1\u6838\u6210\u529f\uff0c\u6279\u6b21\u4e0e\u5e93\u5b58\u5df2\u56de\u6eda\u3002";   // 反审核成功，批次与库存已回滚。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ 仓库退料单选择（模态）

        // GET /ExchangeIn/GetReturnBills
        // 已审核的仓库退料单（FBillType=3, FState=1），最新在前，最多 50 张。
        // 只统计「厂家原因」数量：factoryQty = Σ FNumExt1，restQty = Σ (厂家量 - 已审换货入库累计)。
        [HttpGet]
        public IActionResult GetReturnBills()
        {

            var hq = _db.t_PMS_StockBill
                .Where(h => h.FBillType == ReturnBillType && (h.FState ?? false));
            // 数据闸门②-a（来源单选择器收窄）：只列"至少有一行明细属于本仓库"的退料单。
            if (_depot.IsRestricted) hq = _depot.FilterBills(hq);
            var headers = hq
                .OrderByDescending(h => h.FInterID)
                .Take(50)
                .ToList();
            var ids = headers.Select(h => h.FInterID).ToList();
            if (ids.Count == 0) return Json(new List<object>());

            var entries = _db.t_PMS_StockBillEntry
                .Where(e => ids.Contains(e.FInterID) && e.FItemID != null && e.FItemID.Value > 0)
                .Select(e => new
                {
                    e.FInterID,
                    e.FEntryID,
                    ItemId = e.FItemID.Value,
                    Factory = e.FNumExt1 ?? 0m,
                })
                .ToList();
            var usedMap = LoadExchangedMap(ids, null);

            var stats = new Dictionary<int, (int rows, decimal factory, decimal rest)>();
            foreach (var e in entries)
            {
                var used = usedMap.ContainsKey((e.FInterID, e.FEntryID)) ? usedMap[(e.FInterID, e.FEntryID)] : 0m;
                var rest = Math.Max(0m, e.Factory - used);
                if (!stats.ContainsKey(e.FInterID))
                    stats[e.FInterID] = (0, 0m, 0m);
                var cur = stats[e.FInterID];
                stats[e.FInterID] = (cur.rows + 1, cur.factory + e.Factory, cur.rest + rest);
            }

            var deptIds = headers.Where(h => h.FDeptID != null).Select(h => h.FDeptID.Value).Distinct().ToList();
            var depts = _db.t_ERP_Department.Where(d => deptIds.Contains(d.ID)).ToDictionary(d => d.ID, d => d.DEPName);

            var result = headers.Select(h =>
            {
                // 三元另一侧必须写成同名元组，否则元素名会退化成 (int, decimal, decimal)
                var st = stats.ContainsKey(h.FInterID) ? stats[h.FInterID] : (rows: 0, factory: 0m, rest: 0m);
                return new
                {
                    interId = h.FInterID,
                    billNo = h.FBillNo,
                    date = (h.FDate ?? DateTime.Today).ToString("yyyy-MM-dd"),
                    deptName = (h.FDeptID != null && depts.ContainsKey(h.FDeptID.Value)) ? depts[h.FDeptID.Value] : "",
                    rowCount = st.rows,
                    factoryQty = st.factory,
                    restQty = st.rest,
                };
            }).ToList();
            return Json(result);
        }

        // GET /ExchangeIn/GetReturnBillDetails?interId=N
        // 该退料单里所有「厂家原因数量 > 0」的明细行，已扣除已审换货入库累计，
        // restNum <= 0 的行（已换满）直接不返回。
        [HttpGet]
        public IActionResult GetReturnBillDetails(int interId)
        {

            var header = _db.t_PMS_StockBill.FirstOrDefault(h => h.FInterID == interId
                && h.FBillType == ReturnBillType && (h.FState ?? false));
            if (header == null) return Json(new List<object>());

            var returnBillIds = new List<int> { interId };
            var srcRows = LoadReturnRows(returnBillIds);
            var usedMap = LoadExchangedMap(returnBillIds, null);

            var itemIds = srcRows.Values
                .Where(e => e.FItemID != null)
                .Select(e => e.FItemID.Value)
                .Distinct()
                .ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            var detail = new List<ExchangeInReturnRow>();
            foreach (var e in srcRows.Values.OrderBy(e => e.FEntryID))
            {
                if (e.FItemID == null || e.FItemID.Value == 0) continue;
                // 数据闸门②-b：越仓的来源退料行不进导入流程。
                if (!_depot.AllowsItem(e.FItemID.Value)) continue;
                var factory = e.FNumExt1 ?? 0m;
                if (factory <= 0m) continue;                 // 只取厂家原因导致的退料数量

                var key = (interId, e.FEntryID);
                var used = usedMap.ContainsKey(key) ? usedMap[key] : 0m;
                var rest = Math.Max(0m, factory - used);
                if (rest <= 0.0001m) continue;               // 已换满

                var it = items.ContainsKey(e.FItemID.Value) ? items[e.FItemID.Value] : null;
                detail.Add(new ExchangeInReturnRow
                {
                    returnBillId = interId,
                    returnEntryId = e.FEntryID,
                    returnBillNo = header.FBillNo ?? "",
                    itemId = e.FItemID.Value,
                    cpbm = it?.ItemCode ?? "",
                    productName = it?.ItemShortName ?? "",
                    spec = it?.ItemSpec ?? "",
                    unit = it?.BaseUnit ?? "",
                    categoryName = it?.ProductCategory ?? "",
                    batchNo = e.FBatchNo ?? "",
                    warehouseName = WarehouseNameOf(it),
                    factoryNum = factory,
                    restNum = rest,
                    // 换货入库的商品是厂家免费补送的，单价默认 0；如需计入成本可在明细里手填。
                    price = 0m,
                });
            }
            return Json(detail);
        }

        // GET /ExchangeIn/GetWorkShops?deptId=65
        [HttpGet]
        public IActionResult GetWorkShops(int deptId)
        {
            var list = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 2 && d.ParentID == deptId)
                .OrderBy(d => d.ID)
                .Select(d => new { id = d.ID, name = d.DEPName })
                .ToList();
            return Json(list);
        }

        // GET /ExchangeIn/GetManagers?workShopId=66
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

        // ------------------------------------------------------------ helpers

        // 进程级锁：所有单据类型共用（同一张 t_PMS_StockBill 表）—— BillNoLock 由 StockService 提供。

        /// <summary>"HHRK" + 8 位流水 = 同 FBillType 内 max(FBillNo)+1（旧 GetStockBillNoByFBillType）。</summary>
        private string NextBillNo()
        {
            lock (StockService.BillNoLock)
            {
                // 只认 "HHRK"+8 位数字，脏数据跳过，避免污染序列
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

        /// <summary>按退料单 id 取其全部明细行（用于推导来源字段）。</summary>
        private Dictionary<(int billId, int entryId), t_PMS_StockBillEntry> LoadReturnRows(List<int> returnBillIds)
        {
            var result = new Dictionary<(int, int), t_PMS_StockBillEntry>();
            if (returnBillIds.Count == 0) return result;
            var rows = _db.t_PMS_StockBillEntry.Where(e => returnBillIds.Contains(e.FInterID)).ToList();
            foreach (var e in rows) result[(e.FInterID, e.FEntryID)] = e;
            return result;
        }

        /// <summary>
        /// 所有换货入库单（FB=6，含草稿与已审核；已删除的单据已物理移除不会计入）按来源退料行
        /// (billId, entryId) 的累计换货量（FNum）。用于计算"剩余可换量"，防止同一退料行被重复换货。
        /// 含草稿是关键：草稿占用的额度也必须计入，否则两张草稿可各导入同一退料行并各保存一次，
        /// 审核后合计换货量会超过厂家退料量（重复换货、虚增库存）。
        /// excludeInterId：编辑已有单据时排除本单自身，否则会把自己的量也算成已用。
        /// 反审核回草稿的单据仍计入（FState=false 但仍在表内），避免"反审核即释放→再换一次"的漏洞。
        /// 口径与 ProductLossController.LoadLossedMap 一致（两者共用"来源退料行额度"模型）。
        /// </summary>
        private Dictionary<(int billId, int entryId), decimal> LoadExchangedMap(List<int> returnBillIds, int? excludeInterId)
        {
            // 口径统一在 StockService「来源行占用额度」小节：includeDraft=true，
            // 草稿也占可换额度，否则两张草稿可各导入同一退料行造成重复换货。
            return StockService.UsageByPickRow(_db, BillType, returnBillIds, excludeInterId, includeDraft: true);
        }

        /// <summary>
        /// 信任边界：每行的 (ReturnBillId, ReturnEntryId) 必须能定位到一张已审核退料单(FBillType=3)的明细行。
        /// ItemId / 名称 / 规格 / 单位 / 厂家退料量 / 剩余可换量 / 原批号 全部服务端重算，
        /// 表单里的这些值一律覆盖。定位不到就报错。
        /// </summary>
        private bool DeriveFromReturnBill(ExchangeInEditViewModel vm)
        {
            var rows = (vm.Rows ?? new List<ExchangeInRowInput>()).ToList();
            var keys = rows
                .Where(r => r.ReturnBillId.HasValue && r.ReturnEntryId.HasValue)
                .Select(r => (r.ReturnBillId.Value, r.ReturnEntryId.Value))
                .Distinct()
                .ToList();
            if (keys.Count == 0)
            {
                ModelState.AddModelError("Rows", "\u8bf7\u5148\u4ece\u4ed3\u5e93\u9000\u6599\u5355\u5bfc\u5165\u6362\u8d27\u660e\u7ec6\u3002");   // 请先从仓库退料单导入换货明细。
                return false;
            }

            var billIds = keys.Select(k => k.Item1).Distinct().ToList();
            var returnBills = _db.t_PMS_StockBill
                .Where(h => h.FBillType == ReturnBillType && (h.FState ?? false) && billIds.Contains(h.FInterID))
                .ToDictionary(h => h.FInterID, h => h);
            var srcRows = LoadReturnRows(billIds);
            var usedMap = LoadExchangedMap(billIds, vm.InterId > 0 ? vm.InterId : (int?)null);

            var itemIds = srcRows.Values
                .Where(e => e.FItemID != null)
                .Select(e => e.FItemID.Value)
                .Distinct()
                .ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);

            foreach (var r in rows)
            {
                if (!r.ReturnBillId.HasValue || !r.ReturnEntryId.HasValue
                    || !returnBills.ContainsKey(r.ReturnBillId.Value)
                    || !srcRows.ContainsKey((r.ReturnBillId.Value, r.ReturnEntryId.Value)))
                {
                    ModelState.AddModelError("Rows", "\u6362\u8d27\u660e\u7ec6\u7684\u6765\u6e90\u9000\u6599\u5355\u884c\u4e0d\u5b58\u5728\u6216\u672a\u5ba1\u6838\uff0c\u8bf7\u91cd\u65b0\u5bfc\u5165\u3002");   // 换货明细的来源退料单行不存在或未审核，请重新导入。
                    return false;
                }

                var rb = returnBills[r.ReturnBillId.Value];
                var se = srcRows[(r.ReturnBillId.Value, r.ReturnEntryId.Value)];
                var factoryNum = se.FNumExt1 ?? 0m;
                if (factoryNum <= 0m)
                {
                    ModelState.AddModelError("Rows", "\u6765\u6e90\u9000\u6599\u884c\u6ca1\u6709\u5382\u5bb6\u539f\u56e0\u9000\u6599\u6570\u91cf\uff0c\u4e0d\u80fd\u6362\u8d27\u5165\u5e93\u3002");   // 来源退料行没有厂家原因退料数量，不能换货入库。
                    return false;
                }

                var used = usedMap.ContainsKey((r.ReturnBillId.Value, r.ReturnEntryId.Value))
                    ? usedMap[(r.ReturnBillId.Value, r.ReturnEntryId.Value)] : 0m;
                var it = (se.FItemID != null && items.ContainsKey(se.FItemID.Value)) ? items[se.FItemID.Value] : null;

                r.ItemId = se.FItemID ?? 0;
                r.ReturnBillNo = rb.FBillNo ?? "";
                r.SrcBatchNo = se.FBatchNo ?? "";
                r.ProductName = it?.ItemShortName ?? "";
                r.Spec = it?.ItemSpec ?? "";
                r.CategoryName = it?.ProductCategory ?? "";
                r.Unit = it?.BaseUnit ?? "";
                r.FactoryNum = factoryNum;
                r.RestNum = Math.Max(0m, factoryNum - used);
                // 厂家免费赠送，单价默认为 0（不取来源退料行单价）；用户手填则以表单为准。
                if (r.Price == null) r.Price = 0m;
            }
            return true;
        }

        private bool ValidateBill(ExchangeInEditViewModel vm)
        {
            // SQL Server datetime 下限 1753；解析失败会绑成 0001-01-01，EF6 按 datetime2 发送 -> SqlException 242
            if (vm.Date == default || vm.Date.Year < 2000)
            {
                ModelState.AddModelError("Date", "\u8bf7\u586b\u5199\u6b63\u786e\u7684\u5355\u636e\u65e5\u671f\uff01");   // 请填写正确的单据日期！
                return false;
            }

            if (string.IsNullOrWhiteSpace(vm.Inspectors))
            {
                ModelState.AddModelError("Inspectors", "\u8bf7\u9009\u62e9\u68c0\u9a8c\u4eba\uff01");   // 请选择检验人！
                return false;
            }

            var rows = (vm.Rows ?? new List<ExchangeInRowInput>())
                .Where(r => r.ReturnBillId.HasValue && r.ReturnEntryId.HasValue)
                .ToList();
            if (rows.Count == 0)
            {
                ModelState.AddModelError("Rows", "\u8bf7\u81f3\u5c11\u5bfc\u5165\u4e00\u6761\u6362\u8d27\u660e\u7ec6\uff01");   // 请至少导入一条换货明细！
                return false;
            }

            // 数据闸门③-a（写入）：导入行的商品必须全部属于本仓库。DeriveFromReturnBill 已先行
            // 校验存在性并回填 ProductName，admin 旁路不进入此段。
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

            // 同一来源行不允许重复（否则会重复入库且超额校验被绕过）
            var seen = new HashSet<(int, int)>();
            foreach (var r in rows)
            {
                var key = (r.ReturnBillId.Value, r.ReturnEntryId.Value);
                if (!seen.Add(key))
                {
                    ModelState.AddModelError("Rows", "\u6362\u8d27\u660e\u7ec6\u4e2d\u5b58\u5728\u91cd\u590d\u7684\u9000\u6599\u5355\u884c\uff01");   // 换货明细中存在重复的退料单行！
                    return false;
                }
            }

            foreach (var r in rows)
            {
                var label = string.IsNullOrWhiteSpace(r.ProductName)
                    ? ("\u7b2c " + (rows.IndexOf(r) + 1) + " \u884c")     // 第 N 行
                    : r.ProductName;
                var qty = r.Qty ?? 0m;
                if (qty <= 0m)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u6362\u8d27\u5165\u5e93\u6570\u91cf\u5fc5\u987b\u5927\u4e8e 0\uff01");   // 【X】换货入库数量必须大于 0！
                    return false;
                }
                if ((r.Price ?? 0m) < 0m)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u5355\u4ef7\u4e0d\u80fd\u4e3a\u8d1f\u6570\uff01");   // 【X】单价不能为负数！
                    return false;
                }
                // 超额防护：本次 + 已审累计 不得超过来源行的厂家退料量
                var rest = r.RestNum ?? 0m;
                if (qty > rest + 0.0001m)
                {
                    ModelState.AddModelError("Rows", "\u3010" + label + "\u3011\u6362\u8d27\u6570\u91cf " + qty.ToString("0.##")
                        + " \u8d85\u8fc7\u53ef\u6362\u4f59\u91cf " + Math.Max(0m, rest).ToString("0.##")
                        + " \uff08\u5382\u5bb6\u539f\u56e0\u9000\u6599 " + (r.FactoryNum ?? 0m).ToString("0.##") + "\uff09\uff01");
                    return false;
                }
            }
            return true;
        }

        /// <summary>整单替换保存：先删旧明细再插新明细，同一事务内。</summary>
        private void SaveBill(ExchangeInEditViewModel vm, t_PMS_StockBill header)
        {
            using (var tx = _db.Database.BeginTransaction())
            {
                var old = _db.t_PMS_StockBillEntry.Where(e => e.FInterID == header.FInterID).ToList();
                if (old.Count > 0) _db.t_PMS_StockBillEntry.RemoveRange(old);

                if (header.FInterID == 0 || !_db.t_PMS_StockBill.Any(h => h.FInterID == header.FInterID))
                    _db.t_PMS_StockBill.Add(header);

                foreach (var r in (vm.Rows ?? new List<ExchangeInRowInput>())
                         .Where(r => r.ReturnBillId.HasValue && r.ReturnEntryId.HasValue))
                {
                    var qty = r.Qty ?? 0m;
                    var price = r.Price ?? 0m;
                    var amount = Math.Round(qty * price, 2);

                    var entry = new t_PMS_StockBillEntry
                    {
                        FInterID = header.FInterID,
                        FStepID = 0,
                        FBomId = 0,
                        FItemID = r.ItemId,
                        FNum = qty,                          // 换货入库数量
                        FPrice = price,
                        FAmount = amount,
                        // 换货不是采购，不走税率；但批号单价取 FAfterTaxPrice，
                        // 因此这里同步写一份，保证审核建批次时批次价不为空。
                        FAfterTaxPrice = price,
                        FAfterTaxAmount = amount,
                        FNote = string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note.Trim(),
                        FBatchNo = "",                       // 审核时由 ApplyInStock 生成（换回的是新货，新批次）
                        FPickBillID = r.ReturnBillId,        // 来源退料单 FInterID
                        FPickEntryID = r.ReturnEntryId,      // 来源退料明细 FEntryID
                        FBillUseNo = r.ReturnBillNo ?? "",   // 来源退料单号（追溯）
                        FROB = GONES.Web.Services.StockService.DirIn,   // +1，入库（非空列）
                    };
                    _db.t_PMS_StockBillEntry.Add(entry);
                }

                _db.SaveChanges();
                tx.Commit();
            }
        }

        /// <summary>仓库名：item.WarehouseId -> t_ERP_Department (IsDEP=1).DEPName。</summary>
        private string WarehouseNameOf(t_ERP_ITEM it)
        {
            if (it?.WarehouseId == null || it.WarehouseId.Value <= 0) return "";
            return _db.t_ERP_Department
                .Where(d => d.ID == it.WarehouseId.Value && d.IsDEP == 1)
                .Select(d => d.DEPName)
                .FirstOrDefault() ?? "";
        }

        /// <summary>
        /// 表单数据：入库部门（一级部门）、入库车间（IsDEP=2）、入库人（t_PMS_Worker.DEPID=车间）、
        /// 供货单位（码表 f_parent_id=4，与采购入库单一致）。与采购入库单保持一致。
        /// </summary>
        private void BindFormExtras(int? deptId = null, int? workShopId = null)
        {
            // 入库部门：一级部门（ParentID=1）
            ViewBag.DeptOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.ParentID == 1)
                .OrderBy(d => d.ID)
                .ToList();

            // 入库车间：所选部门下的车间（IsDEP=2）
            ViewBag.WorkShopOptions = _db.t_ERP_Department
                .Where(d => d.Status == 1 && d.IsDEP == 2 && d.ParentID == deptId)
                .OrderBy(d => d.ID)
                .ToList();

            // 入库人：所选车间下的人员
            ViewBag.Managers = _db.t_PMS_Worker
                .Where(w => w.Status == true && w.DEPID == workShopId)
                .OrderBy(w => w.ID)
                .ToList();

            ViewBag.Suppliers = _db.t_ERP_DataDict
                .Where(d => d.FParentID == 4)
                .OrderBy(d => d.ID)
                .ToList();

            // 检验人下拉：码表 t_erp_data_dict 中 f_parent_id=8 的字典项（检验人名单）。
            ViewBag.Inspectors = _db.t_ERP_DataDict
                .Where(d => d.FParentID == 8)
                .OrderBy(d => d.ID)
                .ToList();

            var creater = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == CurrentUserId);
            ViewBag.CreaterName = creater?.UserName ?? "";
            ViewBag.ModifyName = "";
            ViewBag.ModifyTime = null;
        }

        // GET /ExchangeIn/Export?dateFrom=&dateTo=&billNo=&state=
        [HttpGet]
        [HttpGet]
        public IActionResult Export(DateTime? dateFrom, DateTime? dateTo, string billNo, string pym, int? state)
        {
            billNo = billNo?.Trim();
            pym = pym?.Trim();
            // pym 走 pymItemKeyword（商品拼音码命中单据），与 Index 第 99-112 行同口径；
            // 不能走 BuildRows 的 pym 参数（那是供货单位模糊匹配）。
            var rows = StockBillExport.BuildRows(_db, _depot, BillType, dateFrom, dateTo, billNo, null, state, pym);
            var cols = new List<string> { "审核", "单据日期", "单据编号", "批号", "来源退料单", "产品代码", "产品分类", "产品名称", "规格", "单位", "换货数量", "单价", "金额", "供货单位" };
            var data = rows.Select(r => new List<string>
            {
                r.State ? "已审" : "未审",
                r.Date?.ToString("yyyy-MM-dd") ?? "",
                r.BillNo ?? "",
                r.BatchNo ?? "", r.FromBillUseNo ?? "",
                r.Cpbm ?? "", r.Cplb ?? "", r.Cpjc ?? "", r.Cpgg ?? "", r.UnitName ?? "",
                (r.Qty ?? 0m).ToString("0.00"),
                (r.Price ?? 0m).ToString("0.00##"),
                (r.Amount ?? 0m).ToString("0.00"),
                r.BuyingUnit ?? "",
            }).ToList();
            return ExcelExport.HtmlTable("兑换入库单_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".xls", cols, data);
        }
    }
}
