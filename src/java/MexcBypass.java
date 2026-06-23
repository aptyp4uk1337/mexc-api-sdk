import java.math.BigInteger;
import java.net.*;
import java.net.http.*;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.time.*;
import java.time.temporal.TemporalAdjusters;
import java.util.*;
import java.util.concurrent.*;
import java.util.function.Supplier;
import java.util.stream.Collectors;

/**
 * MexcBypass — unofficial MEXC Futures/Spot HTTP client.
 *
 * Single-file version: all supporting types are nested static classes/enums/records.
 * Requires Java 21+. Zero external dependencies.
 *
 * Quick start<
 * {@code
 * var client = new MexcBypass("YOUR_WEB_KEY");
 *
 * // open positions
 * Map<String, Object> positions = client.getFuturesOpenPositions(Map.of());
 *
 * // parallel batch
 * var result = client.batch(Map.of(
 *     "ticker",   () -> client.getFuturesTickers(Map.of("symbol", "BTC_USDT")),
 *     "balance",  () -> client.getFuturesAssets(Map.of("currency", "USDT"))
 * ));
 * }
 */
public final class MexcBypass {
    enum ApiType { GENERAL, FUTURES, CONTRACT, OTHER }

    enum HttpMethod {
        GET, POST, PUT, DELETE;
        boolean hasBody() { return this == POST || this == PUT; }
    }

    enum PriceType {
        LAST_PRICE("last_price"),
        FAIR_PRICE("fair_price"),
        INDEX_PRICE("index_price");

        private final String val;
        PriceType(String val) { this.val = val; }

        String value() { return val; }

        String tickerField() {
            return switch (this) {
                case FAIR_PRICE  -> "fairPrice";
                case INDEX_PRICE -> "indexPrice";
                case LAST_PRICE  -> "lastPrice";
            };
        }

        String klinePathSegment() {
            return switch (this) {
                case INDEX_PRICE -> "index_price";
                case FAIR_PRICE  -> "fair_price";
                case LAST_PRICE  -> "";
            };
        }

        static PriceType fromValue(String v) {
            if (v == null) return LAST_PRICE;
            for (PriceType pt : values()) if (pt.val.equalsIgnoreCase(v)) return pt;
            return LAST_PRICE;
        }
    }

    // ── records ───────────────────────────────────────────────────────────────

    record SignatureResult(String timestamp, String sign) {}

    record PendingRequest(
        String id, String url, HttpMethod method,
        List<String> headers, Map<String, Object> body, String endpoint
    ) {
        PendingRequest(String url, HttpMethod method,
                       List<String> headers, Map<String, Object> body, String endpoint) {
            this(UUID.randomUUID().toString(), url, method, headers, body, endpoint);
        }
    }

    /**
     * Proxy configuration supporting HTTP/HTTPS and SOCKS5.
     * URL format: {@code socks5://user:pass@host:1080} or {@code http://host:8080}
     */
    record ProxyConfig(String host, int port, ProxyType type, String username, String password) {
        enum ProxyType { HTTP, SOCKS5 }

        static ProxyConfig fromUrl(String url) {
            if (url == null || url.isBlank()) return null;
            try {
                URI    uri      = URI.create(url);
                String scheme   = uri.getScheme() == null ? "http" : uri.getScheme().toLowerCase();
                String host     = uri.getHost();
                int    port     = uri.getPort() == -1 ? 1080 : uri.getPort();
                ProxyType type  = scheme.startsWith("socks") ? ProxyType.SOCKS5 : ProxyType.HTTP;
                String username = null, password = null;
                if (uri.getUserInfo() != null) {
                    String[] parts = uri.getUserInfo().split(":", 2);
                    username = parts[0];
                    password = parts.length > 1 ? parts[1] : null;
                }
                return host == null ? null : new ProxyConfig(host, port, type, username, password);
            } catch (Exception e) { return null; }
        }

        Proxy toJavaProxy() {
            InetSocketAddress addr = new InetSocketAddress(host, port);
            return type == ProxyType.SOCKS5 ? new Proxy(Proxy.Type.SOCKS, addr) : new Proxy(Proxy.Type.HTTP, addr);
        }

        boolean hasAuth() { return username != null && !username.isEmpty(); }
    }

    static final class SignatureGenerator {
        private final String apiKey;
        private volatile long lastMs      = 0;
        private volatile int  msIncrement = 0;

        SignatureGenerator(String apiKey) { this.apiKey = apiKey == null ? "" : apiKey; }

        SignatureResult generate(Map<String, Object> payload, HttpMethod method) {
            return generate(payload, method, null);
        }

        SignatureResult generate(Map<String, Object> payload, HttpMethod method, String forceTime) {
            String time = forceTime != null ? forceTime : uniqueMs();
            String g    = md5(apiKey + time).substring(7);
            String sign = method.hasBody()
                ? md5(time + (payload.isEmpty() ? "" : Json.encode(payload)) + g)
                : md5(time + buildQuery(payload) + g);
            return new SignatureResult(time, sign);
        }

        private synchronized String uniqueMs() {
            long now = System.currentTimeMillis();
            if (now == lastMs) msIncrement++; else { lastMs = now; msIncrement = 0; }
            return String.valueOf(now + msIncrement);
        }

        static String md5(String input) {
            try {
                return String.format("%032x",
                    new BigInteger(1, MessageDigest.getInstance("MD5")
                        .digest(input.getBytes(StandardCharsets.UTF_8))));
            } catch (Exception e) { throw new RuntimeException("MD5 unavailable", e); }
        }

        private static String buildQuery(Map<String, Object> p) {
            if (p.isEmpty()) return "";
            return p.entrySet().stream()
                .map(e -> pctEncode(e.getKey()) + "=" + pctEncode(e.getValue().toString()))
                .collect(Collectors.joining("&"));
        }

        static String pctEncode(String s) {
            try {
                return URLEncoder.encode(s, StandardCharsets.UTF_8)
                    .replace("+", "%20").replace("*", "%2A").replace("%7E", "~");
            } catch (Exception e) { return s; }
        }
    }

    static final class Json {
        static String encode(Object obj) {
            if (obj == null)             return "null";
            if (obj instanceof String s) return "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"")
                                                         .replace("\n", "\\n").replace("\r", "\\r")
                                                         .replace("\t", "\\t") + "\"";
            if (obj instanceof Number || obj instanceof Boolean) return obj.toString();
            if (obj instanceof Map<?,?> m) {
                return "{" + m.entrySet().stream()
                    .map(e -> encode(e.getKey().toString()) + ":" + encode(e.getValue()))
                    .collect(Collectors.joining(",")) + "}";
            }
            if (obj instanceof Iterable<?> it) {
                StringJoiner sj = new StringJoiner(",", "[", "]");
                for (Object item : it) sj.add(encode(item));
                return sj.toString();
            }
            return "\"" + obj + "\"";
        }

        @SuppressWarnings("unchecked")
        static Object decode(String src) { return new Parser(src).parse(); }

        private static final class Parser {
            private final String src; private int pos;
            Parser(String src) { this.src = src; }

            Object parse() {
                skipWs();
                if (pos >= src.length()) throw new RuntimeException("Empty JSON");
                return switch (src.charAt(pos)) {
                    case '{' -> parseObject();
                    case '[' -> parseArray();
                    case '"' -> parseString();
                    case 't' -> literal("true",  Boolean.TRUE);
                    case 'f' -> literal("false", Boolean.FALSE);
                    case 'n' -> literal("null",  null);
                    default  -> parseNumber();
                };
            }

