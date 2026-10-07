using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace GONES.Model.Pg;

public partial class GonesPgDbContext : DbContext
{
    public GonesPgDbContext()
    {
    }

    public GonesPgDbContext(DbContextOptions<GonesPgDbContext> options)
        : base(options)
    {
    }


        public virtual DbSet<t_ERP_DataDict> t_ERP_DataDict { get; set; }

    public virtual DbSet<t_ERP_Department> t_ERP_Department { get; set; }

        public virtual DbSet<t_ERP_ITEM> t_ERP_ITEM { get; set; }

    public virtual DbSet<t_ERP_ITEMCLASS> t_ERP_ITEMCLASS { get; set; }

    public virtual DbSet<t_ERP_ITEMGROUP> t_ERP_ITEMGROUP { get; set; }


    public virtual DbSet<t_ERP_Menu> t_ERP_Menu { get; set; }


    public virtual DbSet<t_ERP_Role> t_ERP_Role { get; set; }

    public virtual DbSet<t_ERP_RoleMenu> t_ERP_RoleMenu { get; set; }


        public virtual DbSet<t_ERP_ShopInfo> t_ERP_ShopInfo { get; set; }


        public virtual DbSet<t_ERP_UserInfo> t_ERP_UserInfo { get; set; }


    public virtual DbSet<t_PMS_BatchNoStock> t_PMS_BatchNoStock { get; set; }


    public virtual DbSet<t_PMS_BillUse> t_PMS_BillUse { get; set; }

    public virtual DbSet<t_PMS_BillUseEntry> t_PMS_BillUseEntry { get; set; }

    public virtual DbSet<t_PMS_DefaultPickerInfo> t_PMS_DefaultPickerInfo { get; set; }

    public virtual DbSet<t_PMS_GoodsApply> t_PMS_GoodsApply { get; set; }

    public virtual DbSet<t_PMS_GoodsApplyEntry> t_PMS_GoodsApplyEntry { get; set; }


    public virtual DbSet<t_PMS_ProduceOrder> t_PMS_ProduceOrder { get; set; }

    public virtual DbSet<t_PMS_ProduceOrderProductEntry> t_PMS_ProduceOrderProductEntry { get; set; }

    public virtual DbSet<t_PMS_ProduceOrderRawMaterialEntry> t_PMS_ProduceOrderRawMaterialEntry { get; set; }

    public virtual DbSet<t_PMS_ProduceOrderStepEntry> t_PMS_ProduceOrderStepEntry { get; set; }


    public virtual DbSet<t_PMS_Step> t_PMS_Step { get; set; }

    public virtual DbSet<t_PMS_StepProductBom> t_PMS_StepProductBom { get; set; }

    public virtual DbSet<t_PMS_StockBill> t_PMS_StockBill { get; set; }

    public virtual DbSet<t_PMS_StockBillEntry> t_PMS_StockBillEntry { get; set; }


