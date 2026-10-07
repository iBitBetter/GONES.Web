using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;

using GONES.Web.Services;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// \u7801\u8868\u7ba1\u7406\uff08\u7cfb\u7edf\u7ba1\u7406 / t_ERP_DataDict\uff09\uff0c\u5de6\u5206\u7c7b\u53f3\u660e\u7ec6\u7684\u4e3b\u4ece\u9875\u9762\u3002
    /// \u5173\u952e\u8bed\u4e49\uff1a\u5b50\u9879 FParentID \u5b58\u7684\u662f\u7236\u5206\u7c7b\u7684 DictNo\uff0c\u4e0d\u662f ID\u3002
    /// \u7269\u7406\u5220\u9664\uff1b\u5220\u9664\u5206\u7c7b\u65f6\u82e5\u5176\u4e0b\u8fd8\u6709\u7f16\u7801\u9879\u5219\u62d2\u7edd\u3002
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]
    public class DataDictController : Controller, IMenuGuarded
    {
        private readonly GonesPgDbContext _db;
        public DataDictController(GonesPgDbContext db, MenuService menu) { _db = db; _menu = menu; }



        private readonly MenuService _menu;



        // GET /DataDict?catId=58 \uff08catId=-1 \u8868\u793a\u67e5\u770b\u672a\u5f52\u5c5e\u7f16\u7801\u9879\uff09
        public IActionResult Index(int? catId)
        {
            var all = _db.t_ERP_DataDict.OrderBy(d => d.ID).ToList();
            var categories = all.Where(d => d.FParentID == 0).ToList();

            var catNos = new HashSet<int>();
            foreach (var c in categories)
            {
                int n;
                if (int.TryParse(c.DictNo, out n)) catNos.Add(n);
            }

            var items = all.Where(d => d.FParentID != 0).ToList();
            var counts = items.GroupBy(d => d.FParentID).ToDictionary(g => g.Key, g => g.Count());
            var orphans = items.Where(d => !catNos.Contains(d.FParentID)).ToList();

            t_ERP_DataDict selected = null;
            List<t_ERP_DataDict> rightItems = new List<t_ERP_DataDict>();
            if (catId.HasValue && catId.Value == -1)
            {
                rightItems = orphans;
            }
            else
            {
                selected = catId.HasValue
                    ? categories.FirstOrDefault(c => c.ID == catId.Value)
                    : categories.FirstOrDefault();
                int selNo;
                if (selected != null && int.TryParse(selected.DictNo, out selNo))
                    rightItems = items.Where(d => d.FParentID == selNo).ToList();
            }

            ViewBag.Categories = categories;
            ViewBag.Counts = counts;
            ViewBag.Selected = selected;
            ViewBag.Items = rightItems;
            ViewBag.OrphanCount = orphans.Count;
            return View();
        }

        // GET /DataDict/Create?catId=58 \u6216 ?parentNo=5
        [HttpGet]
        public IActionResult Create(int? catId, string parentNo)
        {
            BindParentOptions(0);
            int parent;
            if (!int.TryParse(parentNo, out parent) || parent <= 0)
            {
                parent = DictNoOfCategory(catId);
            }
            return View("Form", new DataDictEditViewModel
            {
                ParentId = parent,
                IsShow = true,
            });
        }

        // POST /DataDict/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(DataDictEditViewModel vm)
        {
            if (!ValidateParent(vm) || !ValidateDictNo(vm)) { BindParentOptions(0); return View("Form", vm); }
            if (!ModelState.IsValid) { BindParentOptions(0); return View("Form", vm); }

            var entity = new t_ERP_DataDict
            {
                DictNo = vm.DictNo?.Trim(),
                DictName = vm.DictName?.Trim(),
                FParentID = vm.ParentId,
                Remark = vm.Remark?.Trim(),
                IsShow = vm.IsShow,
            };
            _db.t_ERP_DataDict.Add(entity);
            _db.SaveChanges();
            TempData["Success"] = "\u4fdd\u5b58\u6210\u529f\uff01";
            return RedirectToAction(nameof(Index), new { catId = vm.ParentId == 0 ? entity.ID : CategoryIdByNo(vm.ParentId) });
        }

        // GET /DataDict/Edit/5
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var d = _db.t_ERP_DataDict.FirstOrDefault(x => x.ID == id);
            if (d == null) return NotFound();
            BindParentOptions(ExcludedParentNo(d));
            return View("Form", new DataDictEditViewModel
            {
                ID = d.ID,
                DictNo = d.DictNo,
                DictName = d.DictName,
                ParentId = d.FParentID,
                Remark = d.Remark,
                IsShow = (d.IsShow ?? true),
            });
        }

        // POST /DataDict/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, DataDictEditViewModel vm)
        {
            if (id != vm.ID) return BadRequest();
            var d = _db.t_ERP_DataDict.FirstOrDefault(x => x.ID == id);
            if (d == null) return NotFound();
            if (!ValidateParent(vm) || !ValidateDictNo(vm, id)) { BindParentOptions(ExcludedParentNo(d)); return View("Form", vm); }
            if (!ModelState.IsValid) { BindParentOptions(ExcludedParentNo(d)); return View("Form", vm); }

            d.DictNo = vm.DictNo?.Trim();
            d.DictName = vm.DictName?.Trim();
            d.FParentID = vm.ParentId;
            d.Remark = vm.Remark?.Trim();
            d.IsShow = vm.IsShow;
            _db.SaveChanges();
            TempData["Success"] = "\u4fee\u6539\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index), new { catId = vm.ParentId == 0 ? d.ID : CategoryIdByNo(vm.ParentId) });
        }

        // POST /DataDict/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            var d = _db.t_ERP_DataDict.FirstOrDefault(x => x.ID == id);
            if (d == null) return NotFound();

            // \u5b50\u9879 FParentID \u5b58\u7684\u662f\u5206\u7c7b DictNo\uff0c\u56e0\u6b64\u6309\u7f16\u7801\u68c0\u67e5\u8be5\u5206\u7c7b\u4e0b\u662f\u5426\u8fd8\u6709\u5b50\u9879
            int catNo;
            if (d.FParentID == 0 && int.TryParse(d.DictNo, out catNo)
                && _db.t_ERP_DataDict.Any(x => x.FParentID == catNo))
            {
                TempData["Error"] = "\u8be5\u5206\u7c7b\u4e0b\u5b58\u5728\u7f16\u7801\u9879\uff0c\u8bf7\u5148\u5220\u9664\u5b50\u9879\uff01";
                return RedirectToAction(nameof(Index), new { catId = d.ID });
            }

            var backCatId = d.FParentID == 0 ? d.ID : CategoryIdByNo(d.FParentID);
            _db.t_ERP_DataDict.Remove(d);
            _db.SaveChanges();
            TempData["Success"] = "\u5220\u9664\u6210\u529f\u3002";
            return RedirectToAction(nameof(Index), new { catId = backCatId });
        }

        private bool ValidateParent(DataDictEditViewModel vm, int? excludeId = null)
        {
            if (vm.ParentId == 0) return true;
            var parent = _db.t_ERP_DataDict.FirstOrDefault(x => x.FParentID == 0 && x.DictNo == vm.ParentId.ToString());
            if (parent == null)
            {
                ModelState.AddModelError(nameof(vm.ParentId), "\u4e0a\u7ea7\u5fc5\u987b\u662f\u4e00\u4e2a\u5206\u7c7b\u3002");
                return false;
            }
            return true;
        }

        /// <summary>
        /// DictNo must be unique inside its scope: children store their parent's DictNo in
        /// FParentID, so a duplicated number makes the parent of every child ambiguous.
        /// </summary>
        private bool ValidateDictNo(DataDictEditViewModel vm, int? excludeId = null)
        {
            var no = (vm.DictNo ?? "").Trim();
            if (no.Length == 0) return true;

            var dup = _db.t_ERP_DataDict.Any(x => x.FParentID == vm.ParentId
                                                  && x.DictNo == no
                                                  && (!excludeId.HasValue || x.ID != excludeId.Value));
            if (!dup) return true;

            ModelState.AddModelError(nameof(vm.DictNo),
                "\u7f16\u53f7 " + no + " \u5728\u540c\u4e00\u5206\u7c7b\u4e0b\u5df2\u5b58\u5728\uff0c\u8bf7\u6362\u4e00\u4e2a\u3002");   // 编号 X 在同一分类下已存在，请换一个。
            return false;
        }

        /// <summary>\u5206\u7c7b\u81ea\u8eab\u7684 DictNo \u503c\uff08\u7528\u4e8e\u7f16\u8f91\u65f6\u4ece\u4e0b\u62c9\u4e2d\u6392\u9664\u81ea\u8eab\uff09\u3002</summary>
        private int ExcludedParentNo(t_ERP_DataDict d)
        {
            int no;
            return (d.FParentID == 0 && int.TryParse(d.DictNo, out no)) ? no : 0;
        }

        /// <summary>\u7ed9\u5b9a\u5206\u7c7b ID\uff0c\u8fd4\u56de\u5b83\u7684 DictNo\uff08\u4f5c\u4e3a\u5b50\u9879 FParentID \u7528\uff09\u3002</summary>
        private int DictNoOfCategory(int? catId)
        {
            if (!catId.HasValue || catId.Value <= 0) return 0;
            var c = _db.t_ERP_DataDict.FirstOrDefault(x => x.ID == catId.Value && x.FParentID == 0);
            int no;
            return (c != null && int.TryParse(c.DictNo, out no)) ? no : 0;
        }

        /// <summary>\u7ed9\u5b9a\u5206\u7c7b DictNo\uff0c\u8fd4\u56de\u8be5\u5206\u7c7b\u7684 ID\uff08\u7528\u4e8e\u4fdd\u5b58\u540e\u56de\u5230\u5bf9\u5e94\u5206\u7c7b\u9875\uff09\u3002</summary>
        private int CategoryIdByNo(int no)
        {
            var c = _db.t_ERP_DataDict.FirstOrDefault(x => x.FParentID == 0 && x.DictNo == no.ToString());
            return c == null ? 0 : c.ID;
        }

        private void BindParentOptions(int excludeNo)
        {
            var categories = _db.t_ERP_DataDict.Where(d => d.FParentID == 0).OrderBy(d => d.ID).ToList();
            ViewBag.ParentOptions = DataDictNode.BuildCategoryOptions(categories, excludeNo);
        }
    }
}