            private Map<String, Object> parseObject() {
                expect('{'); Map<String, Object> m = new LinkedHashMap<>();
                skipWs(); if (peek() == '}') { pos++; return m; }
                while (true) {
                    skipWs(); String k = parseString(); skipWs(); expect(':'); skipWs();
                    m.put(k, parse()); skipWs();
                    char c = src.charAt(pos++);
                    if (c == '}') break;
                    if (c != ',') throw new RuntimeException("Bad object at " + pos);
                }
                return m;
            }

            private List<Object> parseArray() {
                expect('['); List<Object> l = new ArrayList<>();
                skipWs(); if (peek() == ']') { pos++; return l; }
                while (true) {
                    skipWs(); l.add(parse()); skipWs();
                    char c = src.charAt(pos++);
                    if (c == ']') break;
                    if (c != ',') throw new RuntimeException("Bad array at " + pos);
                }
                return l;
            }

            private String parseString() {
                expect('"'); StringBuilder sb = new StringBuilder();
                while (pos < src.length()) {
                    char c = src.charAt(pos++);
                    if (c == '"') return sb.toString();
                    if (c == '\\') {
                        char e = src.charAt(pos++);
                        sb.append(switch (e) {
                            case '"'  -> '"';  case '\\' -> '\\'; case '/' -> '/';
                            case 'n'  -> '\n'; case 'r'  -> '\r'; case 't' -> '\t';
                            case 'b'  -> '\b'; case 'f'  -> '\f';
                            case 'u'  -> { String h = src.substring(pos, pos+4); pos += 4;
                                           yield (char) Integer.parseInt(h, 16); }
                            default -> e;
                        });
                    } else sb.append(c);
                }
                throw new RuntimeException("Unterminated string");
            }

            private Number parseNumber() {
                int start = pos;
                if (pos < src.length() && src.charAt(pos) == '-') pos++;
                while (pos < src.length() && Character.isDigit(src.charAt(pos))) pos++;
                boolean f = false;
                if (pos < src.length() && src.charAt(pos) == '.') { f = true; pos++;
                    while (pos < src.length() && Character.isDigit(src.charAt(pos))) pos++; }
                if (pos < src.length() && (src.charAt(pos)=='e'||src.charAt(pos)=='E')) { f = true; pos++;
                    if (pos < src.length() && (src.charAt(pos)=='+'||src.charAt(pos)=='-')) pos++;
                    while (pos < src.length() && Character.isDigit(src.charAt(pos))) pos++; }
                String n = src.substring(start, pos);
                try { return f ? Double.parseDouble(n) : Long.parseLong(n); }
                catch (NumberFormatException ex) { return Double.parseDouble(n); }
            }

            private Object literal(String expected, Object value) {
                if (src.startsWith(expected, pos)) { pos += expected.length(); return value; }
                throw new RuntimeException("Unexpected token at " + pos);
            }
            private void skipWs() { while (pos < src.length() && src.charAt(pos) <= ' ') pos++; }
            private void expect(char c) {
                if (pos >= src.length() || src.charAt(pos) != c)
                    throw new RuntimeException("Expected '" + c + "' at " + pos);
                pos++;
            }
            private char peek() { return pos < src.length() ? src.charAt(pos) : 0; }
        }
    }

    static final class HttpFactory {
        private static final Duration CONNECT_TIMEOUT = Duration.ofSeconds(3);
        private static final Duration REQUEST_TIMEOUT = Duration.ofSeconds(5);

        private final HttpClient client;

        HttpFactory(ProxyConfig proxy) {
            HttpClient.Builder b = HttpClient.newBuilder()
                .version(HttpClient.Version.HTTP_2)
                .connectTimeout(CONNECT_TIMEOUT)
                .followRedirects(HttpClient.Redirect.NEVER);

            if (proxy != null) {
                b.proxy(ProxySelector.of(new InetSocketAddress(proxy.host(), proxy.port())));
                if (proxy.hasAuth()) {
                    b.authenticator(new Authenticator() {
                        @Override protected PasswordAuthentication getPasswordAuthentication() {
                            return new PasswordAuthentication(proxy.username(),
                                proxy.password() != null ? proxy.password().toCharArray() : new char[0]);
                        }
                    });
                }
            }
            this.client = b.build();
        }

        HttpResponse<String> execute(String url, HttpMethod method,
                                     List<String> headers, Map<String, Object> body) throws Exception {
            HttpRequest.Builder rb = HttpRequest.newBuilder()
                .uri(URI.create(url))
                .timeout(REQUEST_TIMEOUT);

            for (String h : headers) {
                int colon = h.indexOf(':');
                if (colon <= 0) continue;
                try { rb.header(h.substring(0, colon).trim(), h.substring(colon + 1).trim()); }
                catch (IllegalArgumentException ignored) {}
            }

            HttpRequest.BodyPublisher publisher = (body != null && !body.isEmpty())
                ? HttpRequest.BodyPublishers.ofString(Json.encode(body))
                : HttpRequest.BodyPublishers.noBody();

            rb.method(method.name(), publisher);
            return client.send(rb.build(), HttpResponse.BodyHandlers.ofString());
        }
    }

    static final class ResponseHandler {

        private static final Map<String, Map<String, String>> RENAME = new LinkedHashMap<>();
        static {
            Map<String, String> cd = new LinkedHashMap<>();
            cd.put("dn","displayName"); cd.put("dne","displayNameEn"); cd.put("pot","positionOpenType");
            cd.put("bc","baseCoin"); cd.put("qc","quoteCoin"); cd.put("bcn","baseCoinName");
            cd.put("qcn","quoteCoinName"); cd.put("ft","futureType"); cd.put("sc","settleCoin");
            cd.put("cs","contractSize"); cd.put("minL","minLeverage"); cd.put("maxL","maxLeverage");
            cd.put("ccMaxL","countryConfigContractMaxLeverage"); cd.put("ps","priceScale");
            cd.put("vs","volScale"); cd.put("as","amountScale"); cd.put("pu","priceUnit");
            cd.put("vu","volUnit"); cd.put("minV","minVol"); cd.put("maxV","maxVol");
            cd.put("blpr","bidLimitPriceRate"); cd.put("alpr","askLimitPriceRate");
            cd.put("tfr","takerFeeRate"); cd.put("mfr","makerFeeRate");
            cd.put("mmr","maintenanceMarginRate"); cd.put("imr","initialMarginRate");
            cd.put("rbv","riskBaseVol"); cd.put("riv","riskIncrVol");
            cd.put("rlss","riskLongShortSwitch"); cd.put("rim","riskIncrMmr");
            cd.put("rii","riskIncrImr"); cd.put("rll","riskLevelLimit");
            cd.put("pcv","priceCoefficientVariation"); cd.put("io","indexOrigin");
            cd.put("in","isNew"); cd.put("ih","isHot"); cd.put("ihd","isHidden");
            cd.put("ip","isPromoted"); cd.put("cp","conceptPlate"); cd.put("cpi","conceptPlateId");
            cd.put("rlt","riskLimitType"); cd.put("mno","maxNumOrders");
            cd.put("moml","marketOrderMaxLevel"); cd.put("moplr1","marketOrderPriceLimitRate1");
            cd.put("moplr2","marketOrderPriceLimitRate2"); cd.put("tp","triggerProtect");
            cd.put("ae","appraisal"); cd.put("sac","showAppraisalCountdown");
            cd.put("ad","automaticDelivery"); cd.put("aa","apiAllowed");
            cd.put("dsl","depthStepList"); cd.put("lmv","limitMaxVol"); cd.put("tsd","threshold");
            cd.put("bciu","baseCoinIconUrl"); cd.put("bcid","baseCoinId");
            cd.put("ct","createTime"); cd.put("ot","openingTime");
            cd.put("oco","openingCountdownOption"); cd.put("sbo","showBeforeOpen");
            cd.put("iml","isMaxLeverage"); cd.put("izfr","isZeroFeeRate");
            cd.put("rlm","riskLimitMode"); cd.put("rlcs","riskLimitCustom");
            cd.put("izfs","isZeroFeeSymbol"); cd.put("liqfr","liquidationFeeRate");
            cd.put("frm","feeRateMode"); cd.put("levfrs","leverageFeeRates");
            cd.put("tiefrs","tieredFeeRates");
            RENAME.put("/contract/detailV2", cd);

            Map<String, String> ss = new LinkedHashMap<>();
            ss.put("mcd","marketCurrencyId"); ss.put("cd","coinId"); ss.put("vn","currency");
            ss.put("fn","currencyFullName"); ss.put("srt","sortOrder"); ss.put("sts","status");
            ss.put("tp","marketType"); ss.put("in","icon"); ss.put("ot","openingTime");
            ss.put("cp","categories"); ss.put("ci","categories_ids"); ss.put("ps","priceScale");
            ss.put("qs","quantityScale"); ss.put("cdm","contractDecimalMultiplier");
            ss.put("st","spotEnabled"); ss.put("dst","depositStatus"); ss.put("tt","tradingType");
            ss.put("ca","contractAddress"); ss.put("fne","currencyFullNameEn");
            RENAME.put("/api/platform/spot/market-v2/web/symbolsV2", ss);
        }

