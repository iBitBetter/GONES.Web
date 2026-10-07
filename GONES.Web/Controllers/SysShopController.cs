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
    /// 店铺管理 —— 旧 WinForms SysManager.FrmShopInfo / FrmShopInfoEdit 的 Web 版。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 跳过 Furion 的 ModelState 短路 400，走标准 MVC 表单回显
    public class SysShopController : Controller, IMenuGuarded
    {
        private readonly GonesPgDbContext _db;
        public SysShopController(GonesPgDbContext db, MenuService menu) { _db = db; _menu = menu; }



        private readonly MenuService _menu;



        /// <summary>
        /// 写操作要求管理员（与 SysRole / SysMenu / SysDepartment 同范式）。
        /// 菜单闸门只回答"能不能进这个页面"，门店主数据属全局基础资料，不应由
        /// 拿到菜单授权的普通账号改写。
        /// </summary>
        private bool AmAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        private IActionResult DenyNonAdminWrite()
        {
            TempData["Error"] = "\u53ea\u6709\u7ba1\u7406\u5458\u624d\u80fd\u4fee\u6539\u95e8\u5e97\u4fe1\u606f\u3002";   // 只有管理员才能修改门店信息。
            return RedirectToAction(nameof(Index));
        }

        // GET /SysShop
        public IActionResult Index(int page = 1, int pageSize = 20)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);

            var query = _db.t_ERP_ShopInfo.AsQueryable()
                .OrderBy(s => s.ShopSort ?? int.MaxValue)
                .ThenBy(s => s.ID);
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

        // GET /SysShop/Create
        [HttpGet]
        public IActionResult Create()
        {
            return View("Form", new ShopInfoEditViewModel());
        }

        // POST /SysShop/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(ShopInfoEditViewModel vm)
        {
            if (!AmAdmin) return DenyNonAdminWrite();

            if (!ModelState.IsValid)
                return View("Form", vm);

            var dupMsg = CheckDuplicate(vm, null);
            if (!string.IsNullOrEmpty(dupMsg))
            {
                ModelState.AddModelError(string.Empty, dupMsg);
                return View("Form", vm);
            }

            var entity = new t_ERP_ShopInfo
            {
                ShopNumber = vm.ShopNumber,
                ShopName = vm.ShopName?.Trim(),
                Phone = vm.Phone?.Trim(),
                QQ = vm.QQ?.Trim(),
                ShopType = vm.ShopType?.Trim(),
                IsAutoDown = vm.IsAutoDown,
                Address = vm.Address?.Trim(),
                Appkey = vm.Appkey?.Trim(),
                AppSecrect = vm.AppSecrect?.Trim(),
                SessionKey = vm.SessionKey?.Trim(),
                SessionEndDate = vm.SessionEndDate,
                OrderCount = vm.OrderCount,
                SendAisle = vm.SendAisle?.Trim(),
                IsMessage = vm.IsMessage,
                ShopSort = vm.ShopSort,
                ConfigData = vm.ConfigData?.Trim(),
                Message = vm.Message?.Trim(),
                IsEnable = vm.IsEnable,
                IsMerge = false,
                IsHandMerge = false,
                IsRFM = false,
                IsImport = false,
                IsAgent = false,
            };

            _db.t_ERP_ShopInfo.Add(entity);
            _db.SaveChanges();
            TempData["Success"] = "店铺新建成功。";
            return RedirectToAction(nameof(Index));
        }

        // GET /SysShop/Edit/5
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var shop = _db.t_ERP_ShopInfo.FirstOrDefault(s => s.ID == id);
            if (shop == null) return NotFound();
            return View("Form", Map(shop));
        }

        // POST /SysShop/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, ShopInfoEditViewModel vm)
        {
            if (!AmAdmin) return DenyNonAdminWrite();
            if (id != vm.ID) return BadRequest();

            var shop = _db.t_ERP_ShopInfo.FirstOrDefault(s => s.ID == id);
            if (shop == null) return NotFound();

            if (!ModelState.IsValid)
                return View("Form", vm);

            var dupMsg = CheckDuplicate(vm, id);
            if (!string.IsNullOrEmpty(dupMsg))
            {
                ModelState.AddModelError(string.Empty, dupMsg);
                return View("Form", vm);
            }

            shop.ShopNumber = vm.ShopNumber;
            shop.ShopName = vm.ShopName?.Trim();
            shop.Phone = vm.Phone?.Trim();
            shop.QQ = vm.QQ?.Trim();
            shop.ShopType = vm.ShopType?.Trim();
            shop.IsAutoDown = vm.IsAutoDown;
            shop.Address = vm.Address?.Trim();
            shop.Appkey = vm.Appkey?.Trim();
            shop.AppSecrect = vm.AppSecrect?.Trim();
            shop.SessionKey = vm.SessionKey?.Trim();
            shop.SessionEndDate = vm.SessionEndDate;
            shop.OrderCount = vm.OrderCount;
            shop.SendAisle = vm.SendAisle?.Trim();
            shop.IsMessage = vm.IsMessage;
            shop.ShopSort = vm.ShopSort;
            shop.ConfigData = vm.ConfigData?.Trim();
            shop.Message = vm.Message?.Trim();
            shop.IsEnable = vm.IsEnable;

            _db.SaveChanges();
            TempData["Success"] = "店铺修改成功。";
            return RedirectToAction(nameof(Index));
        }

        // POST /SysShop/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            if (!AmAdmin) return DenyNonAdminWrite();

            var shop = _db.t_ERP_ShopInfo.FirstOrDefault(s => s.ID == id);
            if (shop == null) return NotFound();

            if (HasUserReference(id))
            {
                TempData["Error"] = "该店铺已关联用户，无法删除；请先解除用户关联。";
                return RedirectToAction(nameof(Index));
            }

            _db.t_ERP_ShopInfo.Remove(shop);
            _db.SaveChanges();
            TempData["Success"] = "店铺删除成功。";
            return RedirectToAction(nameof(Index));
        }

        private string CheckDuplicate(ShopInfoEditViewModel vm, int? excludeId)
        {
            var query = _db.t_ERP_ShopInfo.AsQueryable();
            if (excludeId.HasValue)
                query = query.Where(s => s.ID != excludeId.Value);

            if (query.Any(s => s.ShopNumber == vm.ShopNumber))
                return "店铺编号重复！";
            if (query.Any(s => s.ShopName == vm.ShopName.Trim()))
                return "店铺名称重复！";
            if (query.Any(s => s.ShopSort == vm.ShopSort))
                return "店铺序号重复！";
            return null;
        }

        private bool HasUserReference(int shopId)
        {
            var strId = shopId.ToString();
            // t_ERP_UserInfo.Shop 是逗号分隔的店铺 ID 列表，如 "1,2,3"
            return _db.t_ERP_UserInfo
                      .AsEnumerable()
                      .Where(u => !string.IsNullOrEmpty(u.Shop))
                      .Any(u => u.Shop.Split(',').Any(x => x.Trim() == strId));
        }

        private static ShopInfoEditViewModel Map(t_ERP_ShopInfo s)
        {
            return new ShopInfoEditViewModel
            {
                ID = s.ID,
                ShopNumber = s.ShopNumber ?? 0,
                ShopName = s.ShopName,
                Phone = s.Phone,
                QQ = s.QQ,
                ShopType = s.ShopType,
                IsAutoDown = s.IsAutoDown == true,
                Address = s.Address,
                Appkey = s.Appkey,
                AppSecrect = s.AppSecrect,
                SessionKey = s.SessionKey,
                SessionEndDate = s.SessionEndDate,
                OrderCount = s.OrderCount,
                SendAisle = s.SendAisle,
                IsMessage = s.IsMessage == true,
                ShopSort = s.ShopSort ?? 0,
                ConfigData = s.ConfigData,
                Message = s.Message,
                IsEnable = s.IsEnable == true,
            };
        }
    }
}
