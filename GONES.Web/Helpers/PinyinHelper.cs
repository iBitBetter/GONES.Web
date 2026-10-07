using System;
using PKT.NPinyin;

namespace GONES.Web.Helpers
{
    /// <summary>
    /// 拼音码生成（取代 SQL Server 标量函数 dbo.f_GetPinYin，去除数据库函数依赖）。
    /// 取每个汉字的拼音首字母并大写；非汉字字符（数字/字母/符号）原样保留。
    /// </summary>
    public static class PinyinHelper
    {
        public static string GetPinYinCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            try
            {
                var initials = Pinyin.GetInitials(text);
                return (initials ?? "").ToUpperInvariant();
            }
            catch
            {
                return "";
            }
        }
    }
}