        private final boolean isTestnet;
        ResponseHandler(boolean isTestnet) { this.isTestnet = isTestnet; }

        @SuppressWarnings("unchecked")
        Map<String, Object> handle(String raw, int status, String endpoint) {
            if (raw == null || raw.isBlank()) return error(status, "Empty response");
            Map<String, Object> decoded;
            try {
                Object parsed = Json.decode(raw.trim());
                if (!(parsed instanceof Map)) return error(status, "Unexpected JSON root");
                decoded = (Map<String, Object>) parsed;
            } catch (Exception ex) {
                String preview = raw.length() > 200 ? raw.substring(0, 200) : raw;
                return error(status, "Invalid JSON: " + ex.getMessage() + " | raw: " + preview);
            }
            if (decoded.get("data") instanceof Map && endpoint != null)
                decoded.put("data", renameFields((Map<String, Object>) decoded.get("data"), endpoint));
            decoded.put("is_testnet", isTestnet);
            return decoded;
        }

        Map<String, Object> error(int code, String message) {
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("success", false); m.put("code", code); m.put("message", message);
            m.put("timestamp", System.currentTimeMillis()); m.put("is_testnet", isTestnet);
            return m;
        }

        private static Map<String, Object> renameFields(Map<String, Object> data, String endpoint) {
            for (var e : RENAME.entrySet())
                if (endpoint.contains(e.getKey())) return recursiveRename(data, e.getValue());
            return data;
        }

        @SuppressWarnings("unchecked")
        private static Map<String, Object> recursiveRename(Map<String, Object> m, Map<String, String> map) {
            Map<String, Object> out = new LinkedHashMap<>();
            m.forEach((k, v) -> out.put(map.getOrDefault(k, k),
                v instanceof Map ? recursiveRename((Map<String, Object>) v, map) : v));
            return out;
        }
    }

    private static final ExecutorService POOL = Executors.newVirtualThreadPerTaskExecutor();

    private final String            apiKey;
    private final boolean           isTestnet;
    private final SignatureGenerator signer;
    private final HttpFactory        httpFactory;
    private final ResponseHandler    responseHandler;
    private final Map<ApiType, String> baseUrls;

    private volatile boolean                     batchMode       = false;
    private final Map<String, PendingRequest>    pendingRequests = new ConcurrentHashMap<>();

    public MexcBypass(String apiKey) { this(apiKey, false, null); }

    public MexcBypass(String apiKey, boolean isTestnet) { this(apiKey, isTestnet, null); }

    public MexcBypass(String apiKey, boolean isTestnet, String proxyUrl) {
        this.apiKey          = apiKey == null ? "" : apiKey;
        this.isTestnet       = isTestnet;
        this.signer          = new SignatureGenerator(this.apiKey);
        ProxyConfig proxy    = ProxyConfig.fromUrl(proxyUrl);
        this.httpFactory     = new HttpFactory(proxy);
        this.responseHandler = new ResponseHandler(isTestnet);
        this.baseUrls        = Map.of(
            ApiType.GENERAL,  "https://www.mexc.com",
            ApiType.FUTURES,  isTestnet
                ? "https://futures.testnet.mexc.com/api/v1"
                : "https://futures.mexc.com/api/v1",
            ApiType.CONTRACT, "https://contract.mexc.com/api/v1",
            ApiType.OTHER,    ""
        );
    }

    private Map<String, Object> request(HttpMethod method, ApiType type,
                                        String endpoint, Map<String, Object> params) {
        Map<String, Object> p = params.entrySet().stream()
            .filter(e -> e.getValue() != null && !e.getValue().toString().isEmpty())
            .collect(LinkedHashMap::new, (m, e) -> m.put(e.getKey(), e.getValue()), Map::putAll);

        SignatureResult sig    = signer.generate(p, method);
        String          base   = resolveBase(type, endpoint);
        String          origin = extractOrigin(base);
        List<String>    hdrs   = buildHeaders(type, sig, origin);
        String          url    = buildUrl(base, endpoint, method, p);
        Map<String, Object> body = method.hasBody() ? p : Map.of();

        if (batchMode) {
            PendingRequest pr = new PendingRequest(url, method, hdrs, body, endpoint);
            pendingRequests.put(pr.id(), pr);
            return Map.of("_request_id", pr.id());
        }

        try {
            var resp = httpFactory.execute(url, method, hdrs, body.isEmpty() ? null : body);
            return responseHandler.handle(resp.body(), resp.statusCode(), endpoint);
        } catch (Exception ex) {
            return responseHandler.error(0, "Request failed: " + ex.getMessage());
        }
    }

    private Map<String, Object> request(HttpMethod m, ApiType t, String ep) {
        return request(m, t, ep, Map.of());
    }

    /**
     * Execute multiple requests in parallel.
     *
     * <pre>{@code
     * var r = client.batch(Map.of(
     *     "ticker",   () -> client.getFuturesTickers(Map.of("symbol", "BTC_USDT")),
     *     "contract", () -> client.getFuturesContracts(Map.of("symbol", "BTC_USDT"))
     * ));
     * }</pre>
     */
    @SuppressWarnings("unchecked")
    public Map<String, Map<String, Object>> batch(
            Map<String, Supplier<Map<String, Object>>> requests) {
        batchMode = true; pendingRequests.clear();
        Map<String, String> keyToId = new LinkedHashMap<>();
        Map<String, Map<String, Object>> results = new LinkedHashMap<>();

        for (var e : requests.entrySet()) {
            var ret = e.getValue().get();
            if (ret.containsKey("_request_id")) keyToId.put(e.getKey(), (String) ret.get("_request_id"));
            else results.put(e.getKey(), ret);
        }
        results.putAll(executePending(keyToId));
        batchMode = false; pendingRequests.clear();
        return results;
    }

