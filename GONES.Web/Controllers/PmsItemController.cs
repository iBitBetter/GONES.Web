using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Helpers;
using GONES.Web.Models;
using Microsoft.EntityFrameworkCore;

using GONES.Web.Services;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// 商品管理 —— 旧 WinForms ProductManager.FrmProduct 的 Web 版（SYS-系统管理 / t_ERP_ITEM）。
    /// 左侧分类树 + 右侧商品列表（编码/简称/规格/条码过滤，默认仅显示已发布且启用的商品，与旧窗体一致）。
    /// 旧系统无删除功能，只有停用/启用，此处保持一致。
    /// 组合商品明细（t_ERP_ITEMGROUP）：zh_zt=true 时保存明细（先删 combo_item_guid=本商品 guid 的旧行再插入，与旧窗体一致）。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 跳过 Furion 的 ModelState 短路 400，走标准 MVC 表单回显
    public class PmsItemController : Controller, IMenuGuarded
    {
        private readonly GonesPgDbContext _db;
        public PmsItemController(GonesPgDbContext db, MenuService menu) { _db = db; _menu = menu; }



        private readonly MenuService _menu;



        // GET /PmsItem?classId=&cpbm=&cpjc=&cpgg=&txm=&showStopped=&showUnpublished=
        public IActionResult Index(int? classId, string cpbm, string cpjc, string cpgg, string txm,
            bool showStopped = false, bool showUnpublished = false, int page = 1, int pageSize = 20)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);

            var all = _db.t_ERP_ITEMCLASS.OrderBy(c => c.ID).ToList();
            ViewBag.Tree = ItemClassNode.BuildTree(all);
            var classNames = all.ToDictionary(c => c.ID, c => c.ClassName);
            ViewBag.ClassNames = classNames;

            var query = _db.t_ERP_ITEM.AsQueryable();
            // 默认只显示已发布(fb_zt=1)且启用(ty_zt=1)的商品（ty_zt 已改正向：1=启用/0=停用）
            query = showUnpublished ? query : query.Where(i => i.IsPublishControlled == true);
            query = showStopped ? query : query.Where(i => i.IsEnabled == true);

            cpbm = cpbm?.Trim(); cpjc = cpjc?.Trim(); cpgg = cpgg?.Trim(); txm = txm?.Trim();
            if (!string.IsNullOrEmpty(cpbm)) query = query.Where(i => i.ItemCode.Contains(cpbm));
            if (!string.IsNullOrEmpty(cpjc)) query = query.Where(i => i.ItemShortName.Contains(cpjc));
            if (!string.IsNullOrEmpty(cpgg)) query = query.Where(i => i.ItemSpec.Contains(cpgg));
            if (!string.IsNullOrEmpty(txm)) query = query.Where(i => i.Barcode.Contains(txm));

            if (classId.HasValue && classId.Value > 0)
            {
                var scope = ItemClassNode.CollectDescendants(all, classId.Value);
                query = query.Where(i => i.CategoryId != null && scope.Contains(i.CategoryId.Value));
                ViewBag.CurrentClassName = classNames.TryGetValue(classId.Value, out var nm) ? nm : $"分类{classId}";
            }
            ViewBag.CurrentClassId = classId ?? 0;

            int total = query.Count();
            page = PagerViewModel.ClampPage(page, total, pageSize);
            var list = query.OrderBy(i => i.ID).Skip((page - 1) * pageSize).Take(pageSize).ToList();

            ViewBag.Filter = new Dictionary<string, string>
            {
                ["cpbm"] = cpbm ?? "", ["cpjc"] = cpjc ?? "", ["cpgg"] = cpgg ?? "", ["txm"] = txm ?? "",
            };
            ViewBag.ShowStopped = showStopped;
            ViewBag.ShowUnpublished = showUnpublished;

            var pager = new PagerViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                Action = "Index",
            };
            // 翻页/切每页条数时保留过滤条件
            var extra = new Dictionary<string, object>();
            if (classId.HasValue && classId.Value > 0) extra["classId"] = classId.Value;
            if (!string.IsNullOrEmpty(cpbm)) extra["cpbm"] = cpbm;
            if (!string.IsNullOrEmpty(cpjc)) extra["cpjc"] = cpjc;
            if (!string.IsNullOrEmpty(cpgg)) extra["cpgg"] = cpgg;
            if (!string.IsNullOrEmpty(txm)) extra["txm"] = txm;
            if (showStopped) extra["showStopped"] = true;
            if (showUnpublished) extra["showUnpublished"] = true;
            if (extra.Count > 0) pager.ExtraValues = extra;
            ViewBag.Pager = pager;

            return View(list);
        }

        // GET /PmsItem/NextSeq?classId=X —— 返回该分类的 FullCode 和下一可用商品序号
        // 序号规则：cpbm = {分类FullCode}.{序号}，序号从1递增，不与任何已有商品（含已停用）重复
        [HttpGet]
        public IActionResult NextSeq(int classId)
        {

            var cls = _db.t_ERP_ITEMCLASS.FirstOrDefault(c => c.ID == classId);
            if (cls == null) return Json(new { error = "分类不存在" });

            var prefix = (cls.FullCode ?? "") + ".";
            // 查该分类下所有商品（含停用），提取 cpbm 中 prefix 之后的序号部分
            var items = _db.t_ERP_ITEM
                .Where(i => i.CategoryId == classId && i.ItemCode != null && i.ItemCode.StartsWith(prefix))
                .Select(i => i.ItemCode.Substring(prefix.Length))
                .ToList();

            int maxSeq = 0;
            foreach (var s in items)
            {
                if (int.TryParse(s, out var seq) && seq > maxSeq)
                    maxSeq = seq;
            }

            return Json(new { qmdm = cls.FullCode, qmmc = cls.FullName, lb = cls.TopClassName, nextSeq = maxSeq + 1 });
        }

        // GET /PmsItem/ToPinyin?text=XXX —— 调用 PKT.NPinyin 生成拼音码（用于商品全称→拼音码自动填充）
        [HttpGet]
        public IActionResult ToPinyin(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Json(new { pinyin = "" });
            // 原 SQL Server 标量函数 dbo.f_GetPinYin 已用 PKT.NPinyin（C# 拼音库）取代，去除 DB 依赖。
            var py = PinyinHelper.GetPinYinCode(text.Trim());
            return Json(new { pinyin = py ?? "" });
        }

        // GET /PmsItem/Create?classId=
        [HttpGet]
        public IActionResult Create(int? classId)
        {
            BindDicts();
            BindClassOptions(null);
            BindWarehouseOptions();
            BindGroupExtras(new List<ItemGroupInput>());
            var vmCreate = NewWithDefaults(classId);
            ViewBag.IsModal = IsModalRequest();
            return IsModalRequest() ? PartialView("Form", vmCreate) : View("Form", vmCreate);
        }

        // POST /PmsItem/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(ItemEditViewModel vm)
        {

            // 商品编码由服务端统一生成（前端只读），防篡改；Max+1 逻辑必须包在进程级锁内防并发重号
            var clsPre = _db.t_ERP_ITEMCLASS.FirstOrDefault(c => c.ID == vm.ClassId);
            if (clsPre != null)
                vm.Cpbm = GenerateNextCpbm(clsPre);

            var cls = ValidateAndCheck(vm, null);
            if (cls == null)
            {
                BindDicts();
                BindClassOptions(null);
                BindWarehouseOptions();
                BindGroupExtras(vm.GroupItems);
                ViewBag.IsModal = IsAjaxRequest();
                if (IsAjaxRequest())
                {
                    Response.StatusCode = 400;
                    return PartialView("Form", vm);
                }
                return View("Form", vm);
            }

            var entity = new t_ERP_ITEM
            {
                RowGuid = Guid.NewGuid().ToString("D"),
                ItemCode = vm.Cpbm?.Trim(),
                ItemFullName = vm.Cpqc?.Trim(),
                ItemShortName = vm.Cpjc?.Trim(),
                ItemPinyinCode = vm.Cppym?.Trim(),
                ItemSpec = vm.Cpgg?.Trim(),
                CategoryId = cls.ID,
                CategoryCode = cls.FullCode,
                Barcode = vm.Txm?.Trim(),
                Attr1 = vm.Sx,
                Attr2 = vm.Sx1,
                PackageUnit = vm.Wlbzdw,
                PackageRate = vm.Wlbzl,
                BaseUnit = vm.Wljbdw,
                NetContent = vm.Jhl,
                TotalWeight = vm.Zzl,
                RetailPrice = vm.Lsdj,
                FactoryPrice = vm.Ccdj ?? vm.Lsdj,     // 与旧系统一致：留空取零售单价
                FranchisePrice = vm.Jmdj ?? vm.Lsdj,
                CostPrice = vm.Cbdj,
                DistributorPrice = vm.Jxdj,
                TaxRateText = vm.Sl,
                TaxRateValue = ParseSlSize(vm.Sl),
                ExternalCode = vm.Wbbm?.Trim(),
                ProductCategory = cls.TopClassName,         // 自动取所选商品分类的一级类别(TopClassName)，不由前端提交
                Location = vm.Cphw?.Trim(),
                Remark = vm.Bz?.Trim(),
                IsStockControlled = vm.KcZt,
                IsPublishControlled = vm.FbZt,
                IsCombo = vm.ZhZt,
                IsGiftBoxControlled = vm.DlbZt,
                IsGiftControlled = vm.ZpZt,
                IsWalnutMilk = vm.KdZt,
                IsEnabled = vm.Enabled,             // ty_zt 正向：1=启用/0=停用
                VirtualStock = 0M,                      // 虚拟库存默认 0
                WarehouseId = vm.Ckid,                 // 仓库 ID = t_ERP_Department.ID
            };
            _db.t_ERP_ITEM.Add(entity);
            SaveWithGroupItems(entity, vm);

            if (IsAjaxRequest())
                return Json(new { ok = true, msg = $"商品“{entity.ItemShortName}”新增成功！" });
            TempData["Success"] = $"商品“{entity.ItemShortName}”新增成功！";
            return RedirectToAction(nameof(Index));
        }

        // GET /PmsItem/Edit/5
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var item = _db.t_ERP_ITEM.FirstOrDefault(i => i.ID == id);
            if (item == null) return NotFound();
            BindDicts();
            BindClassOptions(item.CategoryId);
            BindWarehouseOptions();
            var vm = Map(item);
            // 加载已有组合明细（t_ERP_ITEMGROUP.combo_item_guid = 本商品 guid），与旧窗体 gridControl1 一致
            vm.GroupItems = _db.t_ERP_ITEMGROUP
                .Where(g => g.ComboItemGuid == item.RowGuid)
                .ToList()
                .Select(g => new ItemGroupInput { Cpbm = g.ComponentItemCode, Jbsl = g.BaseQuantity ?? 1, Dj = g.UnitPrice })
                .ToList();
            BindGroupExtras(vm.GroupItems);
            ViewBag.IsModal = IsModalRequest();
            return IsModalRequest() ? PartialView("Form", vm) : View("Form", vm);
        }

        // POST /PmsItem/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, ItemEditViewModel vm)
        {
            if (id != vm.ID) return BadRequest();

            var item = _db.t_ERP_ITEM.FirstOrDefault(i => i.ID == id);
            if (item == null) return NotFound();

            var cls = ValidateAndCheck(vm, id);
            if (cls == null)
            {
                BindDicts();
                BindClassOptions(item.CategoryId);
                BindWarehouseOptions();
                BindGroupExtras(vm.GroupItems);
                ViewBag.IsModal = IsAjaxRequest();
                if (IsAjaxRequest())
                {
                    Response.StatusCode = 400;
                    return PartialView("Form", vm);
                }
                return View("Form", vm);
            }

            // 与旧系统一致：编辑时商品编码只读，不改 cpbm/guid
            item.ItemFullName = vm.Cpqc?.Trim();
            item.ItemShortName = vm.Cpjc?.Trim();
            item.ItemPinyinCode = vm.Cppym?.Trim();
            item.ItemSpec = vm.Cpgg?.Trim();
            item.CategoryId = cls.ID;
            item.Barcode = vm.Txm?.Trim();
            item.Attr1 = vm.Sx;
            item.Attr2 = vm.Sx1;
            item.PackageUnit = vm.Wlbzdw;
            item.PackageRate = vm.Wlbzl;
            item.BaseUnit = vm.Wljbdw;
            item.NetContent = vm.Jhl;
            item.TotalWeight = vm.Zzl;
            item.RetailPrice = vm.Lsdj;
            item.FactoryPrice = vm.Ccdj ?? vm.Lsdj;
            item.FranchisePrice = vm.Jmdj ?? vm.Lsdj;
            item.CostPrice = vm.Cbdj;
            item.DistributorPrice = vm.Jxdj;
            item.TaxRateText = vm.Sl;
            item.TaxRateValue = ParseSlSize(vm.Sl);
            item.ExternalCode = vm.Wbbm?.Trim();
            item.ProductCategory = cls.TopClassName;        // 自动取所选商品分类的一级类别(TopClassName)，不由前端提交
            item.Location = vm.Cphw?.Trim();
            item.Remark = vm.Bz?.Trim();
            item.IsStockControlled = vm.KcZt;
            item.IsPublishControlled = vm.FbZt;
            item.IsCombo = vm.ZhZt;
            item.IsGiftBoxControlled = vm.DlbZt;
            item.IsGiftControlled = vm.ZpZt;
            item.IsWalnutMilk = vm.KdZt;
            item.IsEnabled = vm.Enabled;        // ty_zt 正向：1=启用/0=停用
            item.WarehouseId = vm.Ckid;            // 仓库 ID = t_ERP_Department.ID
            _db.SaveChanges();
            SaveWithGroupItems(item, vm, itemAlreadySaved: true);

            if (IsAjaxRequest())
                return Json(new { ok = true, msg = $"商品“{item.ItemShortName}”修改成功。" });
            TempData["Success"] = $"商品“{item.ItemShortName}”修改成功。";
            return RedirectToAction(nameof(Index));
        }

        // POST /PmsItem/ToggleStop/5?stop=true —— 停用/启用（旧系统无删除，仅此二态切换）
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ToggleStop(int id, bool stop)
        {

            var item = _db.t_ERP_ITEM.FirstOrDefault(i => i.ID == id);
            if (item == null) return NotFound();

            item.IsEnabled = !stop;             // ty_zt 正向：停用=0，启用=1
            _db.SaveChanges();
            TempData["Success"] = stop ? $"商品“{item.ItemShortName}”已停用。" : $"商品“{item.ItemShortName}”已启用。";
            return RedirectToAction(nameof(Index));
        }

        private static readonly object CpbmLock = new object();

        /// <summary>
        /// 生成下一商品编码（{分类FullCode}.{序号}），与 NextSeq 同规则。
        /// 「读 max → 生成」整段包进程级锁，防止并发新建重号；编码不由前端提交。
        /// </summary>
        private string GenerateNextCpbm(t_ERP_ITEMCLASS cls)
        {
            lock (CpbmLock)
            {
                var prefix = (cls.FullCode ?? "") + ".";
                var items = _db.t_ERP_ITEM
                    .Where(i => i.CategoryId == cls.ID && i.ItemCode != null && i.ItemCode.StartsWith(prefix))
                    .Select(i => i.ItemCode.Substring(prefix.Length))
                    .ToList();
                int maxSeq = 0;
                foreach (var s in items)
                {
                    if (int.TryParse(s, out var seq) && seq > maxSeq)
                        maxSeq = seq;
                }
                return prefix + (maxSeq + 1);
            }
        }

        private List<t_ERP_ITEMCLASS> LoadAll()
            => _db.t_ERP_ITEMCLASS.OrderBy(c => c.ID).ToList();

        /// <summary>
        /// 组合明细页所需数据：可选商品列表（启用+已发布，产品类别=产成品 且非组合商品）+
        /// 明细行的展示列（简称/规格/条码/属性，按 Cpbm 回查，GET 与校验失败回显共用）。
        /// </summary>
        private void BindGroupExtras(List<ItemGroupInput> groupItems)
        {
            ViewBag.PickerItems = _db.t_ERP_ITEM
                .Where(i => i.IsEnabled == true && i.IsPublishControlled == true
                         && i.ProductCategory == "产成品" && i.IsCombo != true)
                .OrderBy(i => i.ItemCode)
                .Select(i => new PickerItem { Cpbm = i.ItemCode, Cpjc = i.ItemShortName, Cpgg = i.ItemSpec, Txm = i.Barcode, Sx = i.Attr1, Sx1 = i.Attr2, Lsdj = i.RetailPrice })
                .ToList();

            var cpbms = (groupItems ?? new List<ItemGroupInput>())
                .Where(g => !string.IsNullOrWhiteSpace(g?.Cpbm))
                .Select(g => g.Cpbm.Trim())
                .Distinct()
                .ToList();
            var rows = _db.t_ERP_ITEM
                .Where(i => cpbms.Contains(i.ItemCode))
                .Select(i => new PickerItem { Cpbm = i.ItemCode, Cpjc = i.ItemShortName, Cpgg = i.ItemSpec, Txm = i.Barcode, Sx = i.Attr1, Sx1 = i.Attr2, Lsdj = i.RetailPrice })
                .ToList();
            // Group by key instead of ToDictionary: legacy migrated rows can carry a duplicate
            // cpbm and ToDictionary would throw ArgumentException, 500-ing the whole edit page.
            var groupRowInfo = new Dictionary<string, PickerItem>();
            foreach (var r in rows) groupRowInfo[r.Cpbm] = r;
            ViewBag.GroupRowInfo = groupRowInfo;
        }

        /// <summary>校验组合明细：勾选组合商品时至少一行、编码存在、不能选自己、不重复（与旧窗体一致）。</summary>
        private void ValidateGroupItems(ItemEditViewModel vm, string selfCpbm)
        {
            if (!vm.ZhZt) return;

            var rows = (vm.GroupItems ?? new List<ItemGroupInput>())
                .Where(g => !string.IsNullOrWhiteSpace(g?.Cpbm))
                .ToList();
            if (rows.Count == 0)
            {
                ModelState.AddModelError(nameof(vm.GroupItems), "组合商品为空，不能保存！");
                return;
            }

            var cpbms = rows.Select(r => r.Cpbm.Trim()).ToList();
            if (cpbms.Distinct(StringComparer.OrdinalIgnoreCase).Count() != cpbms.Count)
                ModelState.AddModelError(nameof(vm.GroupItems), "组合明细中存在重复商品！");

            if (cpbms.Any(c => string.Equals(c, selfCpbm?.Trim(), StringComparison.OrdinalIgnoreCase)))
                ModelState.AddModelError(nameof(vm.GroupItems), "组合明细不能包含商品本身！");

            var found = _db.t_ERP_ITEM.Where(i => cpbms.Contains(i.ItemCode)).Select(i => i.ItemCode).ToList();
            var missing = cpbms.FirstOrDefault(c => !found.Contains(c, StringComparer.OrdinalIgnoreCase));
            if (missing != null)
                ModelState.AddModelError(nameof(vm.GroupItems), $"子商品“{missing}”不存在！");
        }

        /// <summary>
        /// 保存组合明细（与旧窗体一致）：仅勾选组合商品时处理；先删 combo_item_guid=本商品 guid 的旧行，再按表单插入。
        /// IsRepairStatus 固定 false（原取子商品 fhwx_zt，该列已删除），DepartmentId 置空（旧窗体新增时亦不填）。
        /// </summary>
        private void SaveWithGroupItems(t_ERP_ITEM entity, ItemEditViewModel vm, bool itemAlreadySaved = false)
        {
            if (!vm.ZhZt)
            {
                if (!itemAlreadySaved) _db.SaveChanges();
                return;
            }

            using (var tx = _db.Database.BeginTransaction())
            {
                if (!itemAlreadySaved) _db.SaveChanges();

                var oldRows = _db.t_ERP_ITEMGROUP.Where(g => g.ComboItemGuid == entity.RowGuid).ToList();
                _db.t_ERP_ITEMGROUP.RemoveRange(oldRows);

                foreach (var gi in (vm.GroupItems ?? new List<ItemGroupInput>()).Where(g => !string.IsNullOrWhiteSpace(g?.Cpbm)))
                {
                    var child = _db.t_ERP_ITEM.FirstOrDefault(i => i.ItemCode == gi.Cpbm.Trim());
                    if (child == null) continue;    // ValidateAndCheck 已拦截，防御性跳过
                    _db.t_ERP_ITEMGROUP.Add(new t_ERP_ITEMGROUP
                    {
                        ComponentItemGuid = child.RowGuid,
                        ComponentItemCode = child.ItemCode,
                        ComboItemGuid = entity.RowGuid,
                        ComboItemCode = entity.ItemCode,
                        BaseQuantity = gi.Jbsl,
                        UnitPrice = gi.Dj ?? child.RetailPrice,   // 未填单价时取子商品零售价
                        DepartmentId = null,
                        IsRepairStatus = false,   // 原取子商品 fhwx_zt，该列已从 t_ERP_ITEM 删除
                    });
                }
                _db.SaveChanges();
                tx.Commit();
            }
        }

        private void BindClassOptions(int? selectedId)
        {
            var all = LoadAll();
            var tree = ItemClassNode.BuildTree(all);
            var options = new List<SelectOption>();
            void Walk(List<ItemClassNode> nodes, int depth)
            {
                foreach (var n in nodes)
                {
                    options.Add(new SelectOption
                    {
                        Id = n.Id,
                        Text = new string('\u3000', depth) + n.Name,
                        Indent = depth,
                    });
                    Walk(n.Children, depth + 1);
                }
            }
            Walk(tree, 0);
            ViewBag.ClassOptions = options;
            ViewBag.SelectedClassId = selectedId;
            // 分类ID → 一级类别(lb) 映射，前端据此动态切换条形码必填红星（lb=产成品时必填）
            // lb comes from t_ERP_ITEMCLASS (operator-maintained). System.Text.Json already
            // escapes '<' to \u003C by default, so a "</script>" value cannot close the
            // <script> block; the explicit Replace is belt-and-braces in case the serializer
            // is ever switched to UnsafeRelaxedJsonEscaping. Do not remove either guard.
            ViewBag.ClassLbJson = System.Text.Json.JsonSerializer.Serialize(
                all.ToDictionary(c => c.ID.ToString(), c => c.TopClassName ?? ""))
                .Replace("</", "<\\/");
            ViewBag.CurrentLb = selectedId.HasValue
                ? (all.FirstOrDefault(c => c.ID == selectedId.Value)?.TopClassName ?? "")
                : "";
        }

        /// <summary>仓库下拉：t_ERP_Department.IsDEP==1（仓库）且启用，写入 t_ERP_ITEM.WarehouseId。IsDEP 语义：1=仓库 2=车间 0=管理部门。</summary>
        private void BindWarehouseOptions()
        {
            ViewBag.CkOptions = _db.t_ERP_Department
                .Where(d => d.IsDEP == 1 && d.Status == 1)
                .OrderBy(d => d.ID)
                .Select(d => new SelectOption { Id = d.ID, Text = d.DEPName, Indent = 0 })
                .ToList();
        }

        /// <summary>加载数据字典下拉项：1 商品属性、2 税率、9 计量单位（包装单位/基本单位）。</summary>
        private void BindDicts()
        {
            var dicts = _db.t_ERP_DataDict
                .Where(d => d.FParentID == 1 || d.FParentID == 2 || d.FParentID == 9)
                .OrderBy(d => d.ID)
                .ToList();
            ViewBag.UnitOptions = dicts.Where(d => d.FParentID == 9).Select(d => d.DictName).ToList();
            ViewBag.SxOptions = dicts.Where(d => d.FParentID == 1).Select(d => d.DictName).ToList();
            ViewBag.SlOptions = dicts.Where(d => d.FParentID == 2 && d.DictName != null && d.DictName.EndsWith("%"))
                .Select(d => d.DictName).ToList();
        }

        /// <summary>ModelState 校验 + 业务校验（编码唯一 / 分类存在），失败时填充错误，返回 null。</summary>
        private t_ERP_ITEMCLASS ValidateAndCheck(ItemEditViewModel vm, int? excludeId)
        {
            if (!ModelState.IsValid) return null;

            var cpbm = vm.Cpbm?.Trim();
            var dupQuery = _db.t_ERP_ITEM.AsQueryable().Where(i => i.ItemCode == cpbm);
            if (excludeId.HasValue) dupQuery = dupQuery.Where(i => i.ID != excludeId.Value);
            if (dupQuery.Any())
            {
                ModelState.AddModelError(nameof(vm.Cpbm), "该商品编码已存在！");
                return null;
            }

            var cls = _db.t_ERP_ITEMCLASS.FirstOrDefault(c => c.ID == vm.ClassId);
            if (cls == null)
            {
                ModelState.AddModelError(nameof(vm.ClassId), "商品分类不存在。");
                return null;
            }

            // 仓库 ID 指向 t_ERP_Department(ID)。前端下拉可被绕过，写入前必须确认存在，
            // 否则留下脏 FK：商品仓库名解析成空白，盘点/退料按部门过滤的人员下拉会变空。
            if (vm.Ckid.HasValue && vm.Ckid.Value > 0
                && !_db.t_ERP_Department.Any(d => d.ID == vm.Ckid.Value))
            {
                ModelState.AddModelError(nameof(vm.Ckid), "所选仓库不存在，请重新选择。");
                return null;
            }

            // 条形码按分类条件必填：一级类别(lb)为「产成品」时必须填写，其他类别不强制
            if (cls.TopClassName == "产成品" && string.IsNullOrWhiteSpace(vm.Txm))
            {
                ModelState.AddModelError(nameof(vm.Txm), "商品分类为「产成品」，条形码不能为空。");
                return null;
            }

            ValidateGroupItems(vm, vm.Cpbm);
            return ModelState.IsValid ? cls : null;
        }

        /// <summary>税率字符串 → 小数（"13%" → 0.13M），与旧系统换算一致。</summary>
        private static decimal ParseSlSize(string sl)
        {
            if (string.IsNullOrEmpty(sl) || !sl.EndsWith("%")) return 0.16M;
            return decimal.TryParse(sl.Substring(0, sl.Length - 1), out var f) ? f / 100M : 0.16M;
        }

        private static ItemEditViewModel Map(t_ERP_ITEM i)
        {
            return new ItemEditViewModel
            {
                ID = i.ID,
                Cpbm = i.ItemCode,
                Cpqc = i.ItemFullName,
                Cpjc = i.ItemShortName,
                Cppym = i.ItemPinyinCode,
                Cpgg = i.ItemSpec,
                ClassId = i.CategoryId,
                Txm = i.Barcode,
                Sx = i.Attr1,
                Sx1 = i.Attr2,
                Wlbzdw = i.PackageUnit,
                Wlbzl = i.PackageRate,
                Wljbdw = i.BaseUnit,
                Jhl = i.NetContent,
                Zzl = i.TotalWeight,
                Lsdj = i.RetailPrice,
                Ccdj = i.FactoryPrice,
                Jmdj = i.FranchisePrice,
                Cbdj = i.CostPrice,
                Jxdj = i.DistributorPrice,
                Sl = i.TaxRateText,
                Wbbm = i.ExternalCode,
                Cplb = i.ProductCategory,
                Cphw = i.Location,
                Ckid = i.WarehouseId,
                Bz = i.Remark,
                KcZt = i.IsStockControlled == true,
                FbZt = i.IsPublishControlled == true,
                ZhZt = i.IsCombo == true,
                DlbZt = i.IsGiftBoxControlled == true,
                ZpZt = i.IsGiftControlled == true,
                KdZt = i.IsWalnutMilk == true,
                Enabled = i.IsEnabled == true,
            };
        }

        private static ItemEditViewModel NewWithDefaults(int? classId)
        {
            return new ItemEditViewModel
            {
                ClassId = classId.HasValue && classId.Value > 0 ? classId : null,
                Wlbzl = 1,
                Jhl = 0.000M,
                Zzl = 0.000M,
                Lsdj = 0.00M,
                Ccdj = 0.00M,
                Jmdj = 0.00M,
                Cbdj = 0.00M,
                Jxdj = 0.00M,
                Sl = "16%",
                Enabled = true,   // 新商品默认启用
                FbZt = true,      // 新商品默认发布
            };
        }

        /// <summary>GET 是否以模态窗体方式加载：AJAX 请求头或 ?modal=1 视为 partial。</summary>
        private bool IsModalRequest()
            => Request.Query["modal"] == "1" || Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        /// <summary>POST 是否以 AJAX 方式提交：带 X-Requested-With 头时返回 JSON/校验 partial，否则走整页重定向（旧入口兼容）。</summary>
        private bool IsAjaxRequest()
            => Request.Headers["X-Requested-With"] == "XMLHttpRequest";
    }
}
