using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace GONES.Web.Helpers
{
    /// <summary>
    /// LINQ-to-Entities helpers that keep generated SQL inside SQL Server's hard limits.
    /// </summary>
    public static class QueryableExtensions
    {
        /// <summary>
        /// IN-filter that can never overflow SQL Server's 2100-parameter limit. The key
        /// collection is split into chunks and one DbQuery per chunk is OR-combined, the
        /// results being unioned in memory. Use it whenever the key collection can be large
        /// (e.g. "every item id"), where a plain <c>list.Contains(x.Key)</c> would emit one
        /// parameter per element and throw past ~2100 keys.
        /// </summary>
        public static List<T> WhereInChunks<T, TKey>(this IQueryable<T> source,
            IEnumerable<TKey> keys, Expression<Func<T, TKey>> keySelector, int chunkSize = 1000)
        {
            var list = (keys ?? Enumerable.Empty<TKey>()).Distinct().ToList();
            if (list.Count == 0) return new List<T>();

            var result = new List<T>();
            for (int i = 0; i < list.Count; i += chunkSize)
            {
                var chunk = list.GetRange(i, Math.Min(chunkSize, list.Count - i));
                var containsCall = Expression.Call(
                    typeof(Enumerable),
                    nameof(Enumerable.Contains),
                    new[] { typeof(TKey) },
                    Expression.Constant(chunk),
                    keySelector.Body);
                var predicate = Expression.Lambda<Func<T, bool>>(containsCall, keySelector.Parameters);
                result.AddRange(source.Where(predicate).ToList());
            }
            return result;
        }
    }
}