    /**
     * Execute the same set of requests for multiple accounts simultaneously.
     *
     * <pre>{@code
     * var results = MexcBypass.batchAccounts(
     *     Map.of(
     *         "acc1", Map.of("apiKey", "key1"),
     *         "acc2", Map.of("apiKey", "key2", "proxy", "socks5://user:pass@host:1080")
     *     ),
     *     c -> Map.of(
     *         "positions", () -> c.getFuturesOpenPositions(Map.of()),
     *         "balance",   () -> c.getFuturesAssets(Map.of("currency", "USDT"))
     *     )
     * );
     * }</pre>
     */
    public static Map<String, Map<String, Map<String, Object>>> batchAccounts(
            Map<String, Map<String, Object>> accounts,
            java.util.function.Function<MexcBypass, Map<String, Supplier<Map<String, Object>>>> cb) {

        record Meta(String acc, String req, String pid, MexcBypass client) {}

        Map<String, MexcBypass>                      clients    = new LinkedHashMap<>();
        Map<String, Map<String, String>>             pending    = new LinkedHashMap<>();
        Map<String, Map<String, Map<String, Object>>> results   = new LinkedHashMap<>();

        for (var ae : accounts.entrySet()) {
            var cfg    = ae.getValue();
            var client = new MexcBypass(
                cfg.getOrDefault("apiKey", cfg.getOrDefault("webkey", "")).toString(),
                Boolean.parseBoolean(cfg.getOrDefault("isTestnet", false).toString()),
                cfg.containsKey("proxy") ? cfg.get("proxy").toString() : null);
            client.batchMode = true;

            Map<String, String> kid = new LinkedHashMap<>();
            for (var re : cb.apply(client).entrySet()) {
                var ret = re.getValue().get();
                if (ret.containsKey("_request_id"))
                    kid.put(re.getKey(), ret.get("_request_id").toString());
            }
            clients.put(ae.getKey(), client);
            pending.put(ae.getKey(), kid);
            results.put(ae.getKey(), new LinkedHashMap<>());
        }

        List<Callable<Map.Entry<Meta, Map<String, Object>>>> tasks = new ArrayList<>();
        for (var ce : clients.entrySet()) {
            var accountKey = ce.getKey(); var client = ce.getValue();
            for (var re : pending.get(accountKey).entrySet()) {
                var pr = client.pendingRequests.get(re.getValue());
                var m  = new Meta(accountKey, re.getKey(), re.getValue(), client);
                tasks.add(() -> {
                    try {
                        var resp = client.httpFactory.execute(pr.url(), pr.method(), pr.headers(),
                            pr.body().isEmpty() ? null : pr.body());
                        return Map.entry(m, client.responseHandler.handle(resp.body(), resp.statusCode(), pr.endpoint()));
                    } catch (Exception ex) {
                        return Map.entry(m, client.responseHandler.error(0, "Request failed: " + ex.getMessage()));
                    }
                });
            }
        }

        try {
            for (var f : POOL.invokeAll(tasks)) {
                var e = f.get();
                results.get(e.getKey().acc()).put(e.getKey().req(), e.getValue());
            }
        } catch (Exception ex) { Thread.currentThread().interrupt(); }

        clients.forEach((k, c) -> { c.batchMode = false; c.pendingRequests.clear(); });
        return results;
    }

    private String resolveBase(ApiType type, String endpoint) {
        if (type == ApiType.OTHER) {
            int slash = endpoint.indexOf('/', 8);
            return slash != -1 ? endpoint.substring(0, slash) : endpoint;
        }
        return baseUrls.get(type);
    }

    private static String extractOrigin(String base) {
        int pos = base.indexOf('/', 8);
        return pos != -1 ? base.substring(0, pos) : base;
    }

    private List<String> buildHeaders(ApiType type, SignatureResult sig, String origin) {
        List<String> h = new ArrayList<>(List.of(
            "Content-Type: application/json",
            "Accept: */*",
            "User-Agent: Mozilla/5.0 (compatible; MEXC-API/1.0)",
            "Connection: keep-alive",
            "Cache-Control: no-cache",
            "Origin: " + origin));
        if (type == ApiType.GENERAL) {
            h.add("Cookie: u_id=" + apiKey + "; uc_token=" + apiKey + ";");
            h.add("Ucenter-Token: " + apiKey);
        } else {
            h.add("Authorization: " + apiKey);
            h.add("X-Mxc-Nonce: " + sig.timestamp());
            h.add("X-Mxc-Sign: " + sig.sign());
        }
        return h;
    }

    private static String buildUrl(String base, String ep, HttpMethod method, Map<String, Object> p) {
        String url = base + ep;
        if (!method.hasBody() && !p.isEmpty())
            url += "?" + p.entrySet().stream()
                .map(e -> SignatureGenerator.pctEncode(e.getKey()) + "=" +
                          SignatureGenerator.pctEncode(e.getValue().toString()))
                .collect(Collectors.joining("&"));
        return url;
    }

    private Map<String, Map<String, Object>> executePending(Map<String, String> keyToId) {
        if (keyToId.isEmpty()) return Map.of();
        Map<String, Map<String, Object>> results = new LinkedHashMap<>();
        List<Callable<Map.Entry<String, Map<String, Object>>>> tasks = new ArrayList<>();

        for (var e : keyToId.entrySet()) {
            String key = e.getKey(); PendingRequest p = pendingRequests.get(e.getValue());
            tasks.add(() -> {
                try {
                    var resp = httpFactory.execute(p.url(), p.method(), p.headers(),
                        p.body().isEmpty() ? null : p.body());
                    return Map.entry(key, responseHandler.handle(resp.body(), resp.statusCode(), p.endpoint()));
                } catch (Exception ex) {
                    return Map.entry(key, responseHandler.error(0, "Request failed: " + ex.getMessage()));
                }
            });
        }
        try { for (var f : POOL.invokeAll(tasks)) { var e = f.get(); results.put(e.getKey(), e.getValue()); } }
        catch (Exception ex) { Thread.currentThread().interrupt(); }
        return results;
    }

    private long[] weekRange() {
        LocalDate mon = LocalDate.now(ZoneOffset.UTC).with(TemporalAdjusters.previousOrSame(DayOfWeek.MONDAY));
        return new long[]{
            mon.atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli(),
            mon.plusDays(6).atTime(23, 59, 59, 99_000_000).toInstant(ZoneOffset.UTC).toEpochMilli()
        };
    }

    private double normalizeVolume(double raw, Map<String, Object> cd) {
        int    scale = ival(cd, "volScale", 0);
        double unit  = dval(cd, "volUnit",  0.0);
        double v     = Math.round(raw * Math.pow(10, scale)) / Math.pow(10, scale);
        if (unit > 0) v = Math.floor(v / unit) * unit;
        return Math.max(dval(cd, "minVol", 0.0), Math.min(v, dval(cd, "maxVol", Double.MAX_VALUE)));
    }

    private Map<String, Object> volumePayload(Map<String, Object> cd, double vol,
                                               int lev, double price, double usdt, PriceType pt) {
        return mapOf("usdt_value", usdt, "volume", vol, "leverage", lev, "price", price,
            "min_volume", cd.get("minVol"), "max_volume", cd.get("maxVol"),
            "min_leverage", cd.get("minLeverage"), "max_leverage", cd.get("maxLeverage"),
            "price_type", pt.value(), "volume_scale", cd.get("volScale"),
            "volume_unit", cd.get("volUnit"), "price_scale", cd.get("priceScale"),
            "price_unit", cd.get("priceUnit"));
    }

    private Map<String, Object> error(int code, String msg) {
        return responseHandler.error(code, msg);
    }

    private Map<String, Object> fetchTickerAndContract(String symbol) {
        var r = batch(Map.of(
            "ticker",   () -> getFuturesTickers(Map.of("symbol", symbol)),
            "contract", () -> getFuturesContracts(Map.of("symbol", symbol))));
        if (!Boolean.TRUE.equals(r.get("ticker").get("success"))   || r.get("ticker").get("data")   == null)
            return Map.of("ok", false, "error", error(400, "Failed to get ticker data"));
        if (!Boolean.TRUE.equals(r.get("contract").get("success")) || r.get("contract").get("data") == null)
            return Map.of("ok", false, "error", error(400, "Failed to get contract data"));
        return Map.of("ok", true, "result", r);
    }

