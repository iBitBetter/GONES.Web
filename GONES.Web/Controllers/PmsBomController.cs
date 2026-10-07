using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GONES.Model.Pg;
using GONES.Web.Models;
using GONES.Web.Services;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// BOM \u6e05\u5355\u8bbe\u7f6e \u2014\u2014 \u65e7 WinForms BaseInfo.FrmProductRelationBomManager / FrmProductRelationBomEdit \u7684 Web \u7248\u3002
    ///
    /// \u6570\u636e\u8868 t_PMS_StepProductBom\uff1a\u4e00\u4e2a BOM \u5355 = \u540c\u4e00 FBomID \u4e0b\u7684\u4e00\u7ec4\u660e\u7ec6\u884c\uff0c\u5f52\u5c5e\u4e00\u4e2a\u5de5\u5e8f FStepId\u3002
    /// \u5546\u54c1\u6765\u6e90\u4e3a t_ERP_ITEM\uff08\u65e7\u7cfb\u7edf\u7528\u7684\u662f t_ICItemCore / v_PMS_ICItem\uff0c\u672c\u6a21\u5757\u6309\u65b0\u7ed3\u6784\u6539\u4e3a\u5546\u54c1\u8868\uff09\u3002
    /// \u4ea7\u51fa\u54c1\u5224\u5b9a\uff1at_ERP_ITEM.ProductCategory\uff08\u81ea\u52a8\u53d6\u81ea\u5546\u54c1\u5206\u7c7b t_ERP_ITEMCLASS.lb\uff09\u4e3a\u300c\u4ea7\u6210\u54c1\u300d\u6216\u300c\u534a\u6210\u54c1\u300d\u3002
    ///
    /// \u4e0e\u65e7\u7cfb\u7edf\u4e00\u81f4\u7684\u4e1a\u52a1\u89c4\u5219\uff082026-09-08 \u8d77\u65b9\u5411\u7531\u7528\u6237\u52fe\u9009\uff0c\u53d6\u6d88\u4ea7\u6210\u54c1\u9650\u5236\uff09\uff1a
    ///   1. \u6bcf\u884c\u7531\u7528\u6237\u52fe\u9009\u65b9\u5411\uff1a\u5165\u5e93 FIsProduct=1 / \u51fa\u5e93\u6295\u5165 FIsProduct=0\uff0c\u4e0d\u518d\u6309\u5546\u54c1\u7c7b\u522b\u81ea\u52a8\u5224\u5b9a\uff1b
    ///      \u4e0d\u9650\u5236\u5165\u5e93\u884c\u6570\u91cf\uff08\u53ef\u4e3a 0 \u6216\u591a\u4e2a\uff09\uff1b\u540c\u4e00\u5546\u54c1\u53ef\u540c\u65f6\u5b58\u5728\u4e00\u884c\u5165\u5e93 + \u4e00\u884c\u51fa\u5e93\uff1b
    ///   2. \u540c\u4e00\u5546\u54c1\u540c\u4e00\u65b9\u5411\u53ea\u5141\u8bb8\u4e00\u884c\uff1b\u300c\u5305\u88c5\u300d\u5de5\u5e8f\u4e0b\u5165\u5e93\u884c\u6700\u591a 1 \u884c\uff0c\u5176\u5b83\u5de5\u5e8f\u4e0d\u9650\uff1b
    ///   3. \u7f16\u8f91\u65f6\u5148\u6574\u5355\u5220\u9664\u518d\u91cd\u65b0\u63d2\u5165\uff1b\u5220\u9664\u4e3a\u6574\u5355\u7269\u7406\u5220\u9664\uff08DeleteModelByFBomID\uff09\u3002
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // \u8df3\u8fc7 Furion \u7684 ModelState \u77ed\u8def 400\uff0c\u8d70\u6807\u51c6 MVC \u8868\u5355\u56de\u663e
    public class PmsBomController : Controller, IMenuGuarded
    {
        /// <summary>\u300c\u4ea7\u6210\u54c1\u300d\u300c\u534a\u6210\u54c1\u300d\u2014\u2014 \u4e0e\u5546\u54c1\u5206\u7c7b t_ERP_ITEMCLASS.lb \u5bf9\u9f50\uff0c\u7528\u4e8e\u5224\u5b9a BOM \u4ea7\u51fa\u54c1\u3002</summary>
        private static readonly string[] OutputClasses = { "\u4ea7\u6210\u54c1" };


        private readonly GonesPgDbContext _db;
        private readonly MenuService _menu;
        public PmsBomController(GonesPgDbContext db, MenuService menu)
        {
            _db = db;
            _menu = menu;
        }


        // ------------------------------------------------------------ \u5217\u8868

        // GET /PmsBom?stepId=0
        public IActionResult Index(int? stepId)
        {

            ViewBag.Steps = _db.t_PMS_Step.OrderBy(s => s.FItemID).ToList();
            var cur = stepId ?? 0;
            ViewBag.StepId = cur;

            var query = _db.t_PMS_StepProductBom.AsQueryable();
            if (cur > 0) query = query.Where(b => b.FStepId == cur);

            // 明细按「单号 → 行序」排好再进聚合，页面展开即是旧网格的分组顺序。
            ViewBag.Summaries = BuildSummaries(query.OrderBy(b => b.FBomID).ThenBy(b => b.FID).ToList());

            // 左侧每个工序的 BOM 单数（不受当前筛选影响）
            var counts = _db.t_PMS_StepProductBom
                .Where(b => b.FStepId != null)
                .GroupBy(b => b.FStepId.Value)
                .Select(g => new { StepId = g.Key, Cnt = g.Select(x => x.FBomID).Distinct().Count() })
                .ToList();
            ViewBag.StepCounts = counts.ToDictionary(x => x.StepId, x => x.Cnt);
            ViewBag.TotalBomCount = _db.t_PMS_StepProductBom.Select(b => b.FBomID).Distinct().Count();
            return View();
        }

        // ------------------------------------------------------------ \u65b0\u589e

        // GET /PmsBom/Create?stepId=6
        [HttpGet]
        public IActionResult Create(int stepId)
        {

            var step = _db.t_PMS_Step.FirstOrDefault(s => s.FItemID == stepId);
            if (step == null)
            {
                TempData["Error"] = "\u8bf7\u5148\u9009\u62e9\u5de5\u5e8f\u518d\u65b0\u589e BOM \u6e05\u5355\u3002";
                return RedirectToAction(nameof(Index));
            }

            BindFormExtras(stepId);
            ViewBag.IsNew = true;
            return View("Form", new BomEditViewModel
            {
                BomId = NextBomId(),
                StepId = stepId,
                Rows = new List<BomRowInput>(),
            });
        }

        // POST /PmsBom/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(BomEditViewModel vm)
        {

            var step = _db.t_PMS_Step.FirstOrDefault(s => s.FItemID == vm.StepId);
            if (step == null) ModelState.AddModelError(string.Empty, "\u5de5\u5e8f\u4e0d\u5b58\u5728\u3002");

            var items = LoadItems(vm);
            if (step == null || !ValidateBom(vm, items))
            {
                BindFormExtras(vm.StepId);
                ViewBag.IsNew = true;
                return View("Form", vm);
            }

            // Regenerate BomId on save (not trust the GET display value) and persist
            // under one lock so concurrent creates cannot share the same FBomID.
            lock (BomIdLock)
            {
                vm.BomId = NextBomId();
                SaveBom(vm, step, items);
            }
            TempData["Success"] = "BOM \u6e05\u5355\u4fdd\u5b58\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index), new { stepId = vm.StepId });
        }

        // ------------------------------------------------------------ \u7f16\u8f91

        // GET /PmsBom/Edit/5 \u2014\u2014 id \u4e3a FBomID
        [HttpGet]
        public IActionResult Edit(int id)
        {

            var rows = _db.t_PMS_StepProductBom.Where(b => b.FBomID == id).OrderBy(b => b.FID).ToList();
            if (rows.Count == 0) return NotFound();
            var first = rows.First();

            BindFormExtras(first.FStepId ?? 0);
            ViewBag.IsNew = false;
            return View("Form", new BomEditViewModel
            {
                BomId = id,
                StepId = first.FStepId ?? 0,
                Rows = rows.Select(r => new BomRowInput
                {
                    ItemId = r.FPItemID ?? 0,
                    BaseNum = r.FBaseNum ?? 0,
                    Unit = string.IsNullOrWhiteSpace(r.FBaseUnit) ? r.FLossUnit : r.FBaseUnit,
                    LossStandValue = r.FLossStandValue ?? 0,
                    LossRate = r.FLossRate,
                    LossValue = r.FLossValue ?? 0,
                    Remark = r.FRemark,
                    IsProduct = r.FIsProduct == true,
                }).Where(r => r.ItemId > 0).ToList(),
            });
        }

        // POST /PmsBom/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, BomEditViewModel vm)
        {

            var exists = _db.t_PMS_StepProductBom.Any(b => b.FBomID == id);
            if (!exists) return NotFound();
            vm.BomId = id;   // \u4ee5\u8def\u7531\u4e3a\u51c6\uff0c\u9632\u6b62\u8868\u5355\u7be1\u6539\u5355\u53f7

            var step = _db.t_PMS_Step.FirstOrDefault(s => s.FItemID == vm.StepId);
            if (step == null) ModelState.AddModelError(string.Empty, "\u5de5\u5e8f\u4e0d\u5b58\u5728\u3002");

            var items = LoadItems(vm);
            if (step == null || !ValidateBom(vm, items))
            {
                BindFormExtras(vm.StepId);
                ViewBag.IsNew = false;
                return View("Form", vm);
            }

            SaveBom(vm, step, items);
            TempData["Success"] = "BOM \u6e05\u5355\u4fee\u6539\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index), new { stepId = vm.StepId });
        }

        // ------------------------------------------------------------ \u590d\u5236

        // GET /PmsBom/Copy/5 \u2014\u2014 \u590d\u5236\u51fa\u4e00\u5f20\u65b0\u5355\u53f7
        [HttpGet]
        public IActionResult Copy(int id)
        {

            var rows = _db.t_PMS_StepProductBom.Where(b => b.FBomID == id).OrderBy(b => b.FID).ToList();
            if (rows.Count == 0) return NotFound();
            var first = rows.First();

            BindFormExtras(first.FStepId ?? 0);
            ViewBag.IsNew = true;   // \u590d\u5236\u8d70\u65b0\u5efa\u4fdd\u5b58\u903b\u8f91\uff0c\u4f46\u7528\u65b0\u5355\u53f7
            return View("Form", new BomEditViewModel
            {
                BomId = NextBomId(),
                StepId = first.FStepId ?? 0,
                Rows = rows.Select(r => new BomRowInput
                {
                    ItemId = r.FPItemID ?? 0,
                    BaseNum = r.FBaseNum ?? 0,
                    Unit = string.IsNullOrWhiteSpace(r.FBaseUnit) ? r.FLossUnit : r.FBaseUnit,
                    LossStandValue = r.FLossStandValue ?? 0,
                    LossRate = r.FLossRate,
                    LossValue = r.FLossValue ?? 0,
                    Remark = r.FRemark,
                    IsProduct = r.FIsProduct == true,
                }).Where(r => r.ItemId > 0).ToList(),
            });
        }

        // ------------------------------------------------------------ item search (form picker, search-as-you-type)

        // GET /PmsBom/SearchItems?keyword=xxx
        [HttpGet]
        public IActionResult SearchItems(string keyword)
        {

            var kw = (keyword ?? string.Empty).Trim();
            if (kw.Length == 0) return Json(new List<object>());

            // numeric keyword also matches item ID exactly (operators often type the ID)
            int idMatch = int.TryParse(kw, out var n) ? n : -1;

            var list = _db.t_ERP_ITEM
                .Where(i => i.IsEnabled == true)
                .Where(i => (i.ItemCode != null && i.ItemCode.Contains(kw))
                         || (i.ItemShortName != null && i.ItemShortName.Contains(kw))
                         || (i.ItemSpec != null && i.ItemSpec.Contains(kw))
                         || i.ID == idMatch)
                .OrderBy(i => i.ItemCode)
                .Take(20)
                .ToList()
                .Select(i => new
                {
                    id = i.ID,
                    cpbm = i.ItemCode,
                    cpjc = i.ItemShortName,
                    cpgg = i.ItemSpec,
                    cplb = i.ProductCategory,
                    unit = i.BaseUnit,
                    isOutput = IsOutput(i),
                })
                .ToList();
            return Json(list);
        }

        // ------------------------------------------------------------ \u5220\u9664\uff08\u6574\u5355\uff09

        // POST /PmsBom/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {

            var rows = _db.t_PMS_StepProductBom.Where(b => b.FBomID == id).ToList();
            if (rows.Count == 0) return NotFound();

            var stepId = rows.First().FStepId ?? 0;

            // 引用检查：删掉 BOM 后仍指向该 FBomId 的下游明细会展开成空明细，
            // 生产入库的分次入库与批次生成会静默算错。只要被任何一张下游单据引用，
            // 就不允许物理删除；确需停用的改用「禁用」（FDelete=1），保留行以维持引用完整性。
            int usedByProduce = _db.t_PMS_ProduceOrderProductEntry.Count(e => e.FBomId == id);
            int usedByApply = _db.t_PMS_GoodsApplyEntry.Count(e => e.FBomId == id);
            int usedByStock = _db.t_PMS_StockBillEntry.Count(e => e.FBomId == id);
            if (usedByProduce + usedByApply + usedByStock > 0)
            {
                var parts = new List<string>();
                if (usedByProduce > 0) parts.Add(usedByProduce + " 条生产单明细");
                if (usedByApply > 0) parts.Add(usedByApply + " 条要货单明细");
                if (usedByStock > 0) parts.Add(usedByStock + " 条库存单据明细");

                TempData["Error"] = "该 BOM 已被 " + string.Join("、", parts) +
                    "引用，不能删除；如确定不再使用，请点「禁用」将其停用（历史单据不受影响）。";
                return RedirectToAction(nameof(Index), new { stepId = stepId });
            }

            _db.t_PMS_StepProductBom.RemoveRange(rows);
            _db.SaveChanges();

            TempData["Success"] = "BOM \u6e05\u5355\u5220\u9664\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index), new { stepId = stepId });
        }

        // ------------------------------------------------------------ 禁用 / 启用（整单）

        // POST /PmsBom/Disable/5 —— 整单禁用（FDelete=1）。
        // 已被下游单据引用过的 BOM 不允许物理删除，只能禁用：保留明细行以维持引用完整性，
        // 同时让下游按「FDelete == 0」展开 BOM 时不再取到它（不再出现在要货/生产的选择列表）。
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Disable(int id) => SetDisabled(id, true);

        // POST /PmsBom/Enable/5 —— 整单启用（FDelete=0），恢复被下游选用的资格。
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Enable(int id) => SetDisabled(id, false);

        private IActionResult SetDisabled(int id, bool disabled)
        {

            var rows = _db.t_PMS_StepProductBom.Where(b => b.FBomID == id).ToList();
            if (rows.Count == 0) return NotFound();

            var stepId = rows.First().FStepId ?? 0;
            var flag = disabled ? 1 : 0;
            foreach (var r in rows) r.FDelete = flag;
            _db.SaveChanges();

            TempData["Success"] = disabled ? "BOM 清单已禁用。" : "BOM 清单已启用。";
            return RedirectToAction(nameof(Index), new { stepId = stepId });
        }

        // ------------------------------------------------------------ \u79c1\u6709\u65b9\u6cd5

        // Process-wide lock serializes "read max + 1" for BOM id generation.
        private static readonly object BomIdLock = new object();

        /// <summary>\u65b0\u5355\u53f7\uff1amax(FBomID)+1\uff08\u4e0e\u65e7\u7cfb\u7edf GetMaxBomId \u4e00\u81f4\uff09\u3002</summary>
        private int NextBomId()
        {
            lock (BomIdLock)
            {
                var max = _db.t_PMS_StepProductBom.Max(b => (int?)b.FBomID) ?? 0;
                return max + 1;
            }
        }

        /// <summary>\u628a\u660e\u7ec6\u884c\u6309 FBomID \u805a\u5408\u6210\u5217\u8868\u9875\u9700\u8981\u7684\u6c47\u603b\u4fe1\u606f\u3002</summary>
        private List<BomSummary> BuildSummaries(List<t_PMS_StepProductBom> rows)
        {
            var result = new List<BomSummary>();
            var valid = rows.Where(r => r.FBomID != null).ToList();
            if (valid.Count == 0) return result;

            // 商品主档与仓库名**一次批量取**（DISTINCT ID -> 两条 SQL），不按行 N+1 查。
            var itemIds = valid
                .Where(r => r.FPItemID != null && r.FPItemID.Value > 0)
                .Select(r => r.FPItemID.Value)
                .Distinct()
                .ToList();
            var items = itemIds.Count == 0
                ? new Dictionary<int, t_ERP_ITEM>()
                : _db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);
            var warehouses = LoadWarehouseNames(items.Values.Select(i => i.WarehouseId));

            foreach (var g in valid.GroupBy(r => r.FBomID.Value).OrderBy(g => g.Key))
            {
                var list = g.ToList();
                var first = list.First();
                var outRow = list.FirstOrDefault(r => r.FIsProduct == true);

                var summary = new BomSummary
                {
                    BomId = g.Key,
                    StepId = first.FStepId,
                    StepName = first.FStepName,
                    ItemCount = list.Count(r => r.FPItemID != null && r.FPItemID.Value > 0),
                    // 「禁用」是整单状态：BOM 单下所有明细行 FDelete 全为 1 才算已禁用。
                    // 与下游口径一致——要货/生产展开 BOM 时都是按 FDelete == 0 取行。
                    Disabled = list.All(r => (r.FDelete ?? 0) == 1),
                };

                // 明细行：按 FID 顺序（与旧网格同序），列口径见 BomDetailRow。
                foreach (var r in list)
                {
                    items.TryGetValue(r.FPItemID ?? 0, out var it);
                    summary.Details.Add(new BomDetailRow
                    {
                        ItemId = r.FPItemID ?? 0,
                        Cpbm = it?.ItemCode,
                        Cpjc = it?.ItemShortName,
                        Cpgg = it?.ItemSpec,
                        Cplb = it?.ProductCategory,
                        Unit = string.IsNullOrWhiteSpace(r.FBaseUnit) ? (it?.BaseUnit ?? "") : r.FBaseUnit,
                        StockNum = it?.StockQuantity ?? 0m,
                        WarehouseName = WarehouseNameOf(warehouses, it?.WarehouseId),
                        BaseNum = r.FBaseNum ?? 0m,
                        StepName = r.FStepName,
                        Remark = r.FRemark,
                        IsProduct = r.FIsProduct == true,
                        Stopped = it != null && !(it.IsEnabled ?? false),
                    });
                }
                summary.InCount = summary.Details.Count(d => d.IsProduct);
                summary.OutCount = summary.Details.Count - summary.InCount;

                if (outRow != null && outRow.FPItemID != null
                    && items.TryGetValue(outRow.FPItemID.Value, out var outItem))
                {
                    summary.OutputCpbm = outItem.ItemCode;
                    summary.OutputName = outItem.ItemShortName;
                    summary.OutputClass = outItem.ProductCategory;
                    summary.OutputStopped = !(outItem.IsEnabled ?? false);
                }
                result.Add(summary);
            }
            return result;
        }

        /// <summary>\u6309\u660e\u7ec6\u884c\u7684 ItemId \u6279\u91cf\u53d6\u5546\u54c1\uff0c\u8fd4\u56de ID -> \u5546\u54c1\u5feb\u7167\u3002</summary>
        // ── 仓库名唯一口径（与 WarehousePickController.LoadWarehouseNames 完全一致）──────────
        // 仓库名 = 物料主档 t_ERP_ITEM.WarehouseId -> t_ERP_Department(IsDEP=1).DEPName。
        // ⚠ 绝不可取 t_PMS_BatchNoStock.FName：那列存的是**建批操作员**，本库实测恒为
        //   「超级管理员」，与仓库毫无关系。守卫 tests/probe_wh_warehouse_name.js。
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

        private Dictionary<int, t_ERP_ITEM> LoadItems(BomEditViewModel vm)
        {
            var ids = (vm.Rows ?? new List<BomRowInput>())
                .Where(r => r.ItemId > 0)
                .Select(r => r.ItemId)
                .Distinct()
                .ToList();
            return _db.t_ERP_ITEM.Where(i => ids.Contains(i.ID)).ToDictionary(i => i.ID);
        }

        /// <summary>
        /// \u4e1a\u52a1\u6821\u9a8c\uff082026-09-08 \u89c4\u5219\uff09\uff1a
        /// \u660e\u7ec6\u975e\u7a7a / \u5546\u54c1\u5b58\u5728 / \u540c\u5546\u54c1\u540c\u65b9\u5411\u4e0d\u91cd\u590d\uff08\u540c\u5546\u54c1\u53ef\u5206\u522b\u4e00\u884c\u5165\u5e93+\u4e00\u884c\u51fa\u5e93\uff09/
        /// \u300c\u5305\u88c5\u300d\u5de5\u5e8f\u5165\u5e93\u884c\u6700\u591a 1 \u884c\uff0c\u5176\u5b83\u5de5\u5e8f\u4e0d\u9650\u3002\u4e0d\u518d\u8981\u6c42\u5fc5\u987b\u6709\u4ea7\u51fa\u54c1\u3002
        /// </summary>
        private bool ValidateBom(BomEditViewModel vm, Dictionary<int, t_ERP_ITEM> items)
        {
            var rows = (vm.Rows ?? new List<BomRowInput>()).Where(r => r.ItemId > 0).ToList();
            if (rows.Count == 0)
            {
                ModelState.AddModelError("Rows", "BOM \u660e\u7ec6\u4e0d\u80fd\u4e3a\u7a7a\uff0c\u8bf7\u81f3\u5c11\u6dfb\u52a0\u4e00\u4e2a\u5546\u54c1\u3002");
                return false;
            }

            foreach (var r in rows)
            {
                if (!items.ContainsKey(r.ItemId))
                {
                    ModelState.AddModelError("Rows", "\u6240\u9009\u5546\u54c1\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664\uff0c\u8bf7\u91cd\u65b0\u9009\u62e9\u3002");
                    return false;
                }
            }

            // \u540c\u5546\u54c1\u540c\u65b9\u5411\uff08\u5165\u5e93/\u51fa\u5e93\uff09\u53ea\u5141\u8bb8\u4e00\u884c\uff1b\u540c\u5546\u54c1\u53ef\u5206\u522b\u6dfb\u52a0\u4e00\u884c\u5165\u5e93\u4e0e\u4e00\u884c\u51fa\u5e93\u3002
            var dupPair = rows.GroupBy(r => new { r.ItemId, r.IsProduct }).FirstOrDefault(g => g.Count() > 1);
            if (dupPair != null)
            {
                var dupNm = items[dupPair.Key.ItemId].ItemShortName;
                ModelState.AddModelError("Rows",
                    "\u5546\u54c1\u201c" + dupNm + "\u201d\u5728" + (dupPair.Key.IsProduct ? "\u5165\u5e93" : "\u51fa\u5e93") +
                    "\u65b9\u5411\u4e0a\u91cd\u590d\uff1b\u5982\u9700\u540c\u65f6\u5165\u5e93\u4e0e\u51fa\u5e93\uff0c\u8bf7\u5206\u522b\u6dfb\u52a0\u4e24\u884c\u5e76\u52fe\u9009\u4e0d\u540c\u65b9\u5411\u3002");
                return false;
            }

            // \u300c\u5305\u88c5\u300d\u5de5\u5e8f\uff1a\u5165\u5e93\u884c\u6700\u591a 1 \u884c\uff1b\u5176\u5b83\u5de5\u5e8f\u5165\u5e93\u884c\u6570\u91cf\u4e0d\u9650\u3002
            var inCount = rows.Count(r => r.IsProduct);
            if (inCount > 1)
            {
                var stepName = _db.t_PMS_Step
                    .Where(s => s.FItemID == vm.StepId)
                    .Select(s => s.FName)
                    .FirstOrDefault() ?? "";
                if (string.Equals(stepName.Trim(), "\u5305\u88c5", StringComparison.Ordinal))
                {
                    ModelState.AddModelError("Rows", "\u300c\u5305\u88c5\u300d\u5de5\u5e8f\u4e0b\u5165\u5e93\uff08FIsProduct=1\uff09\u884c\u6700\u591a\u53ea\u80fd\u6709 1 \u884c\uff01");
                    return false;
                }
            }

            return true;
        }

        /// <summary>\u6574\u5355\u4fdd\u5b58\uff1a\u5148\u5220\u65e7\u884c\u518d\u63d2\u5165\u65b0\u884c\uff08\u4e0e\u65e7\u7cfb\u7edf Edit \u5206\u652f\u4e00\u81f4\uff09\uff0c\u4e8b\u52a1\u4fdd\u62a4\u3002</summary>
        private void SaveBom(BomEditViewModel vm, t_PMS_Step step, Dictionary<int, t_ERP_ITEM> items)
        {
            using (var tx = _db.Database.BeginTransaction())
            {
                var old = _db.t_PMS_StepProductBom.Where(b => b.FBomID == vm.BomId).ToList();
                // 整单「先删旧行再插新行」：先记下原禁用状态，否则一次编辑就会把已禁用的
                // BOM 静默复活（表单里没有这个开关；新建/复制走新单号时 old 为空 → 默认启用）。
                bool wasDisabled = old.Count > 0 && old.All(r => (r.FDelete ?? 0) == 1);
                if (old.Count > 0) _db.t_PMS_StepProductBom.RemoveRange(old);

                foreach (var r in (vm.Rows ?? new List<BomRowInput>()).Where(r => r.ItemId > 0))
                {
                    var it = items[r.ItemId];
                    var unit = string.IsNullOrWhiteSpace(r.Unit) ? it.BaseUnit : r.Unit.Trim();

                    _db.t_PMS_StepProductBom.Add(new t_PMS_StepProductBom
                    {
                        FPItemID = r.ItemId,
                        FStepId = step.FItemID,
                        FStepName = step.FName,
                        FBaseNum = r.BaseNum ?? 0,
                        FBaseUnit = unit,
                        FLossUnit = unit,
                        FLossStandValue = r.LossStandValue ?? 0,
                        FLossRate = string.IsNullOrWhiteSpace(r.LossRate) ? null : r.LossRate.Trim(),
                        FLossValue = r.LossValue ?? 0,
                        FRemark = r.Remark,
                        FBomID = vm.BomId,
                        FIsProduct = r.IsProduct,
                        // FDelete 只表示「整单禁用」，不再由商品停用状态推导——否则停用一个物料
                        // 就会把该行标成 1、与整单禁用状态互相覆盖。物料停用由选择器负责过滤：
                        // SearchItems / BomItems 都按 t_ERP_ITEM.IsEnabled == true 取商品。
                        FDelete = wasDisabled ? 1 : 0,
                        FCostType = null,
                        FPreStepID = null,
                        FPrePItemID = null,
                    });
                }

                _db.SaveChanges();
                tx.Commit();
            }
        }

        /// <summary>\u662f\u5426\u4ea7\u51fa\u54c1\uff1a\u5546\u54c1\u7c7b\u522b cplb \u4e3a\u300c\u4ea7\u6210\u54c1\u300d\u3002</summary>
        private static bool IsOutput(t_ERP_ITEM item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ProductCategory)) return false;
            var lb = item.ProductCategory.Trim();
            return OutputClasses.Any(c => string.Equals(c, lb, StringComparison.Ordinal));
        }

        /// <summary>\u8868\u5355\u9875\u6240\u9700\u6570\u636e\uff1a\u5de5\u5e8f\u4e0b\u62c9\u3001\u5546\u54c1\u9009\u62e9\u5668\u3001\u5df2\u9009\u884c\u5546\u54c1\u5feb\u7167\u3001\u5355\u4f4d\u5b57\u5178\u3001\u5404\u5de5\u5e8f\u9ed8\u8ba4\u503c\u3002</summary>
        /// <param name="keepStepId">本单当前已引用的工序。即使它已被禁用也要保留在下拉中：
        /// 否则编辑历史 BOM 时下拉会回退到首项，保存即把该单的工序**静默换掉**。</param>
        private void BindFormExtras(int keepStepId = 0)
        {
            // 禁用工序不进「新建选择器」，但当前单已引用的那个必须保留可选。
            var steps = _db.t_PMS_Step
                .Where(s => s.FDelete == 0 || s.FItemID == keepStepId)
                .OrderBy(s => s.FItemID)
                .ToList();
            ViewBag.Steps = steps;

            // PickerItems removed: item picker is now search-as-you-type via /PmsBom/SearchItems.

            // \u5df2\u9009\u884c\u5c55\u793a\u7528\uff08\u542b\u5df2\u505c\u7528\u5546\u54c1\uff0c\u5426\u5219\u7f16\u8f91\u5386\u53f2 BOM \u65f6\u884c\u4f1a\u663e\u793a\u7a7a\u767d\uff09
            ViewBag.RowItems = _db.t_ERP_ITEM
                .ToList()
                .ToDictionary(i => i.ID, i => new BomPickerItem
                {
                    Id = i.ID,
                    Cpbm = i.ItemCode,
                    Cpjc = i.ItemShortName,
                    Cpgg = i.ItemSpec,
                    Cplb = i.ProductCategory,
                    Unit = i.BaseUnit,
                    IsOutput = IsOutput(i),
                    Stopped = !(i.IsEnabled ?? false),
                });

            // \u5355\u4f4d\u5b57\u5178\uff1at_ERP_DataDict \u4e2d FParentID=5\uff08\u5355\u4f4d\uff09
            ViewBag.UnitOptions = _db.t_ERP_DataDict
                .Where(d => d.FParentID == 5 && d.DictName != null && d.DictName != "")
                .OrderBy(d => d.ID)
                .Select(d => d.DictName)
                .ToList();

            // \u5404\u5de5\u5e8f\u7684\u635f\u8017\u9ed8\u8ba4\u503c\uff0c\u65b0\u589e\u884c\u65f6\u5e26\u5165\uff08\u65e7\u7cfb\u7edf\u53d6\u81ea t_PMS_Step\uff09
            var defaults = new Dictionary<string, object>();
            foreach (var s in steps)
            {
                defaults[s.FItemID.ToString()] = new
                {
                    baseNum = s.FNum ?? 0,
                    lossStand = s.FLossStandValue ?? 0,
                    lossRate = s.FLossRate ?? "0%",
                    lossValue = s.FLossValue ?? 0,
                    lossUnit = s.FLossUnit,
                };
            }
            ViewBag.StepDefaultsJson = JsonSerializer.Serialize(defaults);
        }
    }
}
