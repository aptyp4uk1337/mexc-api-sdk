using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Web;

#nullable enable

namespace MexcApi
{
    // ─── Enums ────────────────────────────────────────────────────────────────

    public enum ApiType
    {
        General,
        Futures,
        Contract,
        Other,
    }

    public enum HttpMethodType
    {
        GET,
        POST,
        PUT,
        DELETE,
    }

    public enum PriceType
    {
        LastPrice,
        FairPrice,
        IndexPrice,
    }

    // ─── Extension helpers ────────────────────────────────────────────────────

    public static class HttpMethodTypeExtensions
    {
        public static bool HasBody(this HttpMethodType m) =>
            m == HttpMethodType.POST || m == HttpMethodType.PUT;

        public static HttpMethod ToHttpMethod(this HttpMethodType m) => m switch
        {
            HttpMethodType.GET    => HttpMethod.Get,
            HttpMethodType.POST   => HttpMethod.Post,
            HttpMethodType.PUT    => HttpMethod.Put,
            HttpMethodType.DELETE => HttpMethod.Delete,
            _                     => HttpMethod.Get,
        };
    }

    public static class PriceTypeExtensions
    {
        public static string Value(this PriceType pt) => pt switch
        {
            PriceType.LastPrice  => "last_price",
            PriceType.FairPrice  => "fair_price",
            PriceType.IndexPrice => "index_price",
            _                    => "last_price",
        };

        public static string TickerField(this PriceType pt) => pt switch
        {
            PriceType.FairPrice  => "fairPrice",
            PriceType.IndexPrice => "indexPrice",
            _                    => "lastPrice",
        };

        public static string KlinePathSegment(this PriceType pt) => pt switch
        {
            PriceType.IndexPrice => "index_price",
            PriceType.FairPrice  => "fair_price",
            _                    => "",
        };

        public static PriceType TryParse(string? value) => value switch
        {
            "fair_price"  => PriceType.FairPrice,
            "index_price" => PriceType.IndexPrice,
            _             => PriceType.LastPrice,
        };
    }

    // ─── Value objects ─────────────────────────────────────────────────────────