    @SuppressWarnings("unchecked")
    private Map<String, Object> resolveMarket(Map<String, Object> market, Map<String, Object> params) {
        PriceType pt = PriceType.fromValue((String) params.get("price_type"));
        var result   = (Map<String, Map<String, Object>>) market.get("result");
        var tData    = (Map<String, Object>) result.get("ticker").get("data");
        var cList    = (List<Map<String, Object>>) result.get("contract").get("data");
        return mapOf("ticker", tData != null ? tData.get(pt.tickerField()) : null,
                     "contract", cList != null && !cList.isEmpty() ? cList.get(0) : null,
                     "pt", pt);
    }

    private String[] parseSymbol(Map<String, Object> p) {
        Object sym = p.get("symbol");
        if (sym != null && !sym.toString().isEmpty()) {
            String[] parts = sym.toString().split("_", 2);
            return new String[]{parts[0], parts.length > 1 ? parts[1] : ""};
        }
        return new String[]{p.getOrDefault("base_coin","").toString(),
                            p.getOrDefault("quote_coin","").toString()};
    }

    private static int    ival(Map<String, Object> m, String k, int def)    { Object v = m.get(k); return v != null ? Integer.parseInt(v.toString())  : def; }
    private static double dval(Map<String, Object> m, String k, double def) { Object v = m.get(k); return v != null ? Double.parseDouble(v.toString()) : def; }
    private static long   lval(Object v) { if (v==null) return 0L; try { return Long.parseLong(v.toString()); } catch (Exception e) { return 0L; } }
    private static double round(double v, int s) { double f = Math.pow(10,s); return Math.round(v*f)/f; }

    private static Map<String, Object> mapOf(Object... kv) {
        Map<String, Object> m = new LinkedHashMap<>();
        for (int i = 0; i < kv.length - 1; i += 2)
            if (kv[i+1] != null) m.put(kv[i].toString(), kv[i+1]);
        return m;
    }

    public Map<String, Object> getServerTime()    { return request(HttpMethod.GET,  ApiType.CONTRACT, "/contract/ping"); }
    public Map<String, Object> ping()             { return request(HttpMethod.GET,  ApiType.GENERAL,  "/api/common/ping"); }
    public Map<String, Object> validation()       { return request(HttpMethod.POST, ApiType.GENERAL,  "/ucenter/api/login/validation"); }
    public Map<String, Object> getWSToken()       { return request(HttpMethod.GET,  ApiType.GENERAL,  "/ucenter/api/ws_token"); }
    public Map<String, Object> getCustomerInfo()  { return request(HttpMethod.GET,  ApiType.GENERAL,  "/ucenter/api/customer_info"); }
    public Map<String, Object> getUserInfo()      { return request(HttpMethod.GET,  ApiType.GENERAL,  "/ucenter/api/user_info"); }
    public Map<String, Object> getLatestsDeposits(){ return request(HttpMethod.GET, ApiType.GENERAL,  "/api/platform/deposit/v3/latest"); }
    public Map<String, Object> getSecureInfo()    { return request(HttpMethod.GET,  ApiType.GENERAL,  "/ucenter/api/secure/check"); }
    public Map<String, Object> getOriginInfo()    { return request(HttpMethod.GET,  ApiType.GENERAL,  "/ucenter/api/origin_info"); }
    public Map<String, Object> logout()           { return request(HttpMethod.POST, ApiType.GENERAL,  "/ucenter/api/logout"); }

    public Map<String, Object> getDepositAddresses(Map<String, Object> p) {
        return p.containsKey("currency")
            ? request(HttpMethod.GET, ApiType.GENERAL, "/api/platform/asset/api/asset/spot/currency/v3?currency=" + p.get("currency"))
            : request(HttpMethod.GET, ApiType.GENERAL, "/api/platform/asset/api/deposit/address/list?vcoinId=" + p.get("vcoin_id"));
    }

    @SuppressWarnings("unchecked")
    public Map<String, Object> getMarketSymbols(Map<String, Object> params) {
        var response = request(HttpMethod.GET, ApiType.GENERAL, "/api/platform/spot/market-v2/web/symbolsV2");
        if (params.isEmpty()) return response;

        String[] parts    = parseSymbol(params);
        String   base     = parts[0];
        String   quote    = parts[1].isEmpty() ? "USDT" : parts[1];
        if (base.isEmpty()) return response;

        var data = (Map<String, Object>) response.get("data");
        if (data == null) return error(400, "No data in response");

        var symbols = (Map<String, List<Map<String, Object>>>) data.get("symbols");
        if (symbols == null || !symbols.containsKey(quote)) return error(400, "No " + quote + " symbols found");

        String search = base.toUpperCase();
        var matches = symbols.get(quote).stream()
            .filter(i -> search.equals(i.getOrDefault("currency", "").toString().toUpperCase()))
            .collect(Collectors.toList());
        if (matches.isEmpty()) return error(404, "No matching tokens found");

        return mapOf("success", true, "count", matches.size(), "data", matches,
                     "timestamp", System.currentTimeMillis() / 1000);
    }

    @SuppressWarnings("unchecked")
    public Map<String, Object> getSpotOrderBook(Map<String, Object> p) {
        var sym = getMarketSymbols(Map.of("symbol", p.get("symbol")));
        if (!Boolean.TRUE.equals(sym.get("success"))) return sym;
        var data = (List<Map<String, Object>>) sym.get("data");
        return request(HttpMethod.GET, ApiType.GENERAL, "/api/platform/spot/market-v2/web/depth/v2",
            mapOf("symbolId", data.get(0).get("id"),
                  "decimal",  p.getOrDefault("decimal", "0.0000001"),
                  "count",    p.getOrDefault("count", 100)));
    }

    @SuppressWarnings("unchecked")
    public Map<String, Object> createSpotOrder(Map<String, Object> p) {
        String type = p.get("type").toString();
        String path = "LIMIT_ORDER".equals(type) ? "/api/platform/spot/order/place" : "/api/platform/spot/v4/order/place";
        var sym = getMarketSymbols(Map.of("symbol", p.get("symbol")));
        if (!Boolean.TRUE.equals(sym.get("success"))) return sym;
        var s = ((List<Map<String, Object>>) sym.get("data")).get(0);
        return request(HttpMethod.POST, ApiType.GENERAL, path,
            mapOf("orderType", type, "tradeType", p.get("side"),
                  "price", p.get("price"), "amount", p.get("amount"), "quantity", p.get("quantity"),
                  "marketCurrencyId", s.get("marketCurrencyId"), "currencyId", s.get("coinId"),
                  "orderSource", "WEB", "ts", System.currentTimeMillis()));
    }

    public Map<String, Object> cancelSpotOrder(Map<String, Object> p) {
        return request(HttpMethod.DELETE, ApiType.GENERAL, "/api/platform/spot/order/cancel/v2",
            Map.of("orderId", p.get("order_id")));
    }

    public Map<String, Object> getReferralsList(Map<String, Object> p) {
        long[] w = weekRange();
        return request(HttpMethod.GET, ApiType.GENERAL, "/api/assetbussiness/invite/invites",
            mapOf("startTime", p.getOrDefault("start_time", w[0]), "endTime", p.getOrDefault("end_time", w[1]),
                  "page", p.getOrDefault("page_num", 1), "pageSize", p.getOrDefault("page_size", 10)));
    }

