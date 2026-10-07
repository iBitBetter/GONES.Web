using System;
using System.Collections.Generic;

namespace GONES.Model.Pg;

public partial class t_ERP_ITEM
{
    public int ID { get; set; }

    public string RowGuid { get; set; }

    public string ItemCode { get; set; }

    public string ItemFullName { get; set; }

    public string ItemShortName { get; set; }

    public string ItemPinyinCode { get; set; }

    public string ItemSpec { get; set; }

    public int? CategoryId { get; set; }

    public string CategoryCode { get; set; }

    public string Barcode { get; set; }

    public string Attr1 { get; set; }

    public string Attr2 { get; set; }

    public string PackageUnit { get; set; }

    public int? PackageRate { get; set; }

    public string BaseUnit { get; set; }

    public decimal? NetContent { get; set; }

    public decimal? TotalWeight { get; set; }

    public decimal? RetailPrice { get; set; }

    public decimal? FactoryPrice { get; set; }

    public decimal? FranchisePrice { get; set; }

    public bool? IsStockControlled { get; set; }

    public bool? IsEnabled { get; set; }

    public bool? IsCombo { get; set; }

    public bool? IsPublishControlled { get; set; }

    public bool? IsGiftBoxControlled { get; set; }

    public string Remark { get; set; }

    public decimal? CostPrice { get; set; }

    public string Location { get; set; }

    public bool? IsGiftControlled { get; set; }

    public string ExternalCode { get; set; }

    public bool? ydb_zt { get; set; }

    public bool? hg_zt { get; set; }

    public bool? IsWalnutMilk { get; set; }

    public string TaxRateText { get; set; }

    public decimal? TaxRateValue { get; set; }

    public decimal? DistributorPrice { get; set; }

    public string xguid { get; set; }

    public string ProductCategory { get; set; }

    public int? WarehouseId { get; set; }

    public decimal? VirtualStock { get; set; }

    public decimal? StockQuantity { get; set; }
}