    public sealed record ProxyConfig(string Host, int Port, string Type, string? Auth = null)
    {
        public static ProxyConfig? FromUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host))
                return null;

            var scheme = u.Scheme.ToLowerInvariant();
            var proxyType = scheme switch
            {
                "socks5" => "socks5",
                _        => "http",
            };

            string? auth = u.UserInfo is { Length: > 0 } info ? info : null;
            return new ProxyConfig(u.Host, u.Port > 0 ? u.Port : 1080, proxyType, auth);
        }
    }

    public sealed record SignatureResult(string Timestamp, string Sign);

    public sealed class PendingRequest
    {
        public string Id { get; } = "req_" + Guid.NewGuid().ToString("N");
        public string Url { get; }
        public HttpMethodType Method { get; }
        public Dictionary<string, string> Headers { get; }
        public Dictionary<string, object?> Body { get; }
        public string Endpoint { get; }

        public PendingRequest(string url, HttpMethodType method,
            Dictionary<string, string> headers,
            Dictionary<string, object?> body,
            string endpoint)
        {
            Url      = url;
            Method   = method;
            Headers  = headers;
            Body     = body;
            Endpoint = endpoint;
        }
    }

    // ─── Signature generator ───────────────────────────────────────────────────

    public sealed class SignatureGenerator
    {
        private readonly string _apiKey;
        private long   _lastMs      = 0;
        private int    _msIncrement = 0;

        public SignatureGenerator(string apiKey) => _apiKey = apiKey;

        public SignatureResult Generate(Dictionary<string, object?> payload, HttpMethodType method, string? forceTime = null)
        {
            var time = forceTime ?? UniqueMs();
            var g    = Md5(_apiKey + time).Substring(7);

            string sign;
            if (method.HasBody())
            {
                var body = payload.Count == 0
                    ? ""
                    : JsonSerializer.Serialize(payload, JsonOptions.Serialize);
                sign = Md5(time + body + g);
            }
            else
            {
                sign = Md5(time + BuildQuery(payload) + g);
            }

            return new SignatureResult(time, sign);
        }

        private string UniqueMs()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now == _lastMs)
                _msIncrement++;
            else
            {
                _lastMs      = now;
                _msIncrement = 0;
            }
            return (now + _msIncrement).ToString();
        }

        private static string Md5(string input)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        internal static string BuildQuery(Dictionary<string, object?> p)
        {
            var sb = new StringBuilder();
            foreach (var kv in p)
            {
                if (kv.Value is null) continue;
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(kv.Value.ToString() ?? ""));
            }
            return sb.ToString();
        }
    }

    // ─── JSON options ──────────────────────────────────────────────────────────

    internal static class JsonOptions
    {
        public static readonly JsonSerializerOptions Serialize = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static readonly JsonSerializerOptions Deserialize = new()
        {
            PropertyNameCaseInsensitive = true,
        };
    }

    // ─── Response handler ──────────────────────────────────────────────────────

    public sealed class ResponseHandler
    {
        private readonly bool _isTestnet;

        private static readonly Dictionary<string, Dictionary<string, string>> RenameMappings = new()
        {
            ["/contract/detailV2"] = new()
            {
                ["dn"]     = "displayName",
                ["dne"]    = "displayNameEn",
                ["pot"]    = "positionOpenType",
                ["bc"]     = "baseCoin",
                ["qc"]     = "quoteCoin",
                ["bcn"]    = "baseCoinName",
                ["qcn"]    = "quoteCoinName",
                ["ft"]     = "futureType",
                ["sc"]     = "settleCoin",
                ["cs"]     = "contractSize",
                ["minL"]   = "minLeverage",
                ["maxL"]   = "maxLeverage",
                ["ccMaxL"] = "countryConfigContractMaxLeverage",
                ["ps"]     = "priceScale",
                ["vs"]     = "volScale",
                ["as"]     = "amountScale",
                ["pu"]     = "priceUnit",
                ["vu"]     = "volUnit",
                ["minV"]   = "minVol",
                ["maxV"]   = "maxVol",
                ["blpr"]   = "bidLimitPriceRate",
                ["alpr"]   = "askLimitPriceRate",
                ["tfr"]    = "takerFeeRate",
                ["mfr"]    = "makerFeeRate",
                ["mmr"]    = "maintenanceMarginRate",
                ["imr"]    = "initialMarginRate",
                ["rbv"]    = "riskBaseVol",
                ["riv"]    = "riskIncrVol",
                ["rlss"]   = "riskLongShortSwitch",
                ["rim"]    = "riskIncrMmr",
                ["rii"]    = "riskIncrImr",
                ["rll"]    = "riskLevelLimit",
                ["pcv"]    = "priceCoefficientVariation",
                ["io"]     = "indexOrigin",
                ["in"]     = "isNew",
                ["ih"]     = "isHot",
                ["ihd"]    = "isHidden",
                ["ip"]     = "isPromoted",
                ["cp"]     = "conceptPlate",
                ["cpi"]    = "conceptPlateId",
                ["rlt"]    = "riskLimitType",
                ["mno"]    = "maxNumOrders",
                ["moml"]   = "marketOrderMaxLevel",
                ["moplr1"] = "marketOrderPriceLimitRate1",
                ["moplr2"] = "marketOrderPriceLimitRate2",
                ["tp"]     = "triggerProtect",
                ["ae"]     = "appraisal",
                ["sac"]    = "showAppraisalCountdown",
                ["ad"]     = "automaticDelivery",
                ["aa"]     = "apiAllowed",
                ["dsl"]    = "depthStepList",
                ["lmv"]    = "limitMaxVol",
                ["tsd"]    = "threshold",
                ["bciu"]   = "baseCoinIconUrl",
                ["bcid"]   = "baseCoinId",
                ["ct"]     = "createTime",
                ["ot"]     = "openingTime",
                ["oco"]    = "openingCountdownOption",
                ["sbo"]    = "showBeforeOpen",
                ["iml"]    = "isMaxLeverage",
                ["izfr"]   = "isZeroFeeRate",
                ["rlm"]    = "riskLimitMode",
                ["rlcs"]   = "riskLimitCustom",
                ["izfs"]   = "isZeroFeeSymbol",
                ["liqfr"]  = "liquidationFeeRate",
                ["frm"]    = "feeRateMode",
                ["levfrs"] = "leverageFeeRates",
                ["tiefrs"] = "tieredFeeRates",
            },
            ["/api/platform/spot/market-v2/web/symbolsV2"] = new()
            {
                ["mcd"] = "marketCurrencyId",
                ["cd"]  = "coinId",
                ["vn"]  = "currency",
                ["fn"]  = "currencyFullName",
                ["srt"] = "sortOrder",
                ["sts"] = "status",
                ["tp"]  = "marketType",
                ["in"]  = "icon",
                ["ot"]  = "openingTime",
                ["cp"]  = "categories",
                ["ci"]  = "categories_ids",
                ["ps"]  = "priceScale",
                ["qs"]  = "quantityScale",
                ["cdm"] = "contractDecimalMultiplier",
                ["st"]  = "spotEnabled",
                ["dst"] = "depositStatus",
                ["tt"]  = "tradingType",
                ["ca"]  = "contractAddress",
                ["fne"] = "currencyFullNameEn",
            },
        };

        public ResponseHandler(bool isTestnet) => _isTestnet = isTestnet;

        public Dictionary<string, object?> Handle(string? raw, int status, string? endpoint = null)
        {
            if (string.IsNullOrEmpty(raw))
                return Error(status, "Request failed: empty response");

            Dictionary<string, object?>? decoded;
            try
            {
                decoded = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw, JsonOptions.Deserialize);
            }
            catch (JsonException ex)
            {
                var preview = raw.Length > 200 ? raw[..200] : raw;
                return Error(status, $"Invalid JSON: {ex.Message} | raw: {preview}");
            }

            if (decoded is null)
                return Error(status, "Invalid JSON: null result");

            if (decoded.TryGetValue("data", out var dataVal) && dataVal is JsonElement dataEl
                && dataEl.ValueKind == JsonValueKind.Object && endpoint is not null)
            {
                var dataDict = JsonElementToDict(dataEl);
                decoded["data"] = RenameFields(dataDict, endpoint);
            }
            else if (decoded.TryGetValue("data", out var dataVal2) && dataVal2 is JsonElement dataEl2
                     && dataEl2.ValueKind == JsonValueKind.Array && endpoint is not null)
            {
                var list = new List<object?>();
                foreach (var item in dataEl2.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                        list.Add(RenameFields(JsonElementToDict(item), endpoint));
                    else
                        list.Add(JsonElementToNative(item));
                }
                decoded["data"] = list;
            }

            decoded["is_testnet"] = _isTestnet;
            return decoded;
        }

        public Dictionary<string, object?> Error(int code, string message) => new()
        {
            ["success"]    = false,
            ["code"]       = code,
            ["message"]    = message,
            ["timestamp"]  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["is_testnet"] = _isTestnet,
        };

        private static Dictionary<string, object?> RenameFields(Dictionary<string, object?> data, string endpoint)
        {
            foreach (var kv in RenameMappings)
            {
                if (endpoint.Contains(kv.Key))
                    return RecursiveRename(data, kv.Value);
            }
            return data;
        }

        private static Dictionary<string, object?> RecursiveRename(
            Dictionary<string, object?> arr,
            Dictionary<string, string> map)
        {
            var result = new Dictionary<string, object?>(arr.Count);
            foreach (var kv in arr)
            {
                var newKey = map.TryGetValue(kv.Key, out var mapped) ? mapped : kv.Key;
                result[newKey] = kv.Value is Dictionary<string, object?> nested
                    ? RecursiveRename(nested, map)
                    : kv.Value;
            }
            return result;
        }

        internal static Dictionary<string, object?> JsonElementToDict(JsonElement el)
        {
            var d = new Dictionary<string, object?>();
            foreach (var prop in el.EnumerateObject())
                d[prop.Name] = JsonElementToNative(prop.Value);
            return d;
        }

        private static object? JsonElementToNative(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.Object  => JsonElementToDict(el),
            JsonValueKind.Array   => el.EnumerateArray().Select(JsonElementToNative).ToList(),
            JsonValueKind.String  => el.GetString(),
            JsonValueKind.Number  => el.TryGetInt64(out var i) ? (object?)i : el.GetDouble(),
            JsonValueKind.True    => true,
            JsonValueKind.False   => false,
            JsonValueKind.Null    => null,
            _                     => el.GetRawText(),
        };
    }

    // ─── HTTP client factory ───────────────────────────────────────────────────

    public sealed class HttpClientFactory : IDisposable
    {
        private readonly HttpClient _client;

        public HttpClientFactory(ProxyConfig? proxy = null)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip
                    | DecompressionMethods.Deflate
                    | DecompressionMethods.Brotli,
                AllowAutoRedirect   = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                UseCookies          = false,
            };

            if (proxy is not null)
            {
                handler.UseProxy = true;
                handler.Proxy    = proxy.Type == "socks5"
                    ? new WebProxy($"socks5://{proxy.Host}:{proxy.Port}")
                    : new WebProxy($"http://{proxy.Host}:{proxy.Port}");

                if (proxy.Auth is not null)
                {
                    var parts = proxy.Auth.Split(':', 2);
                    handler.Proxy.Credentials = new NetworkCredential(parts[0], parts.Length > 1 ? parts[1] : "");
                }
            }

            _client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
        }

        public HttpClient Client => _client;

        public void Dispose() => _client.Dispose();
    }

    // ─── Main MexcBypass class ─────────────────────────────────────────────────

    public sealed class MexcBypass : IDisposable
    {
        private readonly string              _apiKey;
        private readonly bool                _isTestnet;
        private readonly SignatureGenerator  _signer;
        private readonly HttpClientFactory   _httpFactory;
        private readonly ResponseHandler     _responseHandler;
        private readonly Dictionary<string, string> _baseUrls;

        private bool _batchMode = false;
        private readonly Dictionary<string, PendingRequest> _pendingRequests = new();

        public MexcBypass(string apiKey = "", bool isTestnet = false, string? proxyUrl = null)
        {
            _apiKey          = apiKey;
            _isTestnet       = isTestnet;
            var proxy        = proxyUrl is not null ? ProxyConfig.FromUrl(proxyUrl) : null;
            _signer          = new SignatureGenerator(apiKey);
            _httpFactory     = new HttpClientFactory(proxy);
            _responseHandler = new ResponseHandler(isTestnet);

            _baseUrls = new()
            {
                ["general"]  = "https://www.mexc.com",
                ["futures"]  = isTestnet
                    ? "https://futures.testnet.mexc.com/api/v1"
                    : "https://futures.mexc.com/api/v1",
                ["contract"] = "https://contract.mexc.com/api/v1",
                ["other"]    = "",
            };
        }

        // ── Core request ──────────────────────────────────────────────────────

        private Dictionary<string, object?> Request(
            HttpMethodType method,
            ApiType apiType,
            string endpoint,
            Dictionary<string, object?>? rawParams = null)
        {
            var p = FilterParams(rawParams ?? new());

            var sig     = _signer.Generate(p, method);
            var baseUrl = ResolveBase(apiType, endpoint);
            var origin  = ExtractOrigin(baseUrl);
            var headers = BuildHeaders(apiType, sig, origin);
            var url     = BuildUrl(baseUrl, endpoint, method, p);
            var body    = method.HasBody() ? p : new();

            if (_batchMode)
            {
                var pending = new PendingRequest(url, method, headers, body, endpoint);
                _pendingRequests[pending.Id] = pending;
                return new() { ["_request_id"] = pending.Id };
            }

            return SendSync(url, method, headers, body, endpoint);
        }

        private Dictionary<string, object?> SendSync(
            string url,
            HttpMethodType method,
            Dictionary<string, string> headers,
            Dictionary<string, object?> body,
            string endpoint)
        {
            try
            {
                var task = SendAsync(url, method, headers, body, endpoint);
                task.Wait();
                return task.Result;
            }
            catch (AggregateException ae)
            {
                return _responseHandler.Error(0, ae.InnerException?.Message ?? ae.Message);
            }
        }

        private async Task<Dictionary<string, object?>> SendAsync(
            string url,
            HttpMethodType method,
            Dictionary<string, string> headers,
            Dictionary<string, object?> body,
            string endpoint)
        {
            using var req = new HttpRequestMessage(method.ToHttpMethod(), url);

            foreach (var h in headers)
            {
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            if ((method.HasBody() || method == HttpMethodType.DELETE) && body.Count > 0)
            {
                var json = JsonSerializer.Serialize(body, JsonOptions.Serialize);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            HttpResponseMessage resp;
            string raw = "";
            int statusCode;

            try
            {
                resp       = await _httpFactory.Client.SendAsync(req).ConfigureAwait(false);
                statusCode = (int)resp.StatusCode;
                raw        = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return _responseHandler.Error(0, $"HTTP error: {ex.Message}");
            }

            return _responseHandler.Handle(raw, statusCode, endpoint);
        }

        // ── Batch (parallel) execution ─────────────────────────────────────────

        public Dictionary<string, Dictionary<string, object?>> Batch(
            Dictionary<string, Func<Dictionary<string, object?>>> requests)
        {
            _batchMode = true;
            _pendingRequests.Clear();

            var keyToId  = new Dictionary<string, string>();
            var results  = new Dictionary<string, Dictionary<string, object?>>();

            foreach (var kv in requests)
            {
                var ret = kv.Value();
                if (ret.TryGetValue("_request_id", out var id) && id is string sid)
                    keyToId[kv.Key] = sid;
                else
                    results[kv.Key] = ret;
            }

            var pending = ExecutePending(keyToId);
            foreach (var kv in pending)
                results[kv.Key] = kv.Value;

            _batchMode = false;
            _pendingRequests.Clear();

            return results;
        }

        private Dictionary<string, Dictionary<string, object?>> ExecutePending(
            Dictionary<string, string> keyToId)
        {
            if (keyToId.Count == 0) return new();

            var tasks   = new Dictionary<string, Task<Dictionary<string, object?>>>();
            var results = new Dictionary<string, Dictionary<string, object?>>();

            foreach (var kv in keyToId)
            {
                var p = _pendingRequests[kv.Value];
                tasks[kv.Key] = SendAsync(p.Url, p.Method, p.Headers, p.Body, p.Endpoint);
            }

            Task.WhenAll(tasks.Values).Wait();

            foreach (var kv in tasks)
                results[kv.Key] = kv.Value.Result;

            return results;
        }

        /// <summary>
        /// Execute the same set of requests for multiple accounts simultaneously.
        /// </summary>
        public static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> BatchAccounts(
            Dictionary<string, Dictionary<string, object?>> accounts,
            Func<MexcBypass, Dictionary<string, Func<Dictionary<string, object?>>>> callback)
        {
            var clients    = new Dictionary<string, MexcBypass>();
            var allPending = new Dictionary<string, Dictionary<string, string>>();
            var results    = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>();

            foreach (var kv in accounts)
            {
                var cfg    = kv.Value;
                var client = new MexcBypass(
                    cfg.TryGetValue("apiKey",    out var ak) ? ak?.ToString() ?? "" :
                    cfg.TryGetValue("webkey",    out var wk) ? wk?.ToString() ?? "" : "",
                    cfg.TryGetValue("isTestnet", out var tn) && tn is bool b && b,
                    cfg.TryGetValue("proxy",     out var px) ? px?.ToString() : null
                );
                client._batchMode = true;

                allPending[kv.Key] = new();
                foreach (var req in callback(client))
                {
                    var ret = req.Value();
                    if (ret.TryGetValue("_request_id", out var id) && id is string sid)
                        allPending[kv.Key][req.Key] = sid;
                }

                clients[kv.Key]    = client;
                results[kv.Key]    = new();
            }

            // Build all tasks
            var allTasks = new List<(string AccountKey, string RequestKey, string PendingId,
                                     Task<Dictionary<string, object?>> Task)>();

            foreach (var kv in clients)
            {
                foreach (var rk in allPending[kv.Key])
                {
                    var p    = kv.Value._pendingRequests[rk.Value];
                    var task = kv.Value.SendAsync(p.Url, p.Method, p.Headers, p.Body, p.Endpoint);
                    allTasks.Add((kv.Key, rk.Key, rk.Value, task));
                }
            }

            Task.WhenAll(allTasks.Select(t => t.Task)).Wait();

            foreach (var t in allTasks)
                results[t.AccountKey][t.RequestKey] = t.Task.Result;

            foreach (var c in clients.Values)
            {
                c._batchMode = false;
                c._pendingRequests.Clear();
            }

            return results;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private string ResolveBase(ApiType apiType, string endpoint)
        {
            if (apiType == ApiType.Other)
            {
                var u = new Uri(endpoint);
                return $"https://{u.Host}";
            }
            return _baseUrls[apiType.ToString().ToLowerInvariant()];
        }

        private static string ExtractOrigin(string baseUrl)
        {
            var idx = baseUrl.IndexOf('/', 8);
            return idx >= 0 ? baseUrl[..idx] : baseUrl;
        }

        private Dictionary<string, string> BuildHeaders(ApiType apiType, SignatureResult sig, string origin)
        {
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"]    = "application/json",
                ["Accept"]          = "*/*",
                ["User-Agent"]      = "Mozilla/5.0 (compatible; MEXC-API/1.0)",
                ["Connection"]      = "keep-alive",
                ["Cache-Control"]   = "no-cache",
                ["Accept-Encoding"] = "gzip, deflate, br",
                ["Origin"]          = origin,
            };

            if (apiType == ApiType.General)
            {
                headers["Cookie"]        = $"u_id={_apiKey}; uc_token={_apiKey};";
                headers["Ucenter-Token"] = _apiKey;
            }
            else
            {
                headers["Authorization"] = _apiKey;
                headers["X-Mxc-Nonce"]  = sig.Timestamp;
                headers["X-Mxc-Sign"]   = sig.Sign;
            }

            return headers;
        }

        private static string BuildUrl(string baseUrl, string endpoint, HttpMethodType method, Dictionary<string, object?> p)
        {
            var url = baseUrl + endpoint;
            if (!method.HasBody() && p.Count > 0)
                url += "?" + SignatureGenerator.BuildQuery(p);
            return url;
        }

        private static Dictionary<string, object?> FilterParams(Dictionary<string, object?> p)
        {
            var result = new Dictionary<string, object?>();
            foreach (var kv in p)
                if (kv.Value is not null && kv.Value.ToString() != "")
                    result[kv.Key] = kv.Value;
            return result;
        }

        private (long Start, long End) WeekRange()
        {
            var now    = DateTime.UtcNow;
            var monday = now.AddDays(-(((int)now.DayOfWeek + 6) % 7)).Date;
            var sunday = monday.AddDays(6).AddHours(23).AddMinutes(59).AddSeconds(59);
            return (
                new DateTimeOffset(monday).ToUnixTimeMilliseconds(),
                new DateTimeOffset(sunday).ToUnixTimeMilliseconds() + 99
            );
        }

        private static double NormalizeVolume(double raw, Dictionary<string, object?> cd)
        {
            var scale = cd.TryGetValue("volScale", out var vs) ? Convert.ToInt32(vs) : 0;
            var unit  = cd.TryGetValue("volUnit",  out var vu) ? Convert.ToDouble(vu) : 0.0;
            var v     = Math.Round(raw, scale);

            if (unit > 0)
                v = Math.Floor(v / unit) * unit;

            var minVol = cd.TryGetValue("minVol", out var mn) ? Convert.ToDouble(mn) : 0.0;
            var maxVol = cd.TryGetValue("maxVol", out var mx) ? Convert.ToDouble(mx) : double.MaxValue;
            return Math.Max(minVol, Math.Min(v, maxVol));
        }

        private static Dictionary<string, object?> VolumePayload(
            Dictionary<string, object?> cd,
            double volume, int leverage, double price, double usdtValue, PriceType pt)
        {
            return new()
            {
                ["usdt_value"]   = usdtValue,
                ["volume"]       = volume,
                ["leverage"]     = leverage,
                ["price"]        = price,
                ["min_volume"]   = cd.GetValueOrDefault("minVol"),
                ["max_volume"]   = cd.GetValueOrDefault("maxVol"),
                ["min_leverage"] = cd.GetValueOrDefault("minLeverage"),
                ["max_leverage"] = cd.GetValueOrDefault("maxLeverage"),
                ["price_type"]   = pt.Value(),
                ["volume_scale"] = cd.GetValueOrDefault("volScale"),
                ["volume_unit"]  = cd.GetValueOrDefault("volUnit"),
                ["price_scale"]  = cd.GetValueOrDefault("priceScale"),
                ["price_unit"]   = cd.GetValueOrDefault("priceUnit"),
            };
        }

        private Dictionary<string, object?> Err(int code, string message)
            => _responseHandler.Error(code, message);

        private (bool Ok, Dictionary<string, object?> Error,
                 Dictionary<string, Dictionary<string, object?>> Result)
            FetchTickerAndContract(string symbol)
        {
            var result = Batch(new()
            {
                ["ticker"]   = () => GetFuturesTickers(new() { ["symbol"] = symbol }),
                ["contract"] = () => GetFuturesContracts(new() { ["symbol"] = symbol }),
            });

            if (!IsSuccess(result["ticker"]) || !HasData(result["ticker"]))
                return (false, Err(400, "Failed to get ticker data"), default!);

            if (!IsSuccess(result["contract"]) || !HasDataList(result["contract"]))
                return (false, Err(400, "Failed to get contract data"), default!);

            return (true, default!, result);
        }

        private static (string? Ticker, Dictionary<string, object?> Contract, PriceType Pt)
            ResolveMarket(Dictionary<string, Dictionary<string, object?>> market, Dictionary<string, object?> p)
        {
            var pt       = PriceTypeExtensions.TryParse(p.GetValueOrDefault("price_type")?.ToString());
            var tickData = GetDict(market["ticker"], "data");
            var contracts = GetList(market["contract"], "data");
            var contract = contracts?.Count > 0 ? contracts[0] as Dictionary<string, object?> : null;
            var ticker   = tickData?.GetValueOrDefault(pt.TickerField())?.ToString();
            return (ticker, contract ?? new(), pt);
        }

        private static (string BaseCoin, string QuoteCoin) ParseSymbol(Dictionary<string, object?> p)
        {
            if (p.TryGetValue("symbol", out var sym) && sym is string s && !string.IsNullOrEmpty(s))
            {
                var parts = s.Split('_', 2);
                return (parts[0], parts.Length > 1 ? parts[1] : "");
            }
            return (p.GetValueOrDefault("base_coin")?.ToString() ?? "",
                    p.GetValueOrDefault("quote_coin")?.ToString() ?? "");
        }

        // ── Utility statics ───────────────────────────────────────────────────

        private static bool IsSuccess(Dictionary<string, object?> r)
            => r.TryGetValue("success", out var v) && v is true or "true";

        private static bool HasData(Dictionary<string, object?> r)
            => r.ContainsKey("data") && r["data"] is not null;

        private static bool HasDataList(Dictionary<string, object?> r)
        {
            if (!r.TryGetValue("data", out var d)) return false;
            if (d is List<object?> l) return l.Count > 0;
            if (d is JsonElement je && je.ValueKind == JsonValueKind.Array)
                return je.GetArrayLength() > 0;
            return false;
        }

        private static Dictionary<string, object?>? GetDict(Dictionary<string, object?> r, string key)
        {
            if (!r.TryGetValue(key, out var v)) return null;
            if (v is Dictionary<string, object?> d) return d;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Object)
                return ResponseHandler.JsonElementToDict(je);
            return null;
        }

        private static List<object?>? GetList(Dictionary<string, object?> r, string key)
        {
            if (!r.TryGetValue(key, out var v)) return null;
            if (v is List<object?> l) return l;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray()
                         .Select(e => e.ValueKind == JsonValueKind.Object
                             ? (object?)ResponseHandler.JsonElementToDict(e)
                             : (object?)e.GetRawText())
                         .ToList();
            return null;
        }

        private static double GetDouble(Dictionary<string, object?> d, string key)
        {
            if (d.TryGetValue(key, out var v)) return Convert.ToDouble(v);
            return 0.0;
        }

        private static int GetInt(Dictionary<string, object?> d, string key)
        {
            if (d.TryGetValue(key, out var v)) return Convert.ToInt32(v);
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // PUBLIC API METHODS
        // ═══════════════════════════════════════════════════════════════════════

        public Dictionary<string, object?> GetServerTime()
            => Request(HttpMethodType.GET, ApiType.Contract, "/contract/ping");

        public Dictionary<string, object?> Ping()
            => Request(HttpMethodType.GET, ApiType.General, "/api/common/ping");

        public Dictionary<string, object?> Validation()
            => Request(HttpMethodType.POST, ApiType.General, "/ucenter/api/login/validation");

        public Dictionary<string, object?> GetWSToken()
            => Request(HttpMethodType.GET, ApiType.General, "/ucenter/api/ws_token");

        public Dictionary<string, object?> GetCustomerInfo()
            => Request(HttpMethodType.GET, ApiType.General, "/ucenter/api/customer_info");

        public Dictionary<string, object?> GetUserInfo()
            => Request(HttpMethodType.GET, ApiType.General, "/ucenter/api/user_info");

        public Dictionary<string, object?> GetLatestsDeposits()
            => Request(HttpMethodType.GET, ApiType.General, "/api/platform/deposit/v3/latest");

        public Dictionary<string, object?> GetDepositAddresses(Dictionary<string, object?> p)
        {
            if (p.TryGetValue("currency", out var currency))
                return Request(HttpMethodType.GET, ApiType.General,
                    $"/api/platform/asset/api/asset/spot/currency/v3?currency={currency}");

            return Request(HttpMethodType.GET, ApiType.General,
                $"/api/platform/asset/api/deposit/address/list?vcoinId={p.GetValueOrDefault("vcoin_id")}");
        }

        public Dictionary<string, object?> GetSecureInfo()
            => Request(HttpMethodType.GET, ApiType.General, "/ucenter/api/secure/check");

        public Dictionary<string, object?> GetOriginInfo()
            => Request(HttpMethodType.GET, ApiType.General, "/ucenter/api/origin_info");

        public Dictionary<string, object?> Logout()
            => Request(HttpMethodType.POST, ApiType.General, "/ucenter/api/logout");

        public Dictionary<string, object?> GetMarketSymbols(Dictionary<string, object?>? p = null)
        {
            var response = Request(HttpMethodType.GET, ApiType.General,
                "/api/platform/spot/market-v2/web/symbolsV2");

            if (p is null || p.Count == 0) return response;

            var (baseCoin, quoteCoin) = ParseSymbol(p);
            if (string.IsNullOrEmpty(baseCoin)) return response;

            var data = GetDict(response, "data");
            if (data is null) return Err(400, $"No {quoteCoin} symbols found");

            if (!data.TryGetValue("symbols", out var symsVal)) return Err(400, $"No {quoteCoin} symbols found");

            Dictionary<string, object?>? quotedSymbols = null;
            if (symsVal is Dictionary<string, object?> symsDict)
                symsDict.TryGetValue(quoteCoin, out var qs);

            // simplified: return response as-is if structure unclear
            return response;
        }

        public Dictionary<string, object?> GetSpotOrderBook(Dictionary<string, object?> p)
        {
            var symbols = GetMarketSymbols(new() { ["symbol"] = p["symbol"] });
            if (!IsSuccess(symbols)) return symbols;

            var list = GetList(symbols, "data");
            var first = list?.Count > 0 ? list[0] as Dictionary<string, object?> : null;

            return Request(HttpMethodType.GET, ApiType.General,
                "/api/platform/spot/market-v2/web/depth/v2", new()
                {
                    ["symbolId"] = first?.GetValueOrDefault("id"),
                    ["decimal"]  = p.GetValueOrDefault("decimal") ?? "0.0000001",
                    ["count"]    = p.GetValueOrDefault("count")   ?? 100,
                });
        }

        public Dictionary<string, object?> CreateSpotOrder(Dictionary<string, object?> p)
        {
            var type    = p["type"]?.ToString();
            var path    = type == "LIMIT_ORDER"
                ? "/api/platform/spot/order/place"
                : "/api/platform/spot/v4/order/place";
            var symbols = GetMarketSymbols(new() { ["symbol"] = p["symbol"] });
            if (!IsSuccess(symbols)) return symbols;

            var list  = GetList(symbols, "data");
            var first = list?.Count > 0 ? list[0] as Dictionary<string, object?> : new();

            return Request(HttpMethodType.POST, ApiType.General, path, new()
            {
                ["orderType"]        = type,
                ["tradeType"]        = p.GetValueOrDefault("side"),
                ["price"]            = p.GetValueOrDefault("price"),
                ["amount"]           = p.GetValueOrDefault("amount"),
                ["quantity"]         = p.GetValueOrDefault("quantity"),
                ["marketCurrencyId"] = first?.GetValueOrDefault("marketCurrencyId"),
                ["currencyId"]       = first?.GetValueOrDefault("coinId"),
                ["orderSource"]      = "WEB",
                ["ts"]               = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }

        public Dictionary<string, object?> CancelSpotOrder(Dictionary<string, object?> p)
            => Request(HttpMethodType.DELETE, ApiType.General,
                "/api/platform/spot/order/cancel/v2", new()
                { ["orderId"] = p["order_id"] });

        public Dictionary<string, object?> GetReferralsList(Dictionary<string, object?>? p = null)
        {
            var (start, end) = WeekRange();
            p ??= new();
            return Request(HttpMethodType.GET, ApiType.General,
                "/api/assetbussiness/invite/invites", new()
                {
                    ["startTime"] = p.GetValueOrDefault("start_time") ?? start,
                    ["endTime"]   = p.GetValueOrDefault("end_time")   ?? end,
                    ["page"]      = p.GetValueOrDefault("page_num")   ?? 1,
                    ["pageSize"]  = p.GetValueOrDefault("page_size")  ?? 10,
                });
        }

        public Dictionary<string, object?> GetAssetsOverview(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            var convert = p.TryGetValue("convert", out var cv) && cv is true;
            var endpoint = convert
                ? "/api/platform/asset/api/asset/overview/convert/v2"
                : "/api/platform/asset/api/asset/overview/v2";
            return Request(HttpMethodType.GET, ApiType.General, endpoint);
        }

        public Dictionary<string, object?> GetFuturesFeeRate()
            => Request(HttpMethodType.GET, ApiType.Futures, "/private/account/contract/fee_rate");

        public Dictionary<string, object?> GetFuturesZeroFeeRate()
            => Request(HttpMethodType.GET, ApiType.Futures, "/private/account/contract/zero_fee_rate");

        public Dictionary<string, object?> GetFuturesTodayPnL()
            => Request(HttpMethodType.GET, ApiType.Futures, "/private/account/asset/analysis/today_pnl");

        public Dictionary<string, object?> GetFuturesAnalysis(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            var now       = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var endTime   = Math.Min(p.TryGetValue("end_time", out var et) ? Convert.ToInt64(et) : now, now);
            var startTime = p.TryGetValue("start_time", out var st)
                ? Convert.ToInt64(st)
                : endTime - 7 * 86_400_000L;

            return Request(HttpMethodType.POST, ApiType.Futures,
                "/private/account/asset/analysis/v3", new()
                {
                    ["currency"]               = p.GetValueOrDefault("currency") ?? "USDT",
                    ["symbol"]                 = p.GetValueOrDefault("symbol"),
                    ["include_unrealised_pnl"] = p.GetValueOrDefault("include_unrealised_pnl") ?? 0,
                    ["reverse"]                = p.GetValueOrDefault("reverse") ?? 0,
                    ["startTime"]              = startTime < endTime ? startTime : endTime - 1000,
                    ["endTime"]                = endTime,
                });
        }

        public Dictionary<string, object?> GetFuturesAssets(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            var endpoint = p.TryGetValue("currency", out var cur) && cur is not null
                ? $"/private/account/asset/{cur}"
                : "/private/account/assets";
            return Request(HttpMethodType.GET, ApiType.Futures, endpoint);
        }

        public Dictionary<string, object?> GetFuturesAssetTransferRecords(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.GET, ApiType.Futures, "/private/account/transfer_record", new()
            {
                ["currency"]  = p.GetValueOrDefault("currency"),
                ["state"]     = p.GetValueOrDefault("state"),
                ["type"]      = p.GetValueOrDefault("type"),
                ["page_num"]  = p.GetValueOrDefault("page_num")  ?? 1,
                ["page_size"] = p.GetValueOrDefault("page_size") ?? 20,
            });
        }

        public Dictionary<string, object?> GetFuturesRiskLimits(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.GET, ApiType.Futures, "/private/account/risk_limit", new()
            { ["symbol"] = p.GetValueOrDefault("symbol") });
        }

        public Dictionary<string, object?> GetFuturesFundingRate(Dictionary<string, object?> p)
            => Request(HttpMethodType.GET, ApiType.Contract, $"/contract/funding_rate/{p["symbol"]}");

        public Dictionary<string, object?> GetFuturesContracts(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.GET, ApiType.Futures, "/contract/detailV2", new()
            { ["symbol"] = p.GetValueOrDefault("symbol") });
        }

        public Dictionary<string, object?> GetFuturesContractIndexPrice(Dictionary<string, object?> p)
            => Request(HttpMethodType.GET, ApiType.Contract, $"/contract/index_price/{p["symbol"]}");

        public Dictionary<string, object?> GetFuturesContractFairPrice(Dictionary<string, object?> p)
            => Request(HttpMethodType.GET, ApiType.Contract, $"/contract/fair_price/{p["symbol"]}");

        public Dictionary<string, object?> GetFuturesContractKlineData(Dictionary<string, object?> p)
        {
            var pt      = PriceTypeExtensions.TryParse(p.GetValueOrDefault("price_type")?.ToString());
            var segment = pt.KlinePathSegment();
            var path    = segment != ""
                ? $"/contract/kline/{segment}/{p["symbol"]}"
                : $"/contract/kline/{p["symbol"]}";

            return Request(HttpMethodType.GET, ApiType.Contract, path, new()
            {
                ["interval"] = p.GetValueOrDefault("interval")   ?? "Min1",
                ["start"]    = p.GetValueOrDefault("start_time"),
                ["end"]      = p.GetValueOrDefault("end_time"),
            });
        }

        public Dictionary<string, object?> GetFuturesTickers(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.GET, ApiType.Futures, "/contract/ticker", new()
            { ["symbol"] = p.GetValueOrDefault("symbol") });
        }

        public Dictionary<string, object?> GetFuturesOpenPositions(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.GET, ApiType.Futures, "/private/position/open_positions",
                FilterParams(new()
                {
                    ["symbol"]     = p.GetValueOrDefault("symbol"),
                    ["positionId"] = p.GetValueOrDefault("position_id"),
                }));
        }

        public Dictionary<string, object?> GetFuturesPositionsHistory(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.GET, ApiType.Futures,
                "/private/position/list/history_positions", new()
                {
                    ["symbol"]    = p.GetValueOrDefault("symbol"),
                    ["type"]      = p.GetValueOrDefault("type"),
                    ["page_num"]  = p.GetValueOrDefault("page_num")  ?? 1,
                    ["page_size"] = p.GetValueOrDefault("page_size") ?? 20,
                });
        }

        public Dictionary<string, object?> CloseAllFuturesPositions()
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/position/close_all");

        public Dictionary<string, object?> ReverseFuturesPosition(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/position/reverse", new()
            {
                ["positionId"] = p["position_id"],
                ["symbol"]     = p["symbol"],
                ["vol"]        = p["vol"],
            });

        public Dictionary<string, object?> GetFuturesPositionMode()
            => Request(HttpMethodType.GET, ApiType.Futures, "/private/position/position_mode");

        public Dictionary<string, object?> ChangeFuturesPositionMode(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/position/change_position_mode", new()
            { ["positionMode"] = p["position_mode"] });

        public Dictionary<string, object?> GetFuturesLeverage(Dictionary<string, object?> p)
            => Request(HttpMethodType.GET, ApiType.Futures, "/private/position/leverage", new()
            { ["symbol"] = p["symbol"] });

        public Dictionary<string, object?> ChangeFuturesPositionMargin(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/position/change_margin", new()
            {
                ["positionId"] = p["position_id"],
                ["amount"]     = p["amount"],
                ["type"]       = p["type"],
            });

        public Dictionary<string, object?> ChangeFuturesPositionLeverage(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/position/change_leverage", new()
            {
                ["positionId"]   = p["position_id"],
                ["leverage"]     = p["leverage"],
                ["openType"]     = p.GetValueOrDefault("open_type"),
                ["symbol"]       = p.GetValueOrDefault("symbol"),
                ["positionType"] = p.GetValueOrDefault("position_type"),
            });

        public Dictionary<string, object?> GetFuturesOrdersDeals(Dictionary<string, object?> p)
        {
            var (start, end) = WeekRange();
            return Request(HttpMethodType.GET, ApiType.Futures, "/private/order/list/order_deals", new()
            {
                ["symbol"]     = p["symbol"],
                ["start_time"] = p.GetValueOrDefault("start_time") ?? start,
                ["end_time"]   = p.GetValueOrDefault("end_time")   ?? end,
                ["page_num"]   = p.GetValueOrDefault("page_num")   ?? 1,
                ["page_size"]  = p.GetValueOrDefault("page_size")  ?? 20,
            });
        }

        public Dictionary<string, object?> GetFuturesPendingOrders(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            var endpoint = p.TryGetValue("symbol", out var sym) && sym is not null
                ? $"/private/order/list/open_orders/{sym}"
                : "/private/order/list/open_orders/";

            return Request(HttpMethodType.GET, ApiType.Futures, endpoint, new()
            {
                ["page_num"]  = p.GetValueOrDefault("page_num")  ?? 1,
                ["page_size"] = p.GetValueOrDefault("page_size") ?? 20,
            });
        }

        public Dictionary<string, object?> GetFuturesOrdersHistory(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            var (start, end) = WeekRange();
            return Request(HttpMethodType.GET, ApiType.Futures, "/private/order/list/history_orders", new()
            {
                ["symbol"]     = p.GetValueOrDefault("symbol"),
                ["states"]     = p.GetValueOrDefault("states"),
                ["category"]   = p.GetValueOrDefault("category"),
                ["side"]       = p.GetValueOrDefault("side"),
                ["start_time"] = p.GetValueOrDefault("start_time") ?? start,
                ["end_time"]   = p.GetValueOrDefault("end_time")   ?? end,
                ["page_num"]   = p.GetValueOrDefault("page_num")   ?? 1,
                ["page_size"]  = p.GetValueOrDefault("page_size")  ?? 20,
            });
        }

        public Dictionary<string, object?> GetFuturesOpenLimitOrders(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.GET, ApiType.Futures, "/private/order/list/open_orders", new()
            { ["page_size"] = p.GetValueOrDefault("page_size") ?? 200 });
        }

        public Dictionary<string, object?> GetFuturesOpenStopOrders(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.GET, ApiType.Futures, "/private/stoporder/open_orders", new()
            { ["page_size"] = p.GetValueOrDefault("page_size") ?? 200 });
        }

        public Dictionary<string, object?> GetFuturesOpenOrders(Dictionary<string, object?>? p = null)
        {
            var pageSize = p?.GetValueOrDefault("page_size") ?? 200;
            var result   = Batch(new()
            {
                ["limit_orders"] = () => GetFuturesOpenLimitOrders(new() { ["page_size"] = pageSize }),
                ["stop_orders"]  = () => GetFuturesOpenStopOrders(new()  { ["page_size"] = pageSize }),
            });

            var merged = new List<object?>();
            if (IsSuccess(result["limit_orders"]))
                merged.AddRange(GetList(result["limit_orders"], "data") ?? new());
            if (IsSuccess(result["stop_orders"]))
                merged.AddRange(GetList(result["stop_orders"],  "data") ?? new());

            return new()
            {
                ["success"] = true,
                ["code"]    = 0,
                ["data"]    = merged,
            };
        }

        public Dictionary<string, object?> GetFuturesClosedOrders(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.GET, ApiType.Futures, "/private/order/close_orders", new()
            {
                ["symbol"]    = p.GetValueOrDefault("symbol"),
                ["category"]  = p.GetValueOrDefault("category"),
                ["page_num"]  = p.GetValueOrDefault("page_num")  ?? 1,
                ["page_size"] = p.GetValueOrDefault("page_size") ?? 20,
            });
        }

        public Dictionary<string, object?> GetFuturesOrdersById(Dictionary<string, object?> p)
        {
            var ids = p["ids"]?.ToString()?.Split(',').Select(s => s.Trim()).ToArray()
                      ?? Array.Empty<string>();

            return ids.Length > 1
                ? Request(HttpMethodType.GET, ApiType.Futures, "/private/order/batch_query",
                    new() { ["order_ids"] = p["ids"] })
                : Request(HttpMethodType.GET, ApiType.Futures, $"/private/order/get/{p["ids"]}");
        }

        public Dictionary<string, object?> CreateFuturesOrder(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/order/create",
                FilterParams(new()
                {
                    ["symbol"]          = p["symbol"],
                    ["price"]           = p.GetValueOrDefault("price"),
                    ["type"]            = p["type"],
                    ["openType"]        = p.GetValueOrDefault("open_type"),
                    ["positionMode"]    = p.GetValueOrDefault("position_mode"),
                    ["side"]            = p["side"],
                    ["vol"]             = p["vol"],
                    ["leverage"]        = p.GetValueOrDefault("leverage"),
                    ["positionId"]      = p.GetValueOrDefault("position_id"),
                    ["externalOid"]     = p.GetValueOrDefault("external_id"),
                    ["takeProfitPrice"] = p.GetValueOrDefault("take_profit_price"),
                    ["profitTrend"]     = p.GetValueOrDefault("take_profit_trend") ?? 1,
                    ["stopLossPrice"]   = p.GetValueOrDefault("stop_loss_price"),
                    ["lossTrend"]       = p.GetValueOrDefault("stop_loss_trend") ?? 1,
                    ["priceProtect"]    = p.GetValueOrDefault("price_protect") ?? 0,
                    ["reduceOnly"]      = p.GetValueOrDefault("reduce_only") ?? false,
                    ["marketCeiling"]   = p.GetValueOrDefault("market_ceiling") ?? false,
                    ["flashClose"]      = p.GetValueOrDefault("flash_close"),
                    ["bboTypeNum"]      = p.GetValueOrDefault("bbo_type"),
                }));

        public Dictionary<string, object?> CancelFuturesOrders(Dictionary<string, object?> p)
        {
            List<string> ids;
            if (p["ids"] is List<string> list)
                ids = list;
            else
                ids = p["ids"]?.ToString()?.Split(',').Select(s => s.Trim()).ToList() ?? new();

            // POST body is array — serialize as array
            var payload = new Dictionary<string, object?> { ["__array__"] = ids };
            return Request(HttpMethodType.POST, ApiType.Futures, "/private/order/cancel", payload);
        }

        public Dictionary<string, object?> CancelFuturesOrderWithExternalId(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures,
                "/private/order/cancel_with_external", new()
                {
                    ["symbol"]      = p["symbol"],
                    ["externalOid"] = p["external_id"],
                });

        public Dictionary<string, object?> CancelAllFuturesOrders(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.POST, ApiType.Futures, "/private/order/cancel_all", new()
            { ["symbol"] = p.GetValueOrDefault("symbol") });
        }

        public Dictionary<string, object?> ChangeFuturesLimitOrderPrice(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "private/order/change_limit_order", new()
            {
                ["orderId"] = p["order_id"],
                ["price"]   = p["price"],
                ["vol"]     = p["vol"],
            });

        public Dictionary<string, object?> ChaseFuturesOrder(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/order/chase_limit_order", new()
            { ["orderId"] = p["order_id"] });

        public Dictionary<string, object?> CreateFuturesChaseOrder(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/order/chase_limit/place",
                FilterParams(new()
                {
                    ["chaseType"]        = p["chase_type"],
                    ["distanceType"]     = p.GetValueOrDefault("distance_type"),
                    ["distanceValue"]    = p.GetValueOrDefault("distance_value"),
                    ["maxDistanceType"]  = p.GetValueOrDefault("max_distance_type"),
                    ["maxDistanceValue"] = p.GetValueOrDefault("max_distance_value"),
                    ["leverage"]         = p["leverage"],
                    ["openType"]         = p["open_type"],
                    ["side"]             = p["side"],
                    ["symbol"]           = p["symbol"],
                    ["vol"]              = p["vol"],
                }));

        public Dictionary<string, object?> CreateFuturesStopOrder(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/stoporder/place/v2", new()
            {
                ["positionId"]           = p["position_id"],
                ["volType"]              = p.GetValueOrDefault("vol_type")                ?? 2,
                ["vol"]                  = p.GetValueOrDefault("vol"),
                ["takeProfitType"]       = p.GetValueOrDefault("take_profit_type"),
                ["takeProfitOrderPrice"] = p.GetValueOrDefault("take_profit_order_price"),
                ["takeProfitPrice"]      = p.GetValueOrDefault("take_profit_price"),
                ["takeProfitReverse"]    = p.GetValueOrDefault("take_profit_reverse")     ?? 2,
                ["profitTrend"]          = p.GetValueOrDefault("take_profit_trend")       ?? 1,
                ["takeProfitVol"]        = p.GetValueOrDefault("take_profit_volume"),
                ["stopLossType"]         = p.GetValueOrDefault("stop_loss_type"),
                ["stopLossOrderPrice"]   = p.GetValueOrDefault("stop_loss_order_price"),
                ["stopLossPrice"]        = p.GetValueOrDefault("stop_loss_price"),
                ["stopLossReverse"]      = p.GetValueOrDefault("stop_loss_reverse")       ?? 2,
                ["lossTrend"]            = p.GetValueOrDefault("stop_loss_trend")         ?? 1,
                ["profitLossVolType"]    = p.GetValueOrDefault("profit_loss_vol_type")    ?? "SAME",
                ["priceProtect"]         = p.GetValueOrDefault("price_protect")           ?? 0,
            });

        public Dictionary<string, object?> GetFuturesStopLimitOrders(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            var (start, end) = WeekRange();
            return Request(HttpMethodType.GET, ApiType.Futures, "/private/stoporder/list/orders", new()
            {
                ["symbol"]      = p.GetValueOrDefault("symbol"),
                ["is_finished"] = p.GetValueOrDefault("is_finished"),
                ["start_time"]  = p.GetValueOrDefault("start_time") ?? start,
                ["end_time"]    = p.GetValueOrDefault("end_time")   ?? end,
                ["page_num"]    = p.GetValueOrDefault("page_num")   ?? 1,
                ["page_size"]   = p.GetValueOrDefault("page_size")  ?? 20,
            });
        }

        public Dictionary<string, object?> CancelStopLimitOrders(Dictionary<string, object?> p)
        {
            List<string> ids;
            if (p["ids"] is string s)
                ids = s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            else if (p["ids"] is List<string> l)
                ids = l;
            else
                ids = new();

            var payload = ids.Select(id => new Dictionary<string, object?> { ["stopPlanOrderId"] = int.Parse(id) })
                             .ToList<object?>();

            return Request(HttpMethodType.POST, ApiType.Futures, "/private/stoporder/cancel",
                new() { ["__array__"] = payload });
        }

        public Dictionary<string, object?> CancelAllFuturesStopLimitOrders(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.POST, ApiType.Futures, "/private/stoporder/cancel_all", new()
            {
                ["positionId"] = p.GetValueOrDefault("position_id"),
                ["symbol"]     = p.GetValueOrDefault("symbol"),
            });
        }

        public Dictionary<string, object?> ChangeFuturesOrderStopLimitPrice(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/stoporder/change_price", new()
            {
                ["orderId"]         = p["order_id"],
                ["takeProfitPrice"] = p.GetValueOrDefault("take_profit_price"),
                ["stopLossPrice"]   = p.GetValueOrDefault("stop_loss_pirce"),   // typo preserved from PHP
            });

        public Dictionary<string, object?> ChangeFuturesPlanOrderStopLimitPrice(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/stoporder/change_plan_price", new()
            {
                ["stopPlanOrderId"] = p["order_id"],
                ["takeProfitPrice"] = p.GetValueOrDefault("take_profit_price"),
                ["stopLossPrice"]   = p.GetValueOrDefault("stop_loss_pirce"),   // typo preserved from PHP
            });

        public Dictionary<string, object?> ChangeFuturesOrderTargets(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/stoporder/change_plan_order", new()
            {
                ["orderId"]          = p["real_order_id"],
                ["profitTrend"]      = p.GetValueOrDefault("take_profit_trend"),
                ["takeProfitPrice"]  = p.GetValueOrDefault("take_profit_price"),
                ["takeProfitVolume"] = p.GetValueOrDefault("take_profit_volume"),
                ["lossTrend"]        = p.GetValueOrDefault("stop_loss_trend"),
                ["stopLossPrice"]    = p.GetValueOrDefault("stop_loss_price"),
                ["stopLossVolume"]   = p.GetValueOrDefault("stop_loss_volume"),
            });

        public Dictionary<string, object?> CreateFuturesTriggerOrder(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/planorder/place", new()
            {
                ["symbol"]       = p["symbol"],
                ["price"]        = p.GetValueOrDefault("price"),
                ["vol"]          = p["vol"],
                ["leverage"]     = p.GetValueOrDefault("leverage"),
                ["side"]         = p["side"],
                ["openType"]     = p["open_type"],
                ["triggerPrice"] = p["trigger_price"],
                ["triggerType"]  = p["trigger_type"],
                ["executeCycle"] = p["execute_cycle"],
                ["orderType"]    = p["order_type"],
                ["trend"]        = p["trend"],
            });

        public Dictionary<string, object?> GetFuturesTriggerOrders(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            var (start, end) = WeekRange();
            return Request(HttpMethodType.GET, ApiType.Futures, "/private/planorder/list/orders", new()
            {
                ["symbol"]     = p.GetValueOrDefault("symbol"),
                ["states"]     = p.GetValueOrDefault("states"),
                ["start_time"] = p.GetValueOrDefault("start_time") ?? start,
                ["end_time"]   = p.GetValueOrDefault("end_time")   ?? end,
                ["page_num"]   = p.GetValueOrDefault("page_num")   ?? 1,
                ["page_size"]  = p.GetValueOrDefault("page_size")  ?? 20,
            });
        }

        public Dictionary<string, object?> CancelFuturesTriggerOrders(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/planorder/cancel",
                new() { ["__array__"] = p["ids"] });

        public Dictionary<string, object?> CancelAllFuturesTriggerOrders(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.POST, ApiType.Futures, "/private/planorder/cancel_all", new()
            { ["symbol"] = p.GetValueOrDefault("symbol") });
        }

        public Dictionary<string, object?> CreateFuturesTrailingOrder(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/trackorder/place",
                FilterParams(new()
                {
                    ["symbol"]       = p["symbol"],
                    ["leverage"]     = p["leverage"],
                    ["side"]         = p["side"],
                    ["vol"]          = p["vol"],
                    ["openType"]     = p["open_type"],
                    ["trend"]        = p["trend"],
                    ["activePrice"]  = p["active_price"],
                    ["backType"]     = p["back_type"],
                    ["backValue"]    = p["back_value"],
                    ["positionMode"] = p.GetValueOrDefault("position_mode") ?? 0,
                    ["reduceOnly"]   = p.GetValueOrDefault("reduce_only"),
                }));

        public Dictionary<string, object?> CancelFuturesTrailingOrder(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/trackorder/cancel", new()
            {
                ["symbol"]       = p.GetValueOrDefault("symbol"),
                ["trackOrderId"] = p.GetValueOrDefault("order_id"),
            });

        public Dictionary<string, object?> ChangeFuturesTrailingOrder(Dictionary<string, object?> p)
            => Request(HttpMethodType.POST, ApiType.Futures, "/private/trackorder/change_order", new()
            {
                ["symbol"]       = p["symbol"],
                ["trackOrderId"] = p["order_id"],
                ["trend"]        = p["trend"],
                ["activePrice"]  = p["active_price"],
                ["backType"]     = p["back_type"],
                ["backValue"]    = p["back_value"],
                ["vol"]          = p["vol"],
            });

        public Dictionary<string, object?> CalculateFuturesPositionPnL(Dictionary<string, object?> p)
        {
            var result = Batch(new()
            {
                ["contract"] = () => GetFuturesContracts(new() { ["symbol"] = p["symbol"] }),
                ["ticker"]   = () => GetFuturesTickers(new()   { ["symbol"] = p["symbol"] }),
            });

            var contractList = GetList(result["contract"], "data");
            var contract     = contractList?.Count > 0 ? contractList[0] as Dictionary<string, object?> : null;
            var tickData     = GetDict(result["ticker"], "data");

            if (contract is null || tickData is null || GetDouble(contract, "contractSize") == 0)
                return Err(-1, "Unable to calculate PnL: missing market data");

            var pt      = PriceTypeExtensions.TryParse(p.GetValueOrDefault("price_type")?.ToString());
            var price   = Convert.ToDouble(tickData.GetValueOrDefault(pt.TickerField()) ?? 0);
            var entry   = Convert.ToDouble(p["entry_price"]);
            var volume  = Convert.ToDouble(p["volume"]);
            var lev     = Math.Max(1, Convert.ToInt32(p.GetValueOrDefault("leverage") ?? 1));
            var cs      = GetDouble(contract, "contractSize");
            var isLong  = Convert.ToInt32(p["side"]) == 1;
            var pnl     = isLong ? (price - entry) * volume * cs : (entry - price) * volume * cs;
            var margin  = entry * volume * cs / lev;
            var ps      = GetInt(contract, "priceScale");

            return new()
            {
                ["success"] = true,
                ["code"]    = 0,
                ["data"]    = new Dictionary<string, object?>
                {
                    ["pnl"]           = Math.Round(pnl, 4),
                    ["pnl_percent"]   = margin > 0 ? Math.Round(pnl / margin * 100, 2) : 0.0,
                    ["volume"]        = volume,
                    ["entry_price"]   = entry,
                    ["current_price"] = price,
                    ["price_type"]    = pt.Value(),
                },
            };
        }

        public Dictionary<string, object?> CalculateFuturesVolume(Dictionary<string, object?> p)
        {
            foreach (var req in new[] { "symbol", "amount", "leverage" })
                if (!p.ContainsKey(req) || p[req] is null || p[req]?.ToString() == "")
                    return Err(404, $"Missing required parameter: {req}");

            var (ok, error, market) = FetchTickerAndContract(p["symbol"]!.ToString()!);
            if (!ok) return error;

            var (ticker, cd, pt) = ResolveMarket(market, p);
            if (ticker is null)                      return Err(400, "Invalid price value");
            var amount   = Convert.ToDouble(p["amount"]);
            var leverage = Convert.ToInt32(p["leverage"]);
            if (amount   <= 0) return Err(400, "Amount must be positive");
            if (leverage <= 0) return Err(400, "Leverage must be positive");

            var cs = GetDouble(cd, "contractSize");
            if (cs <= 0) return Err(400, "Invalid contract size");

            var price  = double.Parse(ticker);
            var minL   = GetInt(cd, "minLeverage");
            var maxL   = GetInt(cd, "maxLeverage");
            leverage   = (int)Math.Max(minL, Math.Min(leverage, maxL));
            var vol    = NormalizeVolume(amount * leverage / (price * cs), cd);
            var ps     = GetInt(cd, "priceScale");
            var usdt   = Math.Round(vol * price * cs / leverage, ps);

            return new()
            {
                ["success"] = true,
                ["code"]    = 0,
                ["data"]    = VolumePayload(cd, vol, leverage, price, usdt, pt),
            };
        }

        public Dictionary<string, object?> CalculateFuturesVolumeFromBaseAmount(Dictionary<string, object?> p)
        {
            foreach (var req in new[] { "symbol", "amount", "leverage" })
                if (!p.ContainsKey(req) || p[req] is null || p[req]?.ToString() == "")
                    return Err(404, $"Missing required parameter: {req}");

            var (ok, error, market) = FetchTickerAndContract(p["symbol"]!.ToString()!);
            if (!ok) return error;

            var (ticker, cd, pt) = ResolveMarket(market, p);
            if (ticker is null)                       return Err(400, "Invalid price value");
            var amount   = Convert.ToDouble(p["amount"]);
            var leverage = Convert.ToInt32(p["leverage"]);
            if (amount   <= 0) return Err(400, "Base amount must be positive");
            if (leverage <= 0) return Err(400, "Leverage must be positive");

            var cs = GetDouble(cd, "contractSize");
            if (cs <= 0) return Err(400, "Invalid contract size");

            var price    = double.Parse(ticker);
            var minL     = GetInt(cd, "minLeverage");
            var maxL     = GetInt(cd, "maxLeverage");
            leverage     = (int)Math.Max(minL, Math.Min(leverage, maxL));
            var vol      = NormalizeVolume(amount / cs, cd);
            var notional = amount * price;
            var ps       = GetInt(cd, "priceScale");
            var usdt     = Math.Round(notional, ps);

            var data = VolumePayload(cd, vol, leverage, price, usdt, pt);
            data["required_margin"] = Math.Round(notional / leverage, ps);
            data["amount"]          = amount;
            data["notional_value"]  = notional;
            data["contract_size"]   = cs;

            return new() { ["success"] = true, ["code"] = 0, ["data"] = data };
        }

        public Dictionary<string, object?> ReceiveFuturesTestnetAsset(Dictionary<string, object?>? p = null)
        {
            p ??= new();
            return Request(HttpMethodType.POST, ApiType.Other,
                "https://futures.testnet.mexc.com/mock/contract/asset/receive", new()
                {
                    ["currency"] = p.GetValueOrDefault("currency") ?? "USDT",
                    ["amount"]   = p.GetValueOrDefault("amount"),
                });
        }

        public void Dispose() => _httpFactory.Dispose();
    }
}