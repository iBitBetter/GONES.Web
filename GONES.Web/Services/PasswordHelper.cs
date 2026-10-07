using System;
using System.Security.Cryptography;
using System.Text;

namespace GONES.Web.Services
{
    /// <summary>
    /// 忠实还原旧 WinForms 的 ZS.Publicinfo.ClassEx.Rnd.MD5 规则：
    ///   MD5(Encoding.Default.GetBytes(pwd)) 以小写十六进制(x2)输出。
    /// 旧系统在中文 Windows 上 Encoding.Default == GBK(936)，故此处注册 CodePages
    /// 以使用 GBK；ASCII 密码(如默认 "123")在 UTF-8/GBK 下字节一致，哈希必然相同。
    /// </summary>
    public static class PasswordHelper
    {
        public static string Md5(string input)
        {
            Encoding enc = Encoding.UTF8;
            try { enc = Encoding.GetEncoding("GBK"); }
            catch { /* 无 GBK 时回退 UTF-8，ASCII 密码结果一致 */ }

            using var md5 = MD5.Create();
            byte[] bytes = md5.ComputeHash(enc.GetBytes(input ?? string.Empty));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Initial password handed out when an account is created or reset.
        ///
        /// Why not the old fixed "123": every new account then shares one well-known password,
        /// and the Web front end is reachable by anyone on the LAN, so "123" is effectively a
        /// published credential. A per-account random value plus PwdMustChange=1 means only the
        /// person who was told the value can log in, and only once.
        ///
        /// ASCII-only on purpose: Md5() encodes with GBK, and a non-ASCII char would produce a
        /// hash the old WinForms client could never reproduce.
        /// 0/O/1/l/I are left out so the password can be read out over the phone.
        /// </summary>
        public static string GenerateInitialPassword(int length = 10)
        {
            const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
            const string digits = "23456789";
            const string alphabet = letters + digits;

            if (length < 8) length = 8;
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

            // Guarantee both character classes (a pure-letter or pure-digit password would be
            // rejected by most password policies and is easier to guess).
            if (!chars.Any(char.IsDigit))
                chars[RandomNumberGenerator.GetInt32(length)] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
            if (!chars.Any(char.IsLetter))
                chars[RandomNumberGenerator.GetInt32(length)] = letters[RandomNumberGenerator.GetInt32(letters.Length)];

            return new string(chars);
        }
    }
}
