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
    /// 商品分类 —— 旧 WinForms ProductManager.FrmProductClass 的 Web 版（SYS-系统管理 / t_ERP_ITEMCLASS）。
    /// 树形管理，最多 3 级（ClassLevel 1=根 2=大类 3=末级）。
    /// 删除与旧系统一致为物理删除；有子类别或有商品引用时拒绝删除。
    /// 保存时推导 ParentClassCode/FullName/FullCode/ClassLevel，与旧 FrmProductClassEdit 逻辑一致。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 跳过 Furion 的 ModelState 短路 400，走标准 MVC 表单回显
    public class PmsItemClassController : Controller, IMenuGuarded
    {
        private readonly GonesPgDbContext _db;
        public PmsItemClassController(GonesPgDbContext db, MenuService menu) { _db = db; _menu = menu; }



        private readonly MenuService _menu;



        // GET /PmsItemClass
        public IActionResult Index()
        {

            var all = LoadAll();
            ViewBag.Tree = ItemClassNode.BuildTree(all);
            // cpfl_id is nullable: uncategorised items group under a NULL key and g.Key.Value
            // would throw InvalidOperationException, taking the whole page down.
            ViewBag.ItemCount = _db.t_ERP_ITEM
                .Where(i => i.CategoryId != null)
                .GroupBy(i => i.CategoryId.Value)
                .ToDictionary(g => g.Key, g => g.Count());
            return View();
        }

        // GET /PmsItemClass/Create?parentId=158
        [HttpGet]
        public IActionResult Create(int? parentId)
        {
            BindParentOptions(null);
            return View("Form", new ItemClassEditViewModel
            {
                ParentId = parentId.HasValue && parentId.Value > 0 ? parentId : null,
                Enabled = true,
            });
        }

        // POST /PmsItemClass/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(ItemClassEditViewModel vm)
        {

            var parent = ValidateParent(vm);
            if (parent == null)
            {
                BindParentOptions(null);
                return View("Form", vm);
            }

            var rawName = vm.Name?.Trim();
            var fullName = BuildQmmc(parent, rawName);
            var entity = new t_ERP_ITEMCLASS
            {
                ClassName = rawName,
                FParentID = parent.ID,
                ParentClassCode = parent.ID.ToString(),
                FullName = fullName,
                FullCode = null,            // 插入拿到 ID 后回填（与旧逻辑一致）
                ClassLevel = (parent.ClassLevel ?? 1) + 1,
                UseStatus = vm.Enabled ? 1 : 0,
                TopClassName = DeriveLb(fullName),
            };
            _db.t_ERP_ITEMCLASS.Add(entity);
            _db.SaveChanges();

            entity.FullCode = BuildQmdm(parent, entity.ID);
            _db.SaveChanges();

            TempData["Success"] = "商品分类新增成功！";
            return RedirectToAction(nameof(Index));
        }

        // GET /PmsItemClass/Edit/5
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var cls = _db.t_ERP_ITEMCLASS.FirstOrDefault(c => c.ID == id);
            if (cls == null) return NotFound();
            BindParentOptions(id);
            return View("Form", new ItemClassEditViewModel
            {
                ID = cls.ID,
                Name = cls.ClassName,
                ParentId = cls.FParentID,
                Enabled = (cls.UseStatus ?? 1) == 1,
            });
        }

        // POST /PmsItemClass/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, ItemClassEditViewModel vm)
        {
            if (id != vm.ID) return BadRequest();

            var cls = _db.t_ERP_ITEMCLASS.FirstOrDefault(c => c.ID == id);
            if (cls == null) return NotFound();

            var parent = ValidateParent(vm, id);
            if (parent == null)
            {
                BindParentOptions(id);
                return View("Form", vm);
            }

            // 按新上级重算 ClassLevel/FullName/FullCode/ParentClassCode/TopClassName（规则见类注释；旧系统不级联更新子孙节点，此处保持一致）
            cls.ClassName = vm.Name?.Trim();
            cls.FParentID = parent.ID;
            cls.ParentClassCode = parent.ID.ToString();
            cls.FullName = BuildQmmc(parent, vm.Name?.Trim());
            cls.FullCode = BuildQmdm(parent, cls.ID);
            cls.ClassLevel = (parent.ClassLevel ?? 1) + 1;
            cls.UseStatus = vm.Enabled ? 1 : 0;
            cls.TopClassName = DeriveLb(cls.FullName);
            _db.SaveChanges();

            TempData["Success"] = "商品分类修改成功。";
            return RedirectToAction(nameof(Index));
        }

        // POST /PmsItemClass/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {

            var cls = _db.t_ERP_ITEMCLASS.FirstOrDefault(c => c.ID == id);
            if (cls == null) return NotFound();

            if (_db.t_ERP_ITEMCLASS.Any(c => c.FParentID == id))
            {
                TempData["Error"] = "该分类下存在子类别，请先删除子类！";
                return RedirectToAction(nameof(Index));
            }
            if (_db.t_ERP_ITEM.Any(i => i.CategoryId == id))
            {
                TempData["Error"] = "该分类下已有商品，请先删除或移走商品！";
                return RedirectToAction(nameof(Index));
            }

            _db.t_ERP_ITEMCLASS.Remove(cls);
            _db.SaveChanges();
            TempData["Success"] = $"分类“{cls.ClassName}”删除成功。";
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// FullName（全称）：父级为根(所有分类, ClassLevel=1)时只取本名，否则 父FullName_本名。
        /// 根节点名称不进入路径（与库内一致：产成品 FullName=产成品，而非 所有分类_产成品）。
        /// </summary>
        private static string BuildQmmc(t_ERP_ITEMCLASS parent, string name)
            => (parent.ClassLevel == 1)
                ? (name ?? "")
                : ((parent.FullName ?? parent.ClassName) + "_" + name);

        /// <summary>
        /// FullCode（点分ID路径）：父级为根时前缀用根ID("1")，否则用父FullCode；再拼 "." + 当前ID。
        /// 例：产成品=1.2，山核桃系列=1.2.10。根节点自身 FullCode 固定为 "0"（库初始化，不经此生成）。
        /// </summary>
        private static string BuildQmdm(t_ERP_ITEMCLASS parent, int id)
            => ((parent.ClassLevel == 1) ? parent.ID.ToString() : (parent.FullCode ?? "")) + "." + id;

        /// <summary>
        /// TopClassName：FullName 含 '_' 取第一段（一级类别名），否则等于 FullName。
        /// 根节点特殊值"全部"为手动维护，不经此生成。
        /// </summary>
        private static string DeriveLb(string fullName)
            => string.IsNullOrEmpty(fullName) ? fullName : (fullName.Contains("_") ? fullName.Split('_')[0] : fullName);

        private List<t_ERP_ITEMCLASS> LoadAll()
            => _db.t_ERP_ITEMCLASS.OrderBy(c => c.ID).ToList();

        private void BindParentOptions(int? excludeId)
        {
            var all = LoadAll();
            var tree = ItemClassNode.BuildTree(all);
            ViewBag.ParentOptions = ItemClassNode.BuildParentOptions(tree, excludeId ?? 0);
        }

        /// <summary>校验上级类别：必须存在、非根节点(ClassLevel=1 不能再挂子级？根 ClassLevel=1 可以挂大类，故要求 ClassLevel&lt;=2)。</summary>
        private t_ERP_ITEMCLASS ValidateParent(ItemClassEditViewModel vm, int? selfId = null)
        {
            if (!ModelState.IsValid || vm.ParentId == null)
                return null;

            var parent = _db.t_ERP_ITEMCLASS.FirstOrDefault(c => c.ID == vm.ParentId.Value);
            if (parent == null)
            {
                ModelState.AddModelError(nameof(vm.ParentId), "上级类别不存在。");
                return null;
            }
            if ((parent.ClassLevel ?? 1) >= 3)
            {
                ModelState.AddModelError(nameof(vm.ParentId), "末级分类(ClassLevel=3)下不能再新增子分类。");
                return null;
            }
            // Cycle guard: making a node its own parent, or moving it under one of its own
            // descendants, would detach the whole subtree from the tree (invisible + undeletable)
            // and can hang the recursion that renders it.
            if (selfId.HasValue)
            {
                if (vm.ParentId.Value == selfId.Value)
                {
                    ModelState.AddModelError(nameof(vm.ParentId), "上级类别不能是自己。");
                    return null;
                }
                var descendants = ItemClassNode.CollectDescendants(LoadAll(), selfId.Value);
                if (descendants.Contains(vm.ParentId.Value))
                {
                    ModelState.AddModelError(nameof(vm.ParentId), "不能把上级类别设为自己的下级分类。");
                    return null;
                }
            }
            return parent;
        }
    }
}
