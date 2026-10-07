using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Npgsql;

namespace GONES.Web.Data.Infrastructure
{
    /// <summary>
    /// 启动自愈引导器：在应用启动时确保数据库与必备运行数据就位。
    ///
    /// 设计约束（与项目铁律一致）：
    /// 1. 不使用 EF 迁移 / EnsureCreated —— 库结构真相只在手工 snake_case SQL 里（schema.sql）。
    /// 2. 仅当库内不存在标记表 t_erp_menu 时才执行建表+播种；已初始化则直接返回（幂等、零开销）。
    /// 3. 只建不删：CREATE DATABASE / CREATE TABLE / INSERT，绝不做 DROP / EnsureDeleted。
    /// 4. 建表与播种在同一事务内，任一步失败整段回滚，不留半初始化状态（fail-fast）。
    /// 5. 若连 GONESERP 数据库本身都不存在，则先连维护库(postgres)建库，再连入建表。
    /// </summary>
    public static class DbInitializer
    {
        // 标记表：存在即视为已初始化
        private const string MarkerTable = "t_erp_menu";
        private const string SchemaResource = "sql.schema.sql";
        private const string SeedResource = "sql.seed.sql";
        private const string MaintenanceDb = "postgres";

        /// <summary>
        /// 确保数据库与必备数据就位。应用启动早期（app.Build() 之后、app.Run() 之前）调用。
        /// </summary>
        public static async Task InitializeAsync(string appConnectionString, int maxRetry = 12, int retryDelayMs = 3000)
        {
            EnsureDatabaseExists(appConnectionString);

            NpgsqlConnection conn = null;
            bool connected = false;
            for (int i = 0; i < maxRetry; i++)
            {
                try
                {
                    conn = new NpgsqlConnection(appConnectionString);
                    await conn.OpenAsync();
                    connected = true;
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DbInitializer] 连接 GONESERP 失败（第 {i + 1}/{maxRetry} 次），{retryDelayMs}ms 后重试：{ex.Message}");
                    if (conn != null) await conn.DisposeAsync();
                    conn = null;
                    if (i == maxRetry - 1) break;
                    await Task.Delay(retryDelayMs);
                }
            }

            if (!connected || conn == null)
                throw new InvalidOperationException("[DbInitializer] 无法连接到 GONESERP，启动中止。请检查连接串与数据库服务。");

            try
            {
                // 幂等闸门：已存在标记表即跳过
                await using var checkCmd = new NpgsqlCommand(
                    "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name=@t", conn);
                checkCmd.Parameters.AddWithValue("t", MarkerTable);
                var exists = await checkCmd.ExecuteScalarAsync();
                if (exists != null)
                {
                    Console.WriteLine("[DbInitializer] 检测到标记表 t_erp_menu，数据库已初始化，跳过建表/播种。");
                    return;
                }

                Console.WriteLine("[DbInitializer] 未检测到库结构，开始自动建库(表)+播种必备数据……");
                await using var tx = await conn.BeginTransactionAsync();
                try
                {
                    ExecuteScript(conn, tx, ReadEmbedded(SchemaResource));
                    ExecuteScript(conn, tx, ReadEmbedded(SeedResource));
                    await tx.CommitAsync();
                    Console.WriteLine("[DbInitializer] 建表与播种完成，数据库已就绪。");
                }
                catch
                {
                    await tx.RollbackAsync();
                    Console.WriteLine("[DbInitializer] 建表/播种失败，已回滚。启动中止以避免半初始化状态。");
                    throw;
                }
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }

        /// <summary>
        /// 若目标库不存在，先连维护库建库。库已存在则直接返回（Docker 下 POSTGRES_DB 通常已建好，此处幂等）。
        /// </summary>
        private static void EnsureDatabaseExists(string appConnectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(appConnectionString);
            var targetDb = builder.Database;
            if (string.IsNullOrWhiteSpace(targetDb))
                return; // 连接串未指定库名，交由上层连接处理

            var maintBuilder = new NpgsqlConnectionStringBuilder(appConnectionString) { Database = MaintenanceDb };
            using var conn = new NpgsqlConnection(maintBuilder.ConnectionString);
            conn.Open();

            using var check = conn.CreateCommand();
            check.CommandText = "SELECT 1 FROM pg_database WHERE datname = @db";
            check.Parameters.AddWithValue("db", targetDb);
            var found = check.ExecuteScalar();

            if (found == null)
            {
                Console.WriteLine($"[DbInitializer] 数据库 \"{targetDb}\" 不存在，正在创建……");
                using var create = conn.CreateCommand();
                // 库名按标识符引用，避免关键字/大小写问题
                create.CommandText = $"CREATE DATABASE \"{targetDb}\"";
                create.ExecuteNonQuery();
                Console.WriteLine($"[DbInitializer] 数据库 \"{targetDb}\" 创建完成。");
            }
        }

        private static string ReadEmbedded(string suffix)
        {
            var asm = typeof(DbInitializer).Assembly;
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"[DbInitializer] 缺少嵌入资源（{suffix}）。可用资源：{string.Join(", ", asm.GetManifestResourceNames())}");
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("[DbInitializer] 无法读取嵌入资源：" + name);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static void ExecuteScript(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.ExecuteNonQuery();
        }
    }
}
