using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace 编辑器
{
    /// <summary>在线文献检索来源。</summary>
    public enum LiteratureSearchSource
    {
        /// <summary>OpenAlex（https://openalex.org）—— 免费、无 Key、覆盖广（2.5 亿+）。</summary>
        OpenAlex,

        /// <summary>Semantic Scholar（https://semanticscholar.org）—— 免费、无 Key（限流 100 次/5 分钟）。</summary>
        SemanticScholar,
    }

    /// <summary>一条检索结果（尚未入库；入库时转成 LiteratureEntry）。</summary>
    public record LiteratureSearchResult(
        string Title, string Authors, string Year, string Venue,
        string Doi, string Url, string Abstract)
    {
        /// <summary>对应来源里的稳定 ID（OpenAlex 的 work id / S2 的 paperId），去重与日志用。</summary>
        public string SourceId { get; init; } = "";
        public LiteratureSearchSource Source { get; init; }

        /// <summary>列表展示行。</summary>
        public string Display
        {
            get
            {
                var head = string.IsNullOrWhiteSpace(Year) ? Title : $"{Title} ({Year})";
                var tail = string.IsNullOrWhiteSpace(Venue) ? "" : $" — {Venue}";
                return head + tail;
            }
        }

        /// <summary>转成文献库条目（DOI 已归一化）。</summary>
        public LiteratureEntry ToEntry() => new()
        {
            Title = Title.Trim(),
            Authors = Authors.Trim(),
            Year = Year.Trim(),
            Venue = Venue.Trim(),
            Doi = LiteratureSearch.NormalizeDoi(Doi),
            Url = Url.Trim(),
            Abstract = Abstract.Trim(),
            Note = Source == LiteratureSearchSource.OpenAlex ? "来自 OpenAlex" : "来自 Semantic Scholar"
        };
    }

    /// <summary>
    /// 在线文献检索（OpenAlex / Semantic Scholar）。两个源都免费、无需 API Key；
    /// 检索结果只是「候选」——用户勾选后才入库，AI 引用仍然只出自文献库，防编造的边界不变。
    /// </summary>
    public static class LiteratureSearch
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // 遵循系统代理（Clash 等本地代理也会被捡起来）；超时给足，学术源偶尔慢
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var ua = "DddWritingHelper/1.0 (mailto:user@example.com)";
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
            // OpenAlex 礼节：带 mailto 进入 polite pool
            client.DefaultRequestHeaders.Add("From", "user@example.com");
            return client;
        }

        /// <summary>DOI 归一化：剥掉 https://doi.org/ 与 dx. 前缀，统一小写。</summary>
        public static string NormalizeDoi(string doi)
        {
            var d = (doi ?? "").Trim();
            foreach (var prefix in new[] { "https://doi.org/", "http://doi.org/", "https://dx.doi.org/", "doi:" })
            {
                if (d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    d = d[prefix.Length..];
                    break;
                }
            }
            return d.Trim().ToLowerInvariant();
        }

        /// <summary>把 OpenAlex 的 abstract_inverted_index 重建为原文（词 → 出现位置列表）。</summary>
        public static string RebuildInvertedIndex(JsonElement inverted)
        {
            if (inverted.ValueKind != JsonValueKind.Object) return "";

            var positions = new List<(int Pos, string Word)>();
            foreach (var prop in inverted.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var p in prop.Value.EnumerateArray())
                {
                    if (p.TryGetInt32(out int pos)) positions.Add((pos, prop.Name));
                }
            }
            if (positions.Count == 0) return "";
            return string.Join(" ", positions.OrderBy(x => x.Pos).Select(x => x.Word));
        }

        /// <summary>检索；失败抛异常（调用方弹窗提示），无结果返回空列表。</summary>
        public static async Task<List<LiteratureSearchResult>> SearchAsync(
            string query, LiteratureSearchSource source, int limit = 15,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<LiteratureSearchResult>();
            return source switch
            {
                LiteratureSearchSource.OpenAlex => await SearchOpenAlexAsync(query, limit, ct),
                LiteratureSearchSource.SemanticScholar => await SearchSemanticScholarAsync(query, limit, ct),
                _ => new List<LiteratureSearchResult>()
            };
        }

        // ------------------------------------------------------------------
        // OpenAlex
        // ------------------------------------------------------------------

        private static async Task<List<LiteratureSearchResult>> SearchOpenAlexAsync(
            string query, int limit, CancellationToken ct)
        {
            var url = "https://api.openalex.org/works?per-page=" + Math.Clamp(limit, 1, 50)
                    + "&search=" + Uri.EscapeDataString(query);
            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var results = new List<LiteratureSearchResult>();

            if (!doc.RootElement.TryGetProperty("results", out var items)) return results;
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("display_name", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(title)) continue;

                // 作者：authorships[].author.display_name，前 5 个
                string authors = "";
                if (item.TryGetProperty("authorships", out var auths)
                    && auths.ValueKind == JsonValueKind.Array)
                {
                    var names = auths.EnumerateArray()
                        .Select(a => a.TryGetProperty("author", out var au)
                            && au.TryGetProperty("display_name", out var n) ? n.GetString() ?? "" : "")
                        .Where(n => n.Length > 0).Take(5).ToList();
                    if (auths.GetArrayLength() > 5) names.Add("等");
                    authors = string.Join(", ", names);
                }

                var year = item.TryGetProperty("publication_year", out var y) && y.ValueKind == JsonValueKind.Number
                    ? y.GetInt32().ToString() : "";

                string venue = "";
                var primary = item.TryGetProperty("primary_location", out var pl)
                    && pl.ValueKind == JsonValueKind.Object ? pl : default;
                if (primary.ValueKind != default
                    && primary.TryGetProperty("source", out var src)
                    && src.ValueKind == JsonValueKind.Object
                    && src.TryGetProperty("display_name", out var sn))
                    venue = sn.GetString() ?? "";

                var doi = NormalizeDoi(item.TryGetProperty("doi", out var d) ? d.GetString() ?? "" : "");

                string abs = "";
                if (item.TryGetProperty("abstract_inverted_index", out var inv)
                    && inv.ValueKind == JsonValueKind.Object)
                    abs = RebuildInvertedIndex(inv);

                results.Add(new LiteratureSearchResult(title, authors, year, venue, doi, "", abs)
                {
                    Source = LiteratureSearchSource.OpenAlex,
                    SourceId = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : ""
                });
            }
            return results;
        }

        // ------------------------------------------------------------------
        // Semantic Scholar
        // ------------------------------------------------------------------

        private static async Task<List<LiteratureSearchResult>> SearchSemanticScholarAsync(
            string query, int limit, CancellationToken ct)
        {
            // fields 用逗号列出需要的字段；openAccessPdf 不取（只要元数据）
            var url = "https://api.semanticscholar.org/graph/v1/paper/search?limit=" + Math.Clamp(limit, 1, 50)
                    + "&fields=title,authors,year,venue,externalIds,abstract"
                    + "&query=" + Uri.EscapeDataString(query);
            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var results = new List<LiteratureSearchResult>();

            if (!doc.RootElement.TryGetProperty("data", out var items)) return results;
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(title)) continue;

                string authors = "";
                if (item.TryGetProperty("authors", out var auths) && auths.ValueKind == JsonValueKind.Array)
                {
                    var names = auths.EnumerateArray()
                        .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                        .Where(n => n.Length > 0).Take(5).ToList();
                    if (auths.GetArrayLength() > 5) names.Add("等");
                    authors = string.Join(", ", names);
                }

                var year = item.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number
                    ? y.GetInt32().ToString() : "";
                var venue = item.TryGetProperty("venue", out var v) ? v.GetString() ?? "" : "";

                var doi = "";
                var s2Url = "";
                if (item.TryGetProperty("externalIds", out var ext) && ext.ValueKind == JsonValueKind.Object)
                {
                    if (ext.TryGetProperty("DOI", out var d)) doi = NormalizeDoi(d.GetString() ?? "");
                    if (ext.TryGetProperty("CorpusId", out var c))
                        s2Url = "https://www.semanticscholar.org/paper/" + c.GetInt64();
                }

                var abs = item.TryGetProperty("abstract", out var a) ? a.GetString() ?? "" : "";

                results.Add(new LiteratureSearchResult(title, authors, year, venue, doi, s2Url, abs)
                {
                    Source = LiteratureSearchSource.SemanticScholar,
                    SourceId = item.TryGetProperty("paperId", out var pid) ? pid.GetString() ?? "" : ""
                });
            }
            return results;
        }
    }
}