        public virtual DbSet<t_PMS_Worker> t_PMS_Worker { get; set; }


    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            // Local sandbox blocks the default 5432; override per-environment via GONES_PG_CONN.
            // Production/customer: Host=<host>;Port=5432;Database=GONESERP;Username=postgres;Password=<pwd>
            var conn = Environment.GetEnvironmentVariable("GONES_PG_CONN")
                ?? "Host=127.0.0.1;Port=5432;Database=GONESERP;Username=postgres;Password=postgres";
            optionsBuilder.UseNpgsql(conn);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {


        modelBuilder.Entity<t_ERP_DataDict>(entity =>
        {
            entity.HasKey(e => e.ID).HasName("PK__t_ERP_Da__3214EC273F865F66");

            entity.ToTable("t_ERP_DataDict");

            entity.Property(e => e.DictName)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.DictNo)
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.IsShow).HasDefaultValue(false);
            entity.Property(e => e.Remark)
                .HasMaxLength(500)
                .IsUnicode(false);
        });

        modelBuilder.Entity<t_ERP_Department>(entity =>
        {
            entity.HasKey(e => e.ID).HasName("PK__t_ERP_De__3214EC274356F04A");

            entity.ToTable("t_ERP_Department");

            entity.HasIndex(e => e.ParentID, "idx_ParentID");

            entity.Property(e => e.ID).ValueGeneratedNever();
            entity.Property(e => e.DEPName).IsRequired();
        });


        modelBuilder.Entity<t_ERP_ITEM>(entity =>
        {
            entity.HasKey(e => e.ID).HasName("PK__t_ERP_IT__3214EC276F357288");

            entity.ToTable("t_ERP_ITEM", tb => tb.HasComment("商品资料表"));
            entity.Property(e => e.ID).HasComment("主键ID（自增）");
            entity.Property(e => e.ydb_zt).HasComment("（原拼音字段 ydb_zt，语义待确认）");
            entity.Property(e => e.xguid).HasComment("外部同步GUID（原拼音字段 xguid，语义待确认）");

            entity.HasIndex(e => e.ItemSpec, "idx_cpgg");

            entity.HasIndex(e => e.ItemShortName, "idx_cpjc");

            entity.HasIndex(e => e.RowGuid, "idx_guid");

            entity.HasIndex(e => e.Barcode, "idx_txm");

            entity.HasIndex(e => e.ProductCategory, "idx_xcpdm");

            entity.Property(e => e.StockQuantity).HasColumnName("stock_quantity")
                .HasDefaultValue(0m)
                .HasColumnType("numeric(18, 2)")
                .HasComment("库存数量");
            entity.Property(e => e.Remark).HasColumnName("remark")
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasComment("备注");
            entity.Property(e => e.CostPrice).HasColumnName("cost_price").HasColumnType("numeric(18, 2)")
                .HasComment("成本单价");
            entity.Property(e => e.FactoryPrice).HasColumnName("factory_price").HasColumnType("numeric(18, 2)")
                .HasComment("出厂单价");
            entity.Property(e => e.ItemCode).HasColumnName("item_code")
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasComment("商品编码");
            entity.Property(e => e.CategoryCode).HasColumnName("category_code")
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasComment("商品分类代码");
            entity.Property(e => e.ItemSpec).HasColumnName("item_spec")
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasComment("商品规格");
            entity.Property(e => e.Location).HasColumnName("location")
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasComment("产品货位");
            entity.Property(e => e.ItemShortName).HasColumnName("item_short_name")
                .HasMaxLength(150)
                .IsUnicode(false)
                .HasComment("商品简称");
            entity.Property(e => e.ProductCategory).HasColumnName("product_category")
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasComment("产品类别");
            entity.Property(e => e.ItemPinyinCode).HasColumnName("item_pinyin_code")
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasComment("商品拼音码");
            entity.Property(e => e.ItemFullName).HasColumnName("item_full_name")
                .HasMaxLength(250)
                .IsUnicode(false)
                .HasComment("商品全称");
            entity.Property(e => e.IsGiftBoxControlled).HasColumnName("is_gift_box_controlled").HasDefaultValue(false)
                .HasComment("礼盒控制");
            entity.Property(e => e.IsPublishControlled).HasColumnName("is_publish_controlled").HasDefaultValue(true)
                .HasComment("发布控制");
            entity.Property(e => e.RowGuid).HasColumnName("row_guid")
                .HasMaxLength(100)
                .IsUnicode(false)
                .HasComment("行唯一标识");
            entity.Property(e => e.hg_zt).HasDefaultValue(false)
                .HasComment("（原拼音字段 hg_zt，语义待确认）");
            entity.Property(e => e.NetContent).HasColumnName("net_content").HasColumnType("numeric(18, 3)")
                .HasComment("净含量");
            entity.Property(e => e.FranchisePrice).HasColumnName("franchise_price").HasColumnType("numeric(18, 2)")
                .HasComment("加盟单价");
            entity.Property(e => e.DistributorPrice).HasColumnName("distributor_price").HasColumnType("numeric(18, 2)")
                .HasComment("经销价");
            entity.Property(e => e.IsWalnutMilk).HasColumnName("is_walnut_milk").HasDefaultValue(false)
                .HasComment("核桃乳商品");
            entity.Property(e => e.RetailPrice).HasColumnName("retail_price").HasColumnType("numeric(18, 2)")
                .HasComment("零售单价");
            entity.Property(e => e.TaxRateText).HasColumnName("tax_rate_text")
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("16%")
                .HasComment("税率(字符串)");
            entity.Property(e => e.TaxRateValue).HasColumnName("tax_rate_value")
                .HasDefaultValue(0.16m)
                .HasColumnType("numeric(18, 2)")
                .HasComment("税率数值");
            entity.Property(e => e.Attr1).HasColumnName("attr1")
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasComment("属性");
            entity.Property(e => e.Attr2).HasColumnName("attr2")
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasComment("属性2");
            entity.Property(e => e.Barcode).HasColumnName("barcode")
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasDefaultValue("")
                .HasComment("条形码");
            entity.Property(e => e.ExternalCode).HasColumnName("external_code")
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasDefaultValue("")
                .HasComment("外部编码");
            entity.Property(e => e.PackageUnit).HasColumnName("package_unit")
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasComment("包装单位");
            entity.Property(e => e.BaseUnit).HasColumnName("base_unit")
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasComment("基本单位");
            entity.Property(e => e.xguid)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.VirtualStock).HasColumnName("virtual_stock")
                .HasDefaultValue(0m)
                .HasColumnType("numeric(18, 2)")
                .HasComment("虚拟库存");
            entity.Property(e => e.TotalWeight).HasColumnName("total_weight").HasColumnType("numeric(18, 3)")
                .HasComment("总重量");
            entity.Property(e => e.CategoryId).HasColumnName("category_id")
                .HasComment("商品分类ID");
            entity.Property(e => e.PackageRate).HasColumnName("package_rate")
                .HasComment("包装率");
            entity.Property(e => e.IsStockControlled).HasColumnName("is_stock_controlled")
                .HasComment("库存控制");
            entity.Property(e => e.IsEnabled).HasColumnName("is_enabled")
                .HasComment("启用(1=启用)");
            entity.Property(e => e.IsCombo).HasColumnName("is_combo")
                .HasComment("组合商品");
            entity.Property(e => e.IsGiftControlled).HasColumnName("is_gift_controlled")
                .HasComment("赠品控制");
            entity.Property(e => e.WarehouseId).HasColumnName("warehouse_id")
                .HasComment("仓库ID");
        });

        modelBuilder.Entity<t_ERP_ITEMCLASS>(entity =>
        {
            entity.HasKey(e => e.ID).HasName("PK__t_ERP_IT__3214EC2773FA27A5");

            entity.ToTable("t_ERP_ITEMCLASS");

            entity.HasIndex(e => e.FParentID, "idx_Fid");

            entity.HasIndex(e => e.ClassName, "idx_class_name");

            entity.HasIndex(e => e.ParentClassCode, "idx_parent_class_code");

            entity.Property(e => e.TopClassName)
                .HasColumnName("top_class_name")
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.ParentClassCode)
                .HasColumnName("parent_class_code")
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.ClassName)
                .HasColumnName("class_name")
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.FullCode)
                .HasColumnName("full_code")
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.FullName)
                .HasColumnName("full_name")
                .HasMaxLength(250)
                .IsUnicode(false);
            entity.Property(e => e.UseStatus).HasColumnName("use_status").HasDefaultValue(1);
            entity.Property(e => e.ClassLevel).HasColumnName("class_level");
        });

        modelBuilder.Entity<t_ERP_ITEMGROUP>(entity =>
        {
            entity.HasKey(e => e.ID).HasName("PK__t_ERP_IT__3214EC2777CAB889");

            entity.ToTable("t_ERP_ITEMGROUP");

            // [2026-09-24] DB 列已改名（rename_itemgroup.js），补 HasColumnName 桥接 + 新索引名；属性名保持 PascalCase 不变。
            entity.HasIndex(e => e.ComponentItemGuid, "idx_component_item_guid");

            entity.HasIndex(e => e.IsRepairStatus, "idx_repair_status");

            entity.HasIndex(e => e.ComboItemCode, "idx_combo_item_code");

            entity.HasIndex(e => e.ComponentItemCode, "idx_component_item_code");

            entity.Property(e => e.IsRepairStatus).HasColumnName("is_repair_status").HasDefaultValue(false);
            entity.Property(e => e.ComboItemCode)
                .HasColumnName("combo_item_code")
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.ComboItemGuid)
                .HasColumnName("combo_item_guid")
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.ComponentItemCode)
                .HasColumnName("component_item_code")
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.ComponentItemGuid)
                .HasColumnName("component_item_guid")
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.UnitPrice).HasColumnName("unit_price").HasColumnType("numeric(18, 2)");
            entity.Property(e => e.BaseQuantity).HasColumnName("base_quantity");
            entity.Property(e => e.DepartmentId).HasColumnName("department_id");
        });


        modelBuilder.Entity<t_ERP_Menu>(entity =>
        {
            entity.HasKey(e => e.ID).HasName("PK__t_ERP_Me__3214EC273C74E891");

            // [2026-09-23] DB 列已迁移为 snake_case（rename_to_snake_case.sql），补 HasColumnName 桥接；属性名保持 PascalCase 不变。
            entity.ToTable("t_erp_menu");

            entity.Property(e => e.ID).HasColumnName("id");
            entity.Property(e => e.MenuName)
                .HasColumnName("menu_name")
                .IsRequired()
                .HasMaxLength(500)
                .IsUnicode(false);
            entity.Property(e => e.ParentID).HasColumnName("parent_id");
            entity.Property(e => e.MenuType).HasColumnName("menu_type");
            entity.Property(e => e.DllName)
                .HasColumnName("dll_name")
                .HasMaxLength(500)
                .IsUnicode(false);
            entity.Property(e => e.ClassName)
                .HasColumnName("class_name")
                .HasMaxLength(500)
                .IsUnicode(false);
            entity.Property(e => e.OpenType).HasColumnName("open_type");
            entity.Property(e => e.Flag).HasColumnName("flag").HasDefaultValue(true);
            entity.Property(e => e.Memo)
                .HasColumnName("memo")
                .HasMaxLength(500)
                .IsUnicode(false);
            entity.Property(e => e.Route)
                .HasColumnName("route")
                .HasMaxLength(64)
                .IsUnicode(false);
            entity.Property(e => e.Operating).HasColumnName("operating");

            // 索引名沿用 DB 现有名（RENAME COLUMN 不会重命名索引）
            entity.HasIndex(e => e.ParentID, "idx_ParentID");
            entity.HasIndex(e => e.ClassName, "idx_classname");
            entity.HasIndex(e => e.MenuType, "idx_menutype");
            entity.HasIndex(e => e.OpenType, "idx_opentype");
        });


        modelBuilder.Entity<t_ERP_Role>(entity =>
        {
            entity.HasKey(e => e.ID).HasName("PK__t_ERP_Ro__3214EC2710615C29");

            entity.ToTable("t_ERP_Role");

            entity.Property(e => e.RoleName)
                .IsRequired()
                .HasMaxLength(100);
        });

        modelBuilder.Entity<t_ERP_RoleMenu>(entity =>
        {
            entity.HasKey(e => e.ID).HasName("PK__t_ERP_Ro__3214EC271431ED0D");

            entity.ToTable("t_ERP_RoleMenu");

            entity.Property(e => e.MenuID).HasMaxLength(2000);
        });


        modelBuilder.Entity<t_ERP_ShopInfo>(entity =>
        {
            entity.HasKey(e => e.ID).HasName("PK__t_ERP_Sh__3214EC271BD30ED5");

            entity.ToTable("t_ERP_ShopInfo");

            entity.HasIndex(e => e.ShopNumber, "idx_ShopNumber");

            entity.Property(e => e.Address)
                .HasMaxLength(200)
                .IsUnicode(false);
            entity.Property(e => e.AppSecrect)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.Appkey)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.ConfigData)
                .HasMaxLength(4000)
                .IsUnicode(false);
            entity.Property(e => e.IsAgent).HasDefaultValue(false);
            entity.Property(e => e.IsEnable).HasDefaultValue(true);
            entity.Property(e => e.IsHandMerge).HasDefaultValue(false);
            entity.Property(e => e.IsImport).HasDefaultValue(false);
            entity.Property(e => e.IsMerge).HasDefaultValue(false);
            entity.Property(e => e.IsMessage).HasDefaultValue(false);
            entity.Property(e => e.IsRFM).HasDefaultValue(false);
            entity.Property(e => e.Message)
                .HasMaxLength(500)
                .IsUnicode(false);
            entity.Property(e => e.Phone)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.QQ)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.SendAisle)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.SessionKey)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.ShopName)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.ShopType)
                .HasMaxLength(50)
                .IsUnicode(false);
        });


        modelBuilder.Entity<t_ERP_UserInfo>(entity =>
        {
            entity.HasKey(e => e.ID).HasName("PK__t_ERP_Us__3214EC272FDA0782");

            // [2026-09-23] DB 列已迁移为 snake_case（rename_to_snake_case.sql），补 HasColumnName 桥接；属性名保持 PascalCase 不变。
            entity.ToTable("t_erp_user_info");

            // SQL Server 源库中 t_ERP_UserInfo.ID 为 IDENTITY；PG 侧须显式声明为 IDENTITY BY DEFAULT，
            // 否则 EF 与原始 SQL 探针不显式给 ID 时插入会触发 not-null 违反(23502)。
            entity.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd().UseIdentityByDefaultColumn();
            entity.Property(e => e.Account).HasColumnName("account").IsRequired();
            entity.Property(e => e.Depot).HasColumnName("depot").IsRequired();
            entity.Property(e => e.MacAddress).HasColumnName("mac_address").HasMaxLength(200);
            entity.Property(e => e.PassWord).HasColumnName("password").IsRequired();
            entity.Property(e => e.UserName).HasColumnName("user_name").IsRequired();
            entity.Property(e => e.DEPID).HasColumnName("dep_id");
            entity.Property(e => e.RoleID).HasColumnName("role_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Shop).HasColumnName("shop");
            entity.Property(e => e.IsLogin).HasColumnName("is_login");
            entity.Property(e => e.PwdMustChange).HasColumnName("pwd_must_change");
        });


        modelBuilder.Entity<t_PMS_BatchNoStock>(entity =>
        {
            entity.HasKey(e => e.FInterID).HasName("PK__t_PMS_Ba__7A7754431C5D1EBA");

            entity.ToTable("t_PMS_BatchNoStock");

            entity.Property(e => e.FInterID).ValueGeneratedNever();
            entity.Property(e => e.FBatchNo)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.FBatchNum).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FBegDate);
            entity.Property(e => e.FEndDate);
            entity.Property(e => e.FName)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.FPrice).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FState).HasDefaultValue(false);
        });


        modelBuilder.Entity<t_PMS_BillUse>(entity =>
        {
            entity.HasKey(e => e.FInterID).HasName("PK__t_PMS_Bi__7A77544327CED166");

            entity.ToTable("t_PMS_BillUse");

            entity.HasIndex(e => e.FBillNo, "idx_instock_billno");

            entity.HasIndex(e => e.FDate, "idx_instock_fdate");

            entity.Property(e => e.FInterID).ValueGeneratedNever();
            entity.Property(e => e.FBillNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FBillType)
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.FRemark)
                .HasMaxLength(1000)
                .IsUnicode(false);
            entity.Property(e => e.FWorkPeople)
                .HasMaxLength(1000)
                .IsUnicode(false);
        });

        modelBuilder.Entity<t_PMS_BillUseEntry>(entity =>
        {
            entity.HasKey(e => e.FEntryID).HasName("PK__t_PMS_Bi__70F191902B9F624A");

            entity.ToTable("t_PMS_BillUseEntry");

            entity.HasIndex(e => e.FBomId, "idx_FBomId");

            entity.HasIndex(e => e.FInterID, "idx_FInterID");

            entity.HasIndex(e => e.FIsUse, "idx_FIsUse");

            entity.HasIndex(e => e.FItemID, "idx_FItemID");

            entity.HasIndex(e => e.FProduceId, "idx_FProduceId");

            entity.HasIndex(e => e.FScheduleNoID, "idx_FScheduleNo");

            entity.HasIndex(e => e.FStepID, "idx_FStepID");

            entity.Property(e => e.FCurrentUseNum)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FIsProduct).HasDefaultValue(false);
            entity.Property(e => e.FIsUse).HasDefaultValue(false);
            entity.Property(e => e.FLastUseNum)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FProduceNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FScheduleNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FUseNum).HasColumnType("decimal(18, 5)");
        });

        modelBuilder.Entity<t_PMS_DefaultPickerInfo>(entity =>
        {
            entity.HasKey(e => e.FID).HasName("PK__t_PMS_De__C1BEA5A22F6FF32E");

            entity.ToTable("t_PMS_DefaultPickerInfo");

            entity.HasIndex(e => e.FBomID, "idx_BomId");
        });

        modelBuilder.Entity<t_PMS_GoodsApply>(entity =>
        {
            entity.HasKey(e => e.FInterID).HasName("PK__t_PMS_Go__7A77544333408412");

            entity.ToTable("t_PMS_GoodsApply");

            entity.HasIndex(e => e.FBillNo, "FBillNo");

            entity.HasIndex(e => e.FInterID, "FInterID");

            entity.HasIndex(e => e.FStockID, "idx_FStockID");

            entity.HasIndex(e => e.FDate, "idx_Fdate");

            entity.Property(e => e.FInterID).ValueGeneratedNever();
            entity.Property(e => e.FBillNo)
                .IsRequired()
                .HasMaxLength(255);
            entity.Property(e => e.FCheckDate);
            entity.Property(e => e.FDate);
            entity.Property(e => e.FExplanation)
                .IsRequired()
                .HasMaxLength(255)
                .HasDefaultValue("");
            entity.Property(e => e.FFetchAdd)
                .IsRequired()
                .HasMaxLength(255)
                .HasDefaultValue("");
            entity.Property(e => e.FModifyDate);
        });

        modelBuilder.Entity<t_PMS_GoodsApplyEntry>(entity =>
        {
            entity.HasKey(e => e.FEntryID).HasName("PK__t_PMS_Go__70F19190371114F6");

            entity.ToTable("t_PMS_GoodsApplyEntry");

            // FEntryID 现改为 IDENTITY BY DEFAULT 全局唯一自增主键（不再与 FInterID 组成复合键）。
            // 应用层不再赋行号，跨单不再撞键（根治旧 23505 + 反审核误禁）。
            entity.Property(e => e.FEntryID).ValueGeneratedOnAdd().UseIdentityByDefaultColumn();

            entity.HasIndex(e => e.FBomId, "idx_FBomId");

            entity.HasIndex(e => e.FInterID, "idx_FInterID");

            entity.HasIndex(e => e.FItemID, "idx_FItemID");

            entity.HasIndex(e => e.FStockID, "idx_FStockID");

            entity.Property(e => e.FAmount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FFetchDate);
            entity.Property(e => e.FFetchNum)
                .HasDefaultValue(0m)
                .HasComment("完成数量")
                .HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FNote)
                .HasMaxLength(255)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.FPrice)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FQty)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FSecCoefficient).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FSecQty).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FStatus)
                .HasDefaultValue((byte)0)
                .HasComment("0 未导入  1  部分导入 2 已导入");
        });


        modelBuilder.Entity<t_PMS_ProduceOrder>(entity =>
        {
            entity.HasKey(e => e.FInterID).HasName("PK__t_PMS_Pr__7A7754433EB236BE");

            entity.ToTable("t_PMS_ProduceOrder");

            entity.HasIndex(e => e.FDate, "idx_Date");

            entity.HasIndex(e => e.FBillNo, "idx_FBillNo");

            entity.HasIndex(e => e.FOrderStatus, "idx_FOrderStatus");

            entity.HasIndex(e => e.FScheduleNo, "idx_FScheduleNo");

            entity.HasIndex(e => e.FStatus, "idx_FStatus");

            entity.Property(e => e.FInterID).ValueGeneratedNever();
            entity.Property(e => e.FApplyNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FAuditDate);
            entity.Property(e => e.FBillNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FComment)
                .HasMaxLength(1000)
                .IsUnicode(false);
            entity.Property(e => e.FDate);
            entity.Property(e => e.FModifyDate);
            entity.Property(e => e.FOrderStatus).HasDefaultValue((short)0);
            entity.Property(e => e.FPickNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FRemark)
                .HasMaxLength(1000)
                .IsUnicode(false);
            entity.Property(e => e.FScheduleNo)
                .HasMaxLength(30)
                .IsUnicode(false);
        });

        modelBuilder.Entity<t_PMS_ProduceOrderProductEntry>(entity =>
        {
            entity.HasKey(e => e.FEntryID).HasName("PK__t_PMS_Pr__70F191904282C7A2");

            entity.ToTable("t_PMS_ProduceOrderProductEntry");

            // FEntryID 现改为 IDENTITY BY DEFAULT 全局唯一自增主键（不再与 FInterID 组成复合键）。
            entity.Property(e => e.FEntryID).ValueGeneratedOnAdd().UseIdentityByDefaultColumn();

            entity.HasIndex(e => e.FBomId, "idx_FBomId");

            entity.HasIndex(e => e.FItemID, "idx_FItemID");

            entity.HasIndex(e => e.FPickingStatus, "idx_FPickingStatus");

            entity.HasIndex(e => e.FProductionPickingStatus, "idx_FProductionPickingStatus");

            entity.HasIndex(e => e.FStockStatus, "idx_FStockStatus");

            entity.HasIndex(e => e.FInterID, "idx_FinterId");

            entity.Property(e => e.FBatchNumber)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.FOutAmount).HasColumnType("numeric(19, 4)");
            entity.Property(e => e.FOutFactory)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.FOutPrice).HasColumnType("numeric(19, 4)");
            entity.Property(e => e.FPickingStatus).HasDefaultValue((byte)0);
            entity.Property(e => e.FPlanDate);
            entity.Property(e => e.FPlanNum)
                .HasDefaultValue(0.0m)
                .HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FProduceType)
                .HasMaxLength(100)
                .IsUnicode(false)
                .HasDefaultValue("自制");
            entity.Property(e => e.FProductionPickingStatus).HasDefaultValue((byte)0);
            entity.Property(e => e.FStockStatus).HasDefaultValue((byte)0);
        });

        modelBuilder.Entity<t_PMS_ProduceOrderRawMaterialEntry>(entity =>
        {
            entity.HasKey(e => e.FEntryID).HasName("PK__t_PMS_Pr__70F1919046535886");

            entity.ToTable("t_PMS_ProduceOrderRawMaterialEntry");

            entity.HasIndex(e => e.FBomId, "idx_FBomId");

            entity.HasIndex(e => e.FInterID, "idx_FInterID");

            entity.HasIndex(e => e.FItemID, "idx_FItemID");

            entity.HasIndex(e => e.FStepID, "idx_FStepID");

            entity.Property(e => e.FFixedLoss).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FLossRate)
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.FLossStandValue).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FLossUnit)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FLossValue).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FNeedNum).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FNum).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FPriceUnit)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FRemark)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.FWorkHourUnit)
                .HasMaxLength(20)
                .IsUnicode(false);
        });

        modelBuilder.Entity<t_PMS_ProduceOrderStepEntry>(entity =>
        {
            entity.HasKey(e => e.FEntryID).HasName("PK__t_PMS_Pr__70F191904A23E96A");

            entity.ToTable("t_PMS_ProduceOrderStepEntry");

            entity.HasIndex(e => e.FBomId, "idx_FBomId");

            entity.HasIndex(e => e.FInterID, "idx_FInterID");

            entity.HasIndex(e => e.FItemID, "idx_FItemID");

            entity.HasIndex(e => e.FStepID, "idx_FStepID");

            entity.Property(e => e.FAlgo)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FFixedLoss).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FHour).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FHourArti).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FLossRate)
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.FLossStandValue).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FLossUnit)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FLossValue).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FNum).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FOutPutNum).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FPlanOutPutNum).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FPrice).HasColumnType("numeric(19, 4)");
            entity.Property(e => e.FPriceUnit)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FSingleArti).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FWorkHourUnit)
                .HasMaxLength(20)
                .IsUnicode(false);
        });


        modelBuilder.Entity<t_PMS_Step>(entity =>
        {
            entity.HasKey(e => e.FItemID).HasName("PK__t_PMS_St__E24F47EF5D36BDDE");

            entity.ToTable("t_PMS_Step");

            entity.HasIndex(e => e.FName, "idx_processname");

            entity.Property(e => e.FAlgo)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FDelete).HasDefaultValue(0);
            entity.Property(e => e.FFixedLoss).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FHour).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FHourArti).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FLossRate)
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.FLossStandValue).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FLossUnit)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FLossValue).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FName)
                .HasMaxLength(255)
                .IsUnicode(false);
            entity.Property(e => e.FNum).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FPrice).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FPriceUnit)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FSingleArti).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FType)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FWorkHourUnit)
                .HasMaxLength(20)
                .IsUnicode(false);
        });

        modelBuilder.Entity<t_PMS_StepProductBom>(entity =>
        {
            entity.HasKey(e => e.FID).HasName("PK__t_PMS_St__C1BEA5A261074EC2");

            entity.ToTable("t_PMS_StepProductBom");

            entity.HasIndex(e => e.FBomID, "idx_FBomID");

            entity.HasIndex(e => e.FIsProduct, "idx_FIsProduct");

            entity.HasIndex(e => e.FPItemID, "idx_FPItemID");

            entity.HasIndex(e => e.FStepId, "idx_FStepId");

            entity.Property(e => e.FBaseNum).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FBaseUnit)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FCostType)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FDelete).HasDefaultValue(0);
            entity.Property(e => e.FIsProduct).HasDefaultValue(false);
            entity.Property(e => e.FLossRate)
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.FLossStandValue).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FLossUnit)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.FLossValue).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FRemark)
                .HasMaxLength(1000)
                .IsUnicode(false);
            entity.Property(e => e.FStepName)
                .HasMaxLength(50)
                .IsUnicode(false);
        });

        modelBuilder.Entity<t_PMS_StockBill>(entity =>
        {
            entity.HasKey(e => e.FInterID).HasName("PK__t_PMS_St__7A77544364D7DFA6");

            entity.ToTable("t_PMS_StockBill");

            entity.HasIndex(e => new { e.FState, e.FBillType }, "IX_SB_State_Type");

            entity.HasIndex(e => e.FBillType, "idx_billtype");

            entity.HasIndex(e => e.FDate, "idx_fdate");

            entity.HasIndex(e => e.FBillNo, "idx_instock_billno");

            entity.Property(e => e.FInterID).ValueGeneratedNever();
            entity.Property(e => e.FBillNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FBillTypeEx)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FBuyingUnit)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.FDate);
            entity.Property(e => e.FInspectors)
                .HasMaxLength(1000)
                .IsUnicode(false);
            entity.Property(e => e.FModifyTime);
            entity.Property(e => e.FROB).HasDefaultValue((short)1);
            entity.Property(e => e.FRemark)
                .HasMaxLength(1000)
                .IsUnicode(false);
            entity.Property(e => e.oaid)
                .HasMaxLength(255)
                .IsUnicode(false);
        });

        modelBuilder.Entity<t_PMS_StockBillEntry>(entity =>
        {
            // FEntryID 为 IDENTITY BY DEFAULT 全局唯一单列主键（Design A：与 GoodsApplyEntry /
            // ProduceOrderProductEntry 一致）。9 个单据控制器不再显式赋 FEntryID=i，改由序列生成，
            // 既根治跨单撞键 23505，也避免之前为躲撞键而被迫使用的复合键。
            entity.HasKey(e => e.FEntryID).HasName("PK__t_PMS_St__70F1919068A8708A");

            entity.Property(e => e.FEntryID).UseIdentityByDefaultColumn();

            entity.ToTable("t_PMS_StockBillEntry");

            entity.HasIndex(e => e.FBatchNo, "IX_SBE_BatchNo");

            entity.HasIndex(e => new { e.FItemID, e.FBatchNo }, "IX_SBE_Item_Batch");

            entity.HasIndex(e => e.FBatchNo, "idx_FBatchNo");

            entity.HasIndex(e => e.FBillUseEntryID, "idx_FBillUseEntryID");

            entity.HasIndex(e => e.FProduceNo, "idx_FProduceNo");

            entity.HasIndex(e => e.FScheduleNoID, "idx_FScheduleNoID");

            entity.HasIndex(e => e.FProduceID, "idx_ProduceId");

            entity.HasIndex(e => e.FBomId, "idx_instock_fbomid");

            entity.HasIndex(e => e.FInterID, "idx_instock_finterid");

            entity.HasIndex(e => e.FItemID, "idx_instock_fitemid");

            entity.Property(e => e.FAfterTaxAmount).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FAfterTaxPrice).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FAmount).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FBatchNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FBegDate);
            entity.Property(e => e.FBillUseEntryID).HasDefaultValue(0);
            entity.Property(e => e.FBillUseNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FBomIDBatchNo).IsUnicode(false);
            entity.Property(e => e.FBuyingNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FEndDate);
            entity.Property(e => e.FNote)
                .HasMaxLength(1000)
                .IsUnicode(false);
            entity.Property(e => e.FNum).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FNumExt1)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FNumExt2)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FNumExt3)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FPlanDate);
            entity.Property(e => e.FPlanNum).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FPrice).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FProduceNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FROB).HasDefaultValue((short)1);
            entity.Property(e => e.FSEOutNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FScheduleNo)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FTaxAmount).HasColumnType("decimal(18, 5)");
            entity.Property(e => e.FTaxRate)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FTaxRateValue).HasColumnType("decimal(18, 5)");
        });


        modelBuilder.Entity<t_PMS_Worker>(entity =>
        {
            entity.HasKey(e => e.ID).HasName("PK__t_PMS_Wo__3214EC27741A2336");

            entity.ToTable("t_PMS_Worker");

            entity.HasIndex(e => e.DEPID, "idx_WorkShopID");

            entity.Property(e => e.FIDNumber)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.FMobile)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.FName).IsRequired();
        });

        // [2026-09-23] 统一应用 snake_case 列/表名约定（DB 已全量改名为 snake_case）。
        // 放在各表 Entity<> 配置之后，作为最终覆盖层；C# 属性名保持 PascalCase 不变。
        SnakeCaseConvention.Apply(modelBuilder);

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
