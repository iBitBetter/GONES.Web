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
    /// 工序名称 —— 旧 WinForms BaseInfo.FrmStep 的 Web 版（生产-基础资料 / t_PMS_Step）。
    /// 下拉字典取自数据字典 t_ERP_DataDict：
    ///   FParentID=5 工序类型(自制工序/委外工序)、6 工序算法(计时/计件)、9 计量单位(工序单位/计价单位/损耗单位)。
    /// 删除与旧系统一致为物理删除，但增加引用检查（BOM/损耗方案引用时拒绝删除）。
    ///
    /// 禁用（FDelete，1=已禁用 / 0=已启用）：
    ///   与 BOM 清单同口径 —— 被引用的工序不能物理删除，改用「禁用」。
    ///   禁用后不再出现在**新建选择器**（BOM 表单的工序下拉、选品弹窗的工序下拉）；
    ///   但历史单据按 FStepID 解析工序名不受影响（那些路径用整表字典，不过滤）。
    ///   编辑既有 BOM 时，其当前工序若已禁用仍会保留在下拉中，避免保存时被静默换掉。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 跳过 Furion 的 ModelState 短路 400，走标准 MVC 表单回显
    public class PmsStepController : Controller, IMenuGuarded
    {

        private readonly GonesPgDbContext _db;
        private readonly MenuService _menu;
        public PmsStepController(GonesPgDbContext db, MenuService menu)
        {
            _db = db;
            _menu = menu;
        }


        // GET /PmsStep
        public IActionResult Index(int page = 1, int pageSize = 20)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);

            var query = _db.t_PMS_Step.AsQueryable()
                .OrderBy(s => s.FItemID);
            int total = query.Count();
            page = PagerViewModel.ClampPage(page, total, pageSize);
            var list = query.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            ViewBag.Pager = new PagerViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                Action = "Index"
            };
            return View(list);
        }

        // GET /PmsStep/Create
        [HttpGet]
        public IActionResult Create()
        {
            BindDicts();
            return View("Form", NewWithDefaults());
        }

        // POST /PmsStep/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(StepEditViewModel vm)
        {

            if (!ModelState.IsValid)
            {
                BindDicts();
                return View("Form", vm);
            }

            var dupMsg = CheckDuplicate(vm.Name?.Trim(), null);
            if (!string.IsNullOrEmpty(dupMsg))
            {
                ModelState.AddModelError(string.Empty, dupMsg);
                BindDicts();
                return View("Form", vm);
            }

            var entity = new t_PMS_Step
            {
                FName = vm.Name?.Trim(),
                FType = vm.Type?.Trim(),
                FAlgo = vm.Algo?.Trim(),
                FHour = vm.Hour ?? 0,
                FNum = vm.Num ?? 0,
                FSingleArti = vm.SingleArti ?? 0,
                FHourArti = vm.HourArti ?? 0,
                FPrice = vm.Price ?? 0,
                FWorkHourUnit = vm.WorkHourUnit?.Trim(),
                FPriceUnit = vm.PriceUnit?.Trim(),
                FFixedLoss = vm.FixedLoss ?? 0,
                FLossStandValue = vm.LossStandValue ?? 0,
                FLossRate = vm.LossRate?.Trim(),
                FLossValue = vm.LossValue ?? 0,
                FLossUnit = vm.LossUnit?.Trim(),
                FDelete = 0,          // 新建即启用
            };

            _db.t_PMS_Step.Add(entity);
            _db.SaveChanges();
            TempData["Success"] = "工序保存成功！";
            return RedirectToAction(nameof(Index));
        }

        // GET /PmsStep/Edit/5
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var step = _db.t_PMS_Step.FirstOrDefault(s => s.FItemID == id);
            if (step == null) return NotFound();
            BindDicts();
            return View("Form", Map(step));
        }

        // POST /PmsStep/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, StepEditViewModel vm)
        {
            if (id != vm.ID) return BadRequest();

            var step = _db.t_PMS_Step.FirstOrDefault(s => s.FItemID == id);
            if (step == null) return NotFound();

            if (!ModelState.IsValid)
            {
                BindDicts();
                return View("Form", vm);
            }

            var dupMsg = CheckDuplicate(vm.Name?.Trim(), id);
            if (!string.IsNullOrEmpty(dupMsg))
            {
                ModelState.AddModelError(string.Empty, dupMsg);
                BindDicts();
                return View("Form", vm);
            }

            step.FName = vm.Name?.Trim();
            step.FType = vm.Type?.Trim();
            step.FAlgo = vm.Algo?.Trim();
            step.FHour = vm.Hour ?? 0;
            step.FNum = vm.Num ?? 0;
            step.FSingleArti = vm.SingleArti ?? 0;
            step.FHourArti = vm.HourArti ?? 0;
            step.FPrice = vm.Price ?? 0;
            step.FWorkHourUnit = vm.WorkHourUnit?.Trim();
            step.FPriceUnit = vm.PriceUnit?.Trim();
            step.FFixedLoss = vm.FixedLoss ?? 0;
            step.FLossStandValue = vm.LossStandValue ?? 0;
            step.FLossRate = vm.LossRate?.Trim();
            step.FLossValue = vm.LossValue ?? 0;
            step.FLossUnit = vm.LossUnit?.Trim();

            _db.SaveChanges();
            TempData["Success"] = "工序修改成功。";
            return RedirectToAction(nameof(Index));
        }

        // POST /PmsStep/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {

            var step = _db.t_PMS_Step.FirstOrDefault(s => s.FItemID == id);
            if (step == null) return NotFound();

            if (HasBomReference(id))
            {
                TempData["Error"] = "该工序已被产品 BOM / 生产方案 / 历史单据引用，无法删除；请先解除相关引用或改用「禁用」。";
                return RedirectToAction(nameof(Index));
            }

            _db.t_PMS_Step.Remove(step);
            _db.SaveChanges();
            TempData["Success"] = "工序删除成功。";
            return RedirectToAction(nameof(Index));
        }

        // POST /PmsStep/Disable/5 —— 禁用（FDelete=1），不再出现在新建选择器中。
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Disable(int id) => SetDisabled(id, true);

        // POST /PmsStep/Enable/5 —— 启用（FDelete=0），恢复被新建选择器选用的资格。
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Enable(int id) => SetDisabled(id, false);

        private IActionResult SetDisabled(int id, bool disabled)
        {

            var step = _db.t_PMS_Step.FirstOrDefault(s => s.FItemID == id);
            if (step == null) return NotFound();

            step.FDelete = disabled ? 1 : 0;
            _db.SaveChanges();

            TempData["Success"] = disabled ? "工序已禁用。" : "工序已启用。";
            return RedirectToAction(nameof(Index));
        }

        /// <summary>加载数据字典下拉项：5 工序类型(自制工序/委外工序)、6 工序算法(计时/计件)、9 计量单位。</summary>
        private void BindDicts()
        {
            var dicts = _db.t_ERP_DataDict
                .Where(d => d.FParentID == 5 || d.FParentID == 6 || d.FParentID == 9)
                .OrderBy(d => d.ID)
                .ToList();
            ViewBag.TypeOptions = dicts.Where(d => d.FParentID == 5).Select(d => d.DictName).ToList();
            ViewBag.AlgoOptions = dicts.Where(d => d.FParentID == 6).Select(d => d.DictName).ToList();
            ViewBag.UnitOptions = dicts.Where(d => d.FParentID == 9).Select(d => d.DictName).ToList();
        }

        private string CheckDuplicate(string name, int? excludeId)
        {
            // A null name would match rows whose FName is NULL and report "duplicate" instead
            // of the real "required" error, so reject it before touching the database.
            if (string.IsNullOrWhiteSpace(name)) return null;

            var query = _db.t_PMS_Step.AsQueryable();
            if (excludeId.HasValue)
                query = query.Where(s => s.FItemID != excludeId.Value);
            return query.Any(s => s.FName == name) ? "工序名称重复！" : null;
        }

        private bool HasBomReference(int stepId)
        {
            // 引用面 = BOM 主表 + 历史单据明细。删一个仅被历史单据引用的工序，
            // 会让那些单据的工序名解析返回空（无 FK，不崩，仅丢名），故一并拦截。
            // ⚠ 不查 v_PMS_ProductRelBom：其基表 t_ICItemCore 已于 09-12 晚「264 死模块清理」
            //   被误删，视图查询即崩（对象名 't_ICItemCore' 无效）→ 致 HasBomReference 抛异常、
            //   Delete 端点 500、工序「没删」被误读成「被拒」（假绿）。该视图是 StepProductBom
            //   的投影，t_PMS_StepProductBom 已覆盖 BOM 引用，视图检查冗余，故移除。
            return _db.t_PMS_StepProductBom.Any(b => b.FStepId == stepId)
                || _db.t_PMS_StockBillEntry.Any(e => e.FStepID == stepId)
                || _db.t_PMS_BillUseEntry.Any(e => e.FStepID == stepId);
        }

        private static StepEditViewModel Map(t_PMS_Step s)
        {
            return new StepEditViewModel
            {
                ID = s.FItemID,
                Name = s.FName,
                Type = s.FType,
                Algo = s.FAlgo,
                Hour = s.FHour ?? 0,
                Num = s.FNum ?? 0,
                SingleArti = s.FSingleArti ?? 0,
                HourArti = s.FHourArti ?? 0,
                Price = s.FPrice ?? 0,
                WorkHourUnit = s.FWorkHourUnit,
                PriceUnit = s.FPriceUnit,
                FixedLoss = s.FFixedLoss ?? 0,
                LossStandValue = s.FLossStandValue ?? 0,
                LossRate = s.FLossRate,
                LossValue = s.FLossValue ?? 0,
                LossUnit = s.FLossUnit,
            };
        }

        private static StepEditViewModel NewWithDefaults()
        {
            return new StepEditViewModel
            {
                Type = "自制工序",
                Algo = "计时",
                Hour = 0,
                Num = 0,
                SingleArti = 0,
                HourArti = 0,
                Price = 0,
                WorkHourUnit = "件",
                PriceUnit = "件",
                FixedLoss = 0,
                LossStandValue = 0,
                LossRate = "0%",
                LossValue = 0,
                LossUnit = "斤",
            };
        }
    }
}