    public Map<String, Object> getAssetsOverview(Map<String, Object> p) {
        boolean convert = Boolean.parseBoolean(p.getOrDefault("convert", false).toString());
        return request(HttpMethod.GET, ApiType.GENERAL,
            convert ? "/api/platform/asset/api/asset/overview/convert/v2"
                    : "/api/platform/asset/api/asset/overview/v2");
    }

    public Map<String, Object> getFuturesFeeRate()       { return request(HttpMethod.GET, ApiType.FUTURES, "/private/account/contract/fee_rate"); }
    public Map<String, Object> getFuturesZeroFeeRate()   { return request(HttpMethod.GET, ApiType.FUTURES, "/private/account/contract/zero_fee_rate"); }
    public Map<String, Object> getFuturesTodayPnL()      { return request(HttpMethod.GET, ApiType.FUTURES, "/private/account/asset/analysis/today_pnl"); }

    public Map<String, Object> getFuturesAnalysis(Map<String, Object> p) {
        long now = System.currentTimeMillis();
        long end = Math.min(lval(p.getOrDefault("end_time", now)), now);
        long start = lval(p.getOrDefault("start_time", end - 7*86_400_000L));
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/account/asset/analysis/v3",
            mapOf("currency", p.getOrDefault("currency", "USDT"), "symbol", p.get("symbol"),
                  "include_unrealised_pnl", p.getOrDefault("include_unrealised_pnl", 0),
                  "reverse", p.getOrDefault("reverse", 0),
                  "startTime", start < end ? start : end - 1000, "endTime", end));
    }

    public Map<String, Object> getFuturesAssets(Map<String, Object> p) {
        return request(HttpMethod.GET, ApiType.FUTURES,
            p.containsKey("currency") ? "/private/account/asset/" + p.get("currency") : "/private/account/assets");
    }

    public Map<String, Object> getFuturesAssetTransferRecords(Map<String, Object> p) {
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/account/transfer_record",
            mapOf("currency", p.get("currency"), "state", p.get("state"), "type", p.get("type"),
                  "page_num", p.getOrDefault("page_num", 1), "page_size", p.getOrDefault("page_size", 20)));
    }

    public Map<String, Object> getFuturesRiskLimits(Map<String, Object> p) {
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/account/risk_limit", mapOf("symbol", p.get("symbol")));
    }

    public Map<String, Object> getFuturesFundingRate(Map<String, Object> p)     { return request(HttpMethod.GET, ApiType.CONTRACT, "/contract/funding_rate/" + p.get("symbol")); }
    public Map<String, Object> getFuturesContractIndexPrice(Map<String, Object> p){ return request(HttpMethod.GET, ApiType.CONTRACT, "/contract/index_price/" + p.get("symbol")); }
    public Map<String, Object> getFuturesContractFairPrice(Map<String, Object> p) { return request(HttpMethod.GET, ApiType.CONTRACT, "/contract/fair_price/"  + p.get("symbol")); }

    public Map<String, Object> getFuturesContracts(Map<String, Object> p) {
        return request(HttpMethod.GET, ApiType.FUTURES, "/contract/detailV2", mapOf("symbol", p.get("symbol")));
    }

    public Map<String, Object> getFuturesTickers(Map<String, Object> p) {
        return request(HttpMethod.GET, ApiType.FUTURES, "/contract/ticker", mapOf("symbol", p.get("symbol")));
    }

    public Map<String, Object> getFuturesContractKlineData(Map<String, Object> p) {
        PriceType pt = PriceType.fromValue((String) p.get("price_type"));
        String seg   = pt.klinePathSegment();
        String path  = seg.isEmpty() ? "/contract/kline/" + p.get("symbol")
                                     : "/contract/kline/" + seg + "/" + p.get("symbol");
        return request(HttpMethod.GET, ApiType.CONTRACT, path,
            mapOf("interval", p.getOrDefault("interval", "Min1"), "start", p.get("start_time"), "end", p.get("end_time")));
    }

    public Map<String, Object> getFuturesOpenPositions(Map<String, Object> p) {
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/position/open_positions",
            mapOf("symbol", p.get("symbol"), "positionId", p.get("position_id")));
    }

    public Map<String, Object> getFuturesPositionsHistory(Map<String, Object> p) {
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/position/list/history_positions",
            mapOf("symbol", p.get("symbol"), "type", p.get("type"),
                  "page_num", p.getOrDefault("page_num", 1), "page_size", p.getOrDefault("page_size", 20)));
    }

    public Map<String, Object> closeAllFuturesPositions() { return request(HttpMethod.POST, ApiType.FUTURES, "/private/position/close_all"); }

    public Map<String, Object> reverseFuturesPosition(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/position/reverse",
            mapOf("positionId", p.get("position_id"), "symbol", p.get("symbol"), "vol", p.get("vol")));
    }

    public Map<String, Object> getFuturesPositionMode() { return request(HttpMethod.GET, ApiType.FUTURES, "/private/position/position_mode"); }

    public Map<String, Object> changeFuturesPositionMode(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/position/change_position_mode",
            Map.of("positionMode", p.get("position_mode")));
    }

    public Map<String, Object> getFuturesLeverage(Map<String, Object> p) {
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/position/leverage", Map.of("symbol", p.get("symbol")));
    }

    public Map<String, Object> changeFuturesPositionMargin(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/position/change_margin",
            mapOf("positionId", p.get("position_id"), "amount", p.get("amount"), "type", p.get("type")));
    }

    public Map<String, Object> changeFuturesPositionLeverage(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/position/change_leverage",
            mapOf("positionId", p.get("position_id"), "leverage", p.get("leverage"),
                  "openType", p.get("open_type"), "symbol", p.get("symbol"), "positionType", p.get("position_type")));
    }

    public Map<String, Object> getFuturesOrdersDeals(Map<String, Object> p) {
        long[] w = weekRange();
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/order/list/order_deals",
            mapOf("symbol", p.get("symbol"),
                  "start_time", p.getOrDefault("start_time", w[0]), "end_time", p.getOrDefault("end_time", w[1]),
                  "page_num", p.getOrDefault("page_num", 1), "page_size", p.getOrDefault("page_size", 20)));
    }

    public Map<String, Object> getFuturesPendingOrders(Map<String, Object> p) {
        String ep = p.containsKey("symbol")
            ? "/private/order/list/open_orders/" + p.get("symbol") : "/private/order/list/open_orders/";
        return request(HttpMethod.GET, ApiType.FUTURES, ep,
            mapOf("page_num", p.getOrDefault("page_num", 1), "page_size", p.getOrDefault("page_size", 20)));
    }

    public Map<String, Object> getFuturesOrdersHistory(Map<String, Object> p) {
        long[] w = weekRange();
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/order/list/history_orders",
            mapOf("symbol", p.get("symbol"), "states", p.get("states"),
                  "category", p.get("category"), "side", p.get("side"),
                  "start_time", p.getOrDefault("start_time", w[0]), "end_time", p.getOrDefault("end_time", w[1]),
                  "page_num", p.getOrDefault("page_num", 1), "page_size", p.getOrDefault("page_size", 20)));
    }

    public Map<String, Object> getFuturesOpenLimitOrders(Map<String, Object> p) {
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/order/list/open_orders",
            Map.of("page_size", p.getOrDefault("page_size", 200)));
    }

    public Map<String, Object> getFuturesOpenStopOrders(Map<String, Object> p) {
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/stoporder/open_orders",
            Map.of("page_size", p.getOrDefault("page_size", 200)));
    }

    @SuppressWarnings("unchecked")
    public Map<String, Object> getFuturesOpenOrders(Map<String, Object> p) {
        int sz = (int) p.getOrDefault("page_size", 200);
        var r  = batch(Map.of(
            "limit_orders", () -> getFuturesOpenLimitOrders(Map.of("page_size", sz)),
            "stop_orders",  () -> getFuturesOpenStopOrders( Map.of("page_size", sz))));
        List<Object> merged = new ArrayList<>();
        if (Boolean.TRUE.equals(r.get("limit_orders").get("success"))) { var d = (List<?>) r.get("limit_orders").get("data"); if (d != null) merged.addAll(d); }
        if (Boolean.TRUE.equals(r.get("stop_orders").get("success")))  { var d = (List<?>) r.get("stop_orders").get("data");  if (d != null) merged.addAll(d); }
        return Map.of("success", true, "code", 0, "data", merged);
    }

    public Map<String, Object> getFuturesClosedOrders(Map<String, Object> p) {
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/order/close_orders",
            mapOf("symbol", p.get("symbol"), "category", p.get("category"),
                  "page_num", p.getOrDefault("page_num", 1), "page_size", p.getOrDefault("page_size", 20)));
    }

    public Map<String, Object> getFuturesOrdersById(Map<String, Object> p) {
        String[] ids = p.get("ids").toString().split(",");
        return ids.length > 1
            ? request(HttpMethod.GET, ApiType.FUTURES, "/private/order/batch_query", Map.of("order_ids", p.get("ids")))
            : request(HttpMethod.GET, ApiType.FUTURES, "/private/order/get/" + p.get("ids").toString().trim());
    }

    public Map<String, Object> createFuturesOrder(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/order/create",
            mapOf("symbol", p.get("symbol"), "price", p.get("price"), "type", p.get("type"),
                  "openType", p.get("open_type"), "positionMode", p.get("position_mode"),
                  "side", p.get("side"), "vol", p.get("vol"), "leverage", p.get("leverage"),
                  "positionId", p.get("position_id"), "externalOid", p.get("external_id"),
                  "takeProfitPrice", p.get("take_profit_price"), "profitTrend", p.getOrDefault("take_profit_trend", 1),
                  "stopLossPrice", p.get("stop_loss_price"), "lossTrend", p.getOrDefault("stop_loss_trend", 1),
                  "priceProtect", p.getOrDefault("price_protect", 0),
                  "reduceOnly", p.getOrDefault("reduce_only", false),
                  "marketCeiling", p.getOrDefault("market_ceiling", false),
                  "flashClose", p.get("flash_close"), "bboTypeNum", p.get("bbo_type")));
    }

    public Map<String, Object> cancelFuturesOrders(Map<String, Object> p) {
        Object raw = p.get("ids");
        List<String> ids = (raw instanceof List)
            ? ((List<?>) raw).stream().map(Object::toString).collect(Collectors.toList())
            : Arrays.stream(raw.toString().split(",")).map(String::trim).collect(Collectors.toList());
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/order/cancel", Map.of("ids", ids));
    }

    public Map<String, Object> cancelFuturesOrderWithExternalId(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/order/cancel_with_external",
            mapOf("symbol", p.get("symbol"), "externalOid", p.get("external_id")));
    }

    public Map<String, Object> cancelAllFuturesOrders(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/order/cancel_all", mapOf("symbol", p.get("symbol")));
    }

    public Map<String, Object> changeFuturesLimitOrderPrice(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/order/change_limit_order",
            mapOf("orderId", p.get("order_id"), "price", p.get("price"), "vol", p.get("vol")));
    }

    public Map<String, Object> chaseFuturesOrder(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/order/chase_limit_order",
            Map.of("orderId", p.get("order_id")));
    }

    public Map<String, Object> createFuturesChaseOrder(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/order/chase_limit/place",
            mapOf("chaseType", p.get("chase_type"), "distanceType", p.get("distance_type"),
                  "distanceValue", p.get("distance_value"), "maxDistanceType", p.get("max_distance_type"),
                  "maxDistanceValue", p.get("max_distance_value"), "leverage", p.get("leverage"),
                  "openType", p.get("open_type"), "side", p.get("side"),
                  "symbol", p.get("symbol"), "vol", p.get("vol")));
    }

    public Map<String, Object> createFuturesStopOrder(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/place/v2",
            mapOf("positionId", p.get("position_id"), "volType", p.getOrDefault("vol_type", 2),
                  "vol", p.get("vol"), "takeProfitType", p.get("take_profit_type"),
                  "takeProfitOrderPrice", p.get("take_profit_order_price"),
                  "takeProfitPrice", p.get("take_profit_price"),
                  "takeProfitReverse", p.getOrDefault("take_profit_reverse", 2),
                  "profitTrend", p.getOrDefault("take_profit_trend", 1),
                  "takeProfitVol", p.get("take_profit_volume"),
                  "stopLossType", p.get("stop_loss_type"),
                  "stopLossOrderPrice", p.get("stop_loss_order_price"),
                  "stopLossPrice", p.get("stop_loss_price"),
                  "stopLossReverse", p.getOrDefault("stop_loss_reverse", 2),
                  "lossTrend", p.getOrDefault("stop_loss_trend", 1),
                  "profitLossVolType", p.getOrDefault("profit_loss_vol_type", "SAME"),
                  "priceProtect", p.getOrDefault("price_protect", 0)));
    }

    public Map<String, Object> getFuturesStopLimitOrders(Map<String, Object> p) {
        long[] w = weekRange();
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/stoporder/list/orders",
            mapOf("symbol", p.get("symbol"), "is_finished", p.get("is_finished"),
                  "start_time", p.getOrDefault("start_time", w[0]), "end_time", p.getOrDefault("end_time", w[1]),
                  "page_num", p.getOrDefault("page_num", 1), "page_size", p.getOrDefault("page_size", 20)));
    }

    public Map<String, Object> cancelStopLimitOrders(Map<String, Object> p) {
        Object raw = p.get("ids");
        List<String> ids = (raw instanceof List)
            ? ((List<?>) raw).stream().map(Object::toString).collect(Collectors.toList())
            : Arrays.stream(raw.toString().split(",")).map(String::trim).filter(s -> !s.isEmpty()).collect(Collectors.toList());
        List<Map<String, Object>> payload = ids.stream()
            .map(id -> Map.<String, Object>of("stopPlanOrderId", Long.parseLong(id))).collect(Collectors.toList());
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/cancel", Map.of("payload", payload));
    }

    public Map<String, Object> cancelAllFuturesStopLimitOrders(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/cancel_all",
            mapOf("positionId", p.get("position_id"), "symbol", p.get("symbol")));
    }

    public Map<String, Object> changeFuturesOrderStopLimitPrice(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/change_price",
            mapOf("orderId", p.get("order_id"), "takeProfitPrice", p.get("take_profit_price"), "stopLossPrice", p.get("stop_loss_price")));
    }

    public Map<String, Object> changeFuturesPlanOrderStopLimitPrice(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/change_plan_price",
            mapOf("stopPlanOrderId", p.get("order_id"), "takeProfitPrice", p.get("take_profit_price"), "stopLossPrice", p.get("stop_loss_price")));
    }

    public Map<String, Object> changeFuturesOrderTargets(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/change_plan_order",
            mapOf("orderId", p.get("real_order_id"), "profitTrend", p.get("take_profit_trend"),
                  "takeProfitPrice", p.get("take_profit_price"), "takeProfitVolume", p.get("take_profit_volume"),
                  "lossTrend", p.get("stop_loss_trend"), "stopLossPrice", p.get("stop_loss_price"),
                  "stopLossVolume", p.get("stop_loss_volume")));
    }

    public Map<String, Object> createFuturesTriggerOrder(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/planorder/place",
            mapOf("symbol", p.get("symbol"), "price", p.get("price"), "vol", p.get("vol"),
                  "leverage", p.get("leverage"), "side", p.get("side"), "openType", p.get("open_type"),
                  "triggerPrice", p.get("trigger_price"), "triggerType", p.get("trigger_type"),
                  "executeCycle", p.get("execute_cycle"), "orderType", p.get("order_type"),
                  "trend", p.get("trend")));
    }

    public Map<String, Object> getFuturesTriggerOrders(Map<String, Object> p) {
        long[] w = weekRange();
        return request(HttpMethod.GET, ApiType.FUTURES, "/private/planorder/list/orders",
            mapOf("symbol", p.get("symbol"), "states", p.get("states"),
                  "start_time", p.getOrDefault("start_time", w[0]), "end_time", p.getOrDefault("end_time", w[1]),
                  "page_num", p.getOrDefault("page_num", 1), "page_size", p.getOrDefault("page_size", 20)));
    }

    public Map<String, Object> cancelFuturesTriggerOrders(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/planorder/cancel", Map.of("ids", p.get("ids")));
    }

    public Map<String, Object> cancelAllFuturesTriggerOrders(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/planorder/cancel_all", mapOf("symbol", p.get("symbol")));
    }

    public Map<String, Object> createFuturesTrailingOrder(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/trackorder/place",
            mapOf("symbol", p.get("symbol"), "leverage", p.get("leverage"), "side", p.get("side"),
                  "vol", p.get("vol"), "openType", p.get("open_type"), "trend", p.get("trend"),
                  "activePrice", p.get("active_price"), "backType", p.get("back_type"),
                  "backValue", p.get("back_value"), "positionMode", p.getOrDefault("position_mode", 0),
                  "reduceOnly", p.get("reduce_only")));
    }

    public Map<String, Object> cancelFuturesTrailingOrder(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/trackorder/cancel",
            mapOf("symbol", p.get("symbol"), "trackOrderId", p.get("order_id")));
    }

    public Map<String, Object> changeFuturesTrailingOrder(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.FUTURES, "/private/trackorder/change_order",
            mapOf("symbol", p.get("symbol"), "trackOrderId", p.get("order_id"),
                  "trend", p.get("trend"), "activePrice", p.get("active_price"),
                  "backType", p.get("back_type"), "backValue", p.get("back_value"), "vol", p.get("vol")));
    }

    @SuppressWarnings("unchecked")
    public Map<String, Object> calculateFuturesPositionPnL(Map<String, Object> p) {
        var r    = batch(Map.of("contract", () -> getFuturesContracts(Map.of("symbol", p.get("symbol"))),
                                "ticker",   () -> getFuturesTickers(  Map.of("symbol", p.get("symbol")))));
        var cList = (List<Map<String, Object>>) r.get("contract").get("data");
        var tData = (Map<String, Object>) r.get("ticker").get("data");
        if (cList == null || cList.isEmpty() || tData == null)
            return error(-1, "Unable to calculate PnL: missing market data");

        var     cd  = cList.get(0); double cs = dval(cd, "contractSize", 0.0);
        if (cs == 0.0) return error(-1, "Unable to calculate PnL: missing market data");

        PriceType pt = PriceType.fromValue((String) p.get("price_type"));
        double price = Double.parseDouble(tData.get(pt.tickerField()).toString());
        double entry = Double.parseDouble(p.get("entry_price").toString());
        double vol   = Double.parseDouble(p.get("volume").toString());
        int    lev   = Math.max(1, (int) lval(p.getOrDefault("leverage", 1)));
        boolean long_ = lval(p.getOrDefault("side", 1)) == 1;

        double pnl  = long_ ? (price - entry) * vol * cs : (entry - price) * vol * cs;
        double im   = entry * vol * cs / lev;
        return Map.of("success", true, "code", 0, "data", mapOf(
            "pnl", round(pnl, 4), "pnl_percent", im > 0 ? round(pnl / im * 100, 2) : 0.0,
            "volume", vol, "entry_price", entry, "current_price", price, "price_type", pt.value()));
    }

    @SuppressWarnings("unchecked")
    public Map<String, Object> calculateFuturesVolume(Map<String, Object> p) {
        for (String r : List.of("symbol", "amount", "leverage"))
            if (!p.containsKey(r) || p.get(r) == null) return error(404, "Missing required parameter: " + r);

        var market = fetchTickerAndContract(p.get("symbol").toString());
        if (!Boolean.TRUE.equals(market.get("ok"))) return (Map<String, Object>) market.get("error");

        var rm = resolveMarket(market, p);
        var cd = (Map<String, Object>) rm.get("contract");
        Object ticker = rm.get("ticker"); PriceType pt = (PriceType) rm.get("pt");
        if (ticker == null) return error(400, "Invalid price value");

        double amount = Double.parseDouble(p.get("amount").toString());
        int    lev    = Integer.parseInt(p.get("leverage").toString());
        double cs     = dval(cd, "contractSize", 0.0);
        if (amount <= 0) return error(400, "Amount must be positive");
        if (lev <= 0)    return error(400, "Leverage must be positive");
        if (cs <= 0)     return error(400, "Invalid contract size");

        double price  = Double.parseDouble(ticker.toString());
        int    clev   = (int) Math.max(dval(cd, "minLeverage", 1), Math.min(lev, dval(cd, "maxLeverage", 200)));
        double vol    = normalizeVolume((amount * clev) / (price * cs), cd);
        double usdt   = round(vol * price * cs / clev, ival(cd, "priceScale", 4));
        return Map.of("success", true, "code", 0, "data", volumePayload(cd, vol, clev, price, usdt, pt));
    }

    @SuppressWarnings("unchecked")
    public Map<String, Object> calculateFuturesVolumeFromBaseAmount(Map<String, Object> p) {
        for (String r : List.of("symbol", "amount", "leverage"))
            if (!p.containsKey(r) || p.get(r) == null) return error(404, "Missing required parameter: " + r);

        var market = fetchTickerAndContract(p.get("symbol").toString());
        if (!Boolean.TRUE.equals(market.get("ok"))) return (Map<String, Object>) market.get("error");

        var rm = resolveMarket(market, p);
        var cd = (Map<String, Object>) rm.get("contract");
        Object ticker = rm.get("ticker"); PriceType pt = (PriceType) rm.get("pt");
        if (ticker == null) return error(400, "Invalid price value");

        double amount = Double.parseDouble(p.get("amount").toString());
        int    lev    = Integer.parseInt(p.get("leverage").toString());
        double cs     = dval(cd, "contractSize", 0.0);
        if (amount <= 0) return error(400, "Base amount must be positive");
        if (lev <= 0)    return error(400, "Leverage must be positive");
        if (cs <= 0)     return error(400, "Invalid contract size");

        double price    = Double.parseDouble(ticker.toString());
        int    clev     = (int) Math.max(dval(cd, "minLeverage", 1), Math.min(lev, dval(cd, "maxLeverage", 200)));
        double vol      = normalizeVolume(amount / cs, cd);
        double notional = amount * price;
        int    ps       = ival(cd, "priceScale", 4);

        Map<String, Object> data = new LinkedHashMap<>(volumePayload(cd, vol, clev, price, round(notional, ps), pt));
        data.put("required_margin", round(notional / clev, ps));
        data.put("amount",          amount);
        data.put("notional_value",  notional);
        data.put("contract_size",   cs);
        return Map.of("success", true, "code", 0, "data", data);
    }

    public Map<String, Object> receiveFuturesTestnetAsset(Map<String, Object> p) {
        return request(HttpMethod.POST, ApiType.OTHER,
            "https://futures.testnet.mexc.com/mock/contract/asset/receive",
            mapOf("currency", p.getOrDefault("currency", "USDT"), "amount", p.get("amount")));
    }
}