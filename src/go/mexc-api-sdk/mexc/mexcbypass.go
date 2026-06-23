package mexcbypass

import (
"compress/gzip"
"context"
"crypto/md5"
"crypto/tls"
"encoding/json"
"fmt"
"github.com/google/uuid"
"io"
"math"
"net"
"net/http"
"net/url"
"sort"
"strconv"
"strings"
"sync"
"sync/atomic"
"time"
)

type ApiType int

const (
	ApiTypeGeneral  ApiType = iota
	ApiTypeFutures
	ApiTypeContract
	ApiTypeOther
)

type HttpMethod int

const (
	MethodGET    HttpMethod = iota
	MethodPOST
	MethodPUT
	MethodDELETE
)

func (m HttpMethod) String() string {
	switch m {
	case MethodGET:
		return "GET"
	case MethodPOST:
		return "POST"
	case MethodPUT:
		return "PUT"
	case MethodDELETE:
		return "DELETE"
	}
	return "GET"
}

func (m HttpMethod) HasBody() bool {
	return m == MethodPOST || m == MethodPUT
}

type PriceType string

const (
	PriceTypeLastPrice  PriceType = "last_price"
	PriceTypeFairPrice  PriceType = "fair_price"
	PriceTypeIndexPrice PriceType = "index_price"
)

func (pt PriceType) TickerField() string {
	switch pt {
	case PriceTypeFairPrice:
		return "fairPrice"
	case PriceTypeIndexPrice:
		return "indexPrice"
	default:
		return "lastPrice"
	}
}

func (pt PriceType) KlinePathSegment() string {
	switch pt {
	case PriceTypeIndexPrice:
		return "index_price"
	case PriceTypeFairPrice:
		return "fair_price"
	default:
		return ""
	}
}

func ParsePriceType(s string) PriceType {
	switch PriceType(s) {
	case PriceTypeFairPrice, PriceTypeIndexPrice:
		return PriceType(s)
	default:
		return PriceTypeLastPrice
	}
}

type ProxyConfig struct {
	URL string
}

func (p *ProxyConfig) Validate() error {
	if p == nil || p.URL == "" {
		return nil
	}
	u, err := url.Parse(p.URL)
	if err != nil {
		return fmt.Errorf("invalid proxy URL: %w", err)
	}
	switch u.Scheme {
	case "http", "https", "socks5", "socks5h":
		return nil
	default:
		return fmt.Errorf("unsupported proxy scheme: %s", u.Scheme)
	}
}

type Params map[string]any

type Response map[string]any

func (r Response) Success() bool {
	v, ok := r["success"]
	if !ok {
		return false
	}
	b, ok := v.(bool)
	return ok && b
}

func (r Response) Code() int {
	v, ok := r["code"]
	if !ok {
		return -1
	}
	switch c := v.(type) {
	case int:
		return c
	case float64:
		return int(c)
	}
	return -1
}

func (r Response) Message() string {
	v, ok := r["message"]
	if !ok {
		return ""
	}
	s, _ := v.(string)
	return s
}

type pendingRequest struct {
	id       string
	url      string
	method   HttpMethod
	headers  map[string]string
	body     Params
	endpoint string
}

type signatureResult struct {
	Timestamp string
	Sign      string
}

type signatureGenerator struct {
	apiKey  string
	lastMs  atomic.Int64
	counter atomic.Int64
	mu      sync.Mutex
}

func newSignatureGenerator(apiKey string) *signatureGenerator {
	return &signatureGenerator{apiKey: apiKey}
}

func (sg *signatureGenerator) Generate(payload Params, method HttpMethod, forceTime string) signatureResult {
	ts := forceTime
	if ts == "" {
		ts = sg.uniqueMs()
	}

	g := md5hash(sg.apiKey + ts)
	g = g[7:]

	var sign string
	if method.HasBody() {
		var body string
		if len(payload) > 0 {
			b, _ := json.Marshal(payload)
			body = string(b)
		}
		sign = md5hash(ts + body + g)
	} else {
		sign = md5hash(ts + encodeQueryRFC3986(payload) + g)
	}

	return signatureResult{Timestamp: ts, Sign: sign}
}

func (sg *signatureGenerator) uniqueMs() string {
	sg.mu.Lock()
	defer sg.mu.Unlock()

	now := time.Now().UnixMilli()

	if now == sg.lastMs.Load() {
		sg.counter.Add(1)
	} else {
		sg.lastMs.Store(now)
		sg.counter.Store(0)
	}

	return strconv.FormatInt(sg.lastMs.Load()+sg.counter.Load(), 10)
}

func md5hash(s string) string {
	h := md5.Sum([]byte(s))
	return fmt.Sprintf("%x", h)
}

func encodeQueryRFC3986(params Params) string {
	if len(params) == 0 {
		return ""
	}

	keys := make([]string, 0, len(params))
	for k := range params {
		keys = append(keys, k)
	}
	sort.Strings(keys)

	vals := make(url.Values, len(params))
	for _, k := range keys {
		v := params[k]
		if v == nil {
			continue
		}
		vals.Set(k, fmt.Sprintf("%v", v))
	}

	return vals.Encode()
}

var renameMappings = map[string]map[string]string{
	"/contract/detailV2": {
		"dn":     "displayName",
		"dne":    "displayNameEn",
		"pot":    "positionOpenType",
		"bc":     "baseCoin",
		"qc":     "quoteCoin",
		"bcn":    "baseCoinName",
		"qcn":    "quoteCoinName",
		"ft":     "futureType",
		"sc":     "settleCoin",
		"cs":     "contractSize",
		"minL":   "minLeverage",
		"maxL":   "maxLeverage",
		"ccMaxL": "countryConfigContractMaxLeverage",
		"ps":     "priceScale",
		"vs":     "volScale",
		"as":     "amountScale",
		"pu":     "priceUnit",
		"vu":     "volUnit",
		"minV":   "minVol",
		"maxV":   "maxVol",
		"blpr":   "bidLimitPriceRate",
		"alpr":   "askLimitPriceRate",
		"tfr":    "takerFeeRate",
		"mfr":    "makerFeeRate",
		"mmr":    "maintenanceMarginRate",
		"imr":    "initialMarginRate",
		"rbv":    "riskBaseVol",
		"riv":    "riskIncrVol",
		"rlss":   "riskLongShortSwitch",
		"rim":    "riskIncrMmr",
		"rii":    "riskIncrImr",
		"rll":    "riskLevelLimit",
		"pcv":    "priceCoefficientVariation",
		"io":     "indexOrigin",
		"in":     "isNew",
		"ih":     "isHot",
		"ihd":    "isHidden",
		"ip":     "isPromoted",
		"cp":     "conceptPlate",
		"cpi":    "conceptPlateId",
		"rlt":    "riskLimitType",
		"mno":    "maxNumOrders",
		"moml":   "marketOrderMaxLevel",
		"moplr1": "marketOrderPriceLimitRate1",
		"moplr2": "marketOrderPriceLimitRate2",
		"tp":     "triggerProtect",
		"ae":     "appraisal",
		"sac":    "showAppraisalCountdown",
		"ad":     "automaticDelivery",
		"aa":     "apiAllowed",
		"dsl":    "depthStepList",
		"lmv":    "limitMaxVol",
		"tsd":    "threshold",
		"bciu":   "baseCoinIconUrl",
		"bcid":   "baseCoinId",
		"ct":     "createTime",
		"ot":     "openingTime",
		"oco":    "openingCountdownOption",
		"sbo":    "showBeforeOpen",
		"iml":    "isMaxLeverage",
		"izfr":   "isZeroFeeRate",
		"rlm":    "riskLimitMode",
		"rlcs":   "riskLimitCustom",
		"izfs":   "isZeroFeeSymbol",
		"liqfr":  "liquidationFeeRate",
		"frm":    "feeRateMode",
		"levfrs": "leverageFeeRates",
		"tiefrs": "tieredFeeRates",
	},
	"/api/platform/spot/market-v2/web/symbolsV2": {
		"mcd": "marketCurrencyId",
		"cd":  "coinId",
		"vn":  "currency",
		"fn":  "currencyFullName",
		"srt": "sortOrder",
		"sts": "status",
		"tp":  "marketType",
		"in":  "icon",
		"ot":  "openingTime",
		"cp":  "categories",
		"ci":  "categories_ids",
		"ps":  "priceScale",
		"qs":  "quantityScale",
		"cdm": "contractDecimalMultiplier",
		"st":  "spotEnabled",
		"dst": "depositStatus",
		"tt":  "tradingType",
		"ca":  "contractAddress",
		"fne": "currencyFullNameEn",
	},
}

func renameFields(data any, endpoint string) any {
	for pattern, mapping := range renameMappings {
		if strings.Contains(endpoint, pattern) {
			return recursiveRename(data, mapping)
		}
	}
	return data
}

func recursiveRename(v any, mapping map[string]string) any {
	switch val := v.(type) {
	case map[string]any:
		out := make(map[string]any, len(val))
		for k, child := range val {
			newKey := k
			if renamed, ok := mapping[k]; ok {
				newKey = renamed
			}
			out[newKey] = recursiveRename(child, mapping)
		}
		return out
	case []any:
		out := make([]any, len(val))
		for i, item := range val {
			out[i] = recursiveRename(item, mapping)
		}
		return out
	default:
		return v
	}
}

const (
	defaultTimeout        = 5 * time.Second
	defaultConnectTimeout = 3 * time.Second
	defaultKeepAlive      = 60 * time.Second
	maxIdleConns          = 100
	maxIdleConnsPerHost   = 20
	idleConnTimeout       = 90 * time.Second
)

type httpClient struct {
	client *http.Client
}

func newHTTPClient(proxyCfg *ProxyConfig) (*httpClient, error) {
	transport, err := buildTransport(proxyCfg)
	if err != nil {
		return nil, err
	}

	client := &http.Client{
		Transport:     transport,
		Timeout:       defaultTimeout,
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
	}

	return &httpClient{client: client}, nil
}

func buildTransport(proxyCfg *ProxyConfig) (http.RoundTripper, error) {
	dialer := &net.Dialer{
		Timeout:   defaultConnectTimeout,
		KeepAlive: defaultKeepAlive,
	}

	base := &http.Transport{
		DialContext:           dialer.DialContext,
		ForceAttemptHTTP2:     true,
		TLSClientConfig:       &tls.Config{InsecureSkipVerify: false},
		MaxIdleConns:          maxIdleConns,
		MaxIdleConnsPerHost:   maxIdleConnsPerHost,
		IdleConnTimeout:       idleConnTimeout,
		TLSHandshakeTimeout:   5 * time.Second,
		ExpectContinueTimeout: 1 * time.Second,
		DisableCompression:    false,
	}

	if proxyCfg != nil && proxyCfg.URL != "" {
		if err := applyProxy(base, dialer, proxyCfg); err != nil {
			return nil, err
		}
	}

	return base, nil
}

func applyProxy(t *http.Transport, dialer *net.Dialer, cfg *ProxyConfig) error {
	u, err := url.Parse(cfg.URL)
	if err != nil {
		return fmt.Errorf("parse proxy URL: %w", err)
	}

	switch u.Scheme {
	case "http", "https":
		t.Proxy = http.ProxyURL(u)

	case "socks5", "socks5h":
		t.DialContext = socks5DialContext(u, dialer)

	default:
		return fmt.Errorf("unsupported proxy scheme: %s", u.Scheme)
	}

	return nil
}

func socks5DialContext(proxyURL *url.URL, baseDialer *net.Dialer) func(context.Context, string, string) (net.Conn, error) {
	return func(ctx context.Context, network, addr string) (net.Conn, error) {
		conn, err := baseDialer.DialContext(ctx, "tcp", proxyURL.Host)
		if err != nil {
			return nil, fmt.Errorf("socks5 connect: %w", err)
		}

		if err := socks5Handshake(conn, proxyURL, addr); err != nil {
			conn.Close()
			return nil, err
		}

		return conn, nil
	}
}

func socks5Handshake(conn net.Conn, proxyURL *url.URL, targetAddr string) error {
	var user, pass string
	if proxyURL.User != nil {
		user = proxyURL.User.Username()
		pass, _ = proxyURL.User.Password()
	}

	authMethod := byte(0x00)
	if user != "" {
		authMethod = 0x02
	}

	if _, err := conn.Write([]byte{0x05, 0x01, authMethod}); err != nil {
		return fmt.Errorf("socks5 greeting: %w", err)
	}

	buf := make([]byte, 2)
	if _, err := io.ReadFull(conn, buf); err != nil {
		return fmt.Errorf("socks5 greeting response: %w", err)
	}
	if buf[0] != 0x05 {
		return fmt.Errorf("socks5: unexpected version %d", buf[0])
	}
	if buf[1] != authMethod {
		return fmt.Errorf("socks5: server chose method %d, expected %d", buf[1], authMethod)
	}

	if authMethod == 0x02 {
		auth := []byte{0x01, byte(len(user))}
		auth = append(auth, []byte(user)...)
		auth = append(auth, byte(len(pass)))
		auth = append(auth, []byte(pass)...)
		if _, err := conn.Write(auth); err != nil {
			return fmt.Errorf("socks5 auth send: %w", err)
		}
		resp := make([]byte, 2)
		if _, err := io.ReadFull(conn, resp); err != nil {
			return fmt.Errorf("socks5 auth response: %w", err)
		}
		if resp[1] != 0x00 {
			return fmt.Errorf("socks5 auth failed: status %d", resp[1])
		}
	}

	host, port, err := net.SplitHostPort(targetAddr)
	if err != nil {
		return fmt.Errorf("socks5 target addr: %w", err)
	}

	var portNum int
	fmt.Sscanf(port, "%d", &portNum)

	req := []byte{0x05, 0x01, 0x00, 0x03, byte(len(host))}
	req = append(req, []byte(host)...)
	req = append(req, byte(portNum>>8), byte(portNum&0xFF))

	if _, err := conn.Write(req); err != nil {
		return fmt.Errorf("socks5 connect request: %w", err)
	}

	head := make([]byte, 4)
	if _, err := io.ReadFull(conn, head); err != nil {
		return fmt.Errorf("socks5 connect response: %w", err)
	}
	if head[1] != 0x00 {
		return fmt.Errorf("socks5 connect failed: status %d", head[1])
	}

	switch head[3] {
	case 0x01:
		drain := make([]byte, 4+2)
		io.ReadFull(conn, drain)
	case 0x03:
		lenBuf := make([]byte, 1)
		io.ReadFull(conn, lenBuf)
		drain := make([]byte, int(lenBuf[0])+2)
		io.ReadFull(conn, drain)
	case 0x04:
		drain := make([]byte, 16+2)
		io.ReadFull(conn, drain)
	}

	return nil
}

func (c *httpClient) do(ctx context.Context, method HttpMethod, rawURL string, headers map[string]string, body []byte) ([]byte, int, error) {
	var bodyReader io.Reader
	if body != nil {
		bodyReader = strings.NewReader(string(body))
	}

	req, err := http.NewRequestWithContext(ctx, method.String(), rawURL, bodyReader)
	if err != nil {
		return nil, 0, fmt.Errorf("create request: %w", err)
	}

	for k, v := range headers {
		req.Header.Set(k, v)
	}
	req.Header.Set("Accept-Encoding", "gzip, deflate, br")

	resp, err := c.client.Do(req)
	if err != nil {
		return nil, 0, fmt.Errorf("http do: %w", err)
	}
	defer resp.Body.Close()

	reader := resp.Body
	if strings.EqualFold(resp.Header.Get("Content-Encoding"), "gzip") {
		gr, err := gzip.NewReader(resp.Body)
		if err != nil {
			return nil, resp.StatusCode, fmt.Errorf("gzip reader: %w", err)
		}
		defer gr.Close()
		reader = gr
	}

	data, err := io.ReadAll(reader)
	if err != nil {
		return nil, resp.StatusCode, fmt.Errorf("read body: %w", err)
	}

	return data, resp.StatusCode, nil
}

type responseHandler struct {
	isTestnet bool
}

func newResponseHandler(isTestnet bool) *responseHandler {
	return &responseHandler{isTestnet: isTestnet}
}

func (h *responseHandler) handle(raw []byte, statusCode int, endpoint string) Response {
	if len(raw) == 0 {
		return h.errResponse(statusCode, "Request failed: empty response")
	}

	var decoded Response
	if err := json.Unmarshal(raw, &decoded); err != nil {
		preview := string(raw)
		if len(preview) > 200 {
			preview = preview[:200]
		}
		return h.errResponse(statusCode, fmt.Sprintf("Invalid JSON: %s | raw: %s", err.Error(), preview))
	}

	if data, ok := decoded["data"]; ok && endpoint != "" {
		decoded["data"] = renameFields(data, endpoint)
	}
	decoded["is_testnet"] = h.isTestnet

	return decoded
}

func (h *responseHandler) errResponse(code int, message string) Response {
	return Response{
		"success":    false,
		"code":       code,
		"message":    message,
		"timestamp":  time.Now().UnixMilli(),
		"is_testnet": h.isTestnet,
	}
}

func nowMillis() int64 {
	return time.Now().UnixMilli()
}

func orDefault(val, def any) any {
	if val == nil {
		return def
	}
	if s, ok := val.(string); ok && s == "" {
		return def
	}
	return val
}

func stringVal(v any) string {
	if v == nil {
		return ""
	}
	s, _ := v.(string)
	return s
}

func int64Val(v any) int64 {
	switch x := v.(type) {
	case int64:
		return x
	case int:
		return int64(x)
	case float64:
		return int64(x)
	}
	return 0
}

func parseSymbol(params Params) (base, quote string) {
	if sym, ok := params["symbol"].(string); ok && sym != "" {
		parts := strings.SplitN(sym, "_", 2)
		if len(parts) == 2 {
			return parts[0], parts[1]
		}
		return parts[0], ""
	}
	base, _ = params["base_coin"].(string)
	quote, _ = params["quote_coin"].(string)
	if quote == "" {
		quote = "USDT"
	}
	return
}

func filterNilParams(p Params) Params {
	out := make(Params, len(p))
	for k, v := range p {
		if v == nil {
			continue
		}
		if s, ok := v.(string); ok && s == "" {
			continue
		}
		out[k] = v
	}
	return out
}

func mustString(v any) string {
	if v == nil {
		return ""
	}
	return fmt.Sprintf("%v", v)
}

type Config struct {
	APIKey    string
	IsTestnet bool
	ProxyURL  string
}

type MexcBypass struct {
	cfg             Config
	signer          *signatureGenerator
	http            *httpClient
	responseHandler *responseHandler
	baseURLs        map[ApiType]string
	batchMu         sync.Mutex
	batchMode       bool
	pendingRequests map[string]*pendingRequest
}

func New(cfg Config) (*MexcBypass, error) {
	if cfg.ProxyURL != "" {
		pc := &ProxyConfig{URL: cfg.ProxyURL}
		if err := pc.Validate(); err != nil {
			return nil, fmt.Errorf("invalid proxy: %w", err)
		}
	}

	var proxyCfg *ProxyConfig
	if cfg.ProxyURL != "" {
		proxyCfg = &ProxyConfig{URL: cfg.ProxyURL}
	}

	hc, err := newHTTPClient(proxyCfg)
	if err != nil {
		return nil, fmt.Errorf("build http client: %w", err)
	}

	futuresBase := "https://futures.mexc.com/api/v1"
	if cfg.IsTestnet {
		futuresBase = "https://futures.testnet.mexc.com/api/v1"
	}

	return &MexcBypass{
		cfg:             cfg,
		signer:          newSignatureGenerator(cfg.APIKey),
		http:            hc,
		responseHandler: newResponseHandler(cfg.IsTestnet),
		baseURLs: map[ApiType]string{
			ApiTypeGeneral:  "https://www.mexc.com",
			ApiTypeFutures:  futuresBase,
			ApiTypeContract: "https://contract.mexc.com/api/v1",
			ApiTypeOther:    "",
		},
		pendingRequests: make(map[string]*pendingRequest),
	}, nil
}

func (m *MexcBypass) request(ctx context.Context, method HttpMethod, apiType ApiType, endpoint string, params Params) Response {
	filtered := make(Params, len(params))
	for k, v := range params {
		if v == nil {
			continue
		}
		if s, ok := v.(string); ok && s == "" {
			continue
		}
		filtered[k] = v
	}

	sig := m.signer.Generate(filtered, method, "")

	baseURL := m.resolveBase(apiType, endpoint)
	origin := extractOrigin(baseURL)
	headers := m.buildHeaders(apiType, sig, origin)
	rawURL := m.buildURL(baseURL, endpoint, method, filtered)

	var bodyBytes []byte
	if method.HasBody() {
		if len(filtered) > 0 {
			bodyBytes, _ = json.Marshal(filtered)
		}
	} else if method == MethodDELETE && len(filtered) > 0 {
		bodyBytes, _ = json.Marshal(filtered)
	}

	m.batchMu.Lock()
	if m.batchMode {
		id := uuid.New().String()
		m.pendingRequests[id] = &pendingRequest{
			id:       id,
			url:      rawURL,
			method:   method,
			headers:  headers,
			body:     filtered,
			endpoint: endpoint,
		}
		m.batchMu.Unlock()
		return Response{"_request_id": id}
	}
	m.batchMu.Unlock()

	raw, status, err := m.http.do(ctx, method, rawURL, headers, bodyBytes)
	if err != nil {
		return m.responseHandler.errResponse(status, "HTTP error: "+err.Error())
	}

	return m.responseHandler.handle(raw, status, endpoint)
}

func (m *MexcBypass) requestSlice(ctx context.Context, method HttpMethod, apiType ApiType, endpoint string, slice any) Response {
	sig := m.signer.Generate(nil, method, "")
	baseURL := m.resolveBase(apiType, endpoint)
	origin := extractOrigin(baseURL)
	headers := m.buildHeaders(apiType, sig, origin)
	rawURL := m.buildURL(baseURL, endpoint, method, nil)

	bodyBytes, _ := json.Marshal(slice)

	raw, status, err := m.http.do(ctx, method, rawURL, headers, bodyBytes)
	if err != nil {
		return m.responseHandler.errResponse(status, "HTTP error: "+err.Error())
	}

	return m.responseHandler.handle(raw, status, endpoint)
}

func (m *MexcBypass) resolveBase(apiType ApiType, endpoint string) string {
	if apiType == ApiTypeOther {
		u, err := url.Parse(endpoint)
		if err != nil || u.Host == "" {
			return ""
		}
		return u.Scheme + "://" + u.Host
	}
	return m.baseURLs[apiType]
}

func extractOrigin(baseURL string) string {
	idx := strings.Index(baseURL[8:], "/")
	if idx == -1 {
		return baseURL
	}
	return baseURL[:8+idx]
}

func (m *MexcBypass) buildHeaders(apiType ApiType, sig signatureResult, origin string) map[string]string {
	h := map[string]string{
		"Content-Type":  "application/json",
		"Accept":        "*/*",
		"User-Agent":    "Mozilla/5.0 (compatible; MEXC-API/1.0)",
		"Connection":    "keep-alive",
		"Cache-Control": "no-cache",
		"Origin":        origin,
	}

	if apiType == ApiTypeGeneral {
		h["Cookie"] = fmt.Sprintf("u_id=%s; uc_token=%s;", m.cfg.APIKey, m.cfg.APIKey)
		h["Ucenter-Token"] = m.cfg.APIKey
	} else {
		h["Authorization"] = m.cfg.APIKey
		h["X-Mxc-Nonce"] = sig.Timestamp
		h["X-Mxc-Sign"] = sig.Sign
	}

	return h
}

func (m *MexcBypass) buildURL(baseURL, endpoint string, method HttpMethod, params Params) string {
	var rawURL string
	if baseURL == "" {
		rawURL = endpoint
	} else {
		rawURL = baseURL + endpoint
	}

	if !method.HasBody() && len(params) > 0 {
		rawURL += "?" + encodeQueryRFC3986(params)
	}

	return rawURL
}

func (m *MexcBypass) errResponse(code int, msg string) Response {
	return m.responseHandler.errResponse(code, msg)
}

func weekRange() (start, end int64) {
	now := time.Now().UTC()
	weekday := int(now.Weekday())
	if weekday == 0 {
		weekday = 7 // воскресенье → 7
	}
	monday := now.AddDate(0, 0, -(weekday - 1))
	monday = time.Date(monday.Year(), monday.Month(), monday.Day(), 0, 0, 0, 0, time.UTC)
	sunday := monday.AddDate(0, 0, 6)
	sunday = time.Date(sunday.Year(), sunday.Month(), sunday.Day(), 23, 59, 59, 99_000_000, time.UTC)
	return monday.UnixMilli(), sunday.UnixMilli()
}

func floatVal(v any) float64 {
	switch x := v.(type) {
	case float64:
		return x
	case float32:
		return float64(x)
	case int:
		return float64(x)
	case int64:
		return float64(x)
	case string:
		var f float64
		fmt.Sscanf(x, "%f", &f)
		return f
	}
	return 0
}

func intVal(v any) int {
	switch x := v.(type) {
	case float64:
		return int(x)
	case int:
		return x
	case int64:
		return int(x)
	}
	return 0
}

type BatchFunc func() Response

//	results := client.Batch(ctx, map[string]BatchFunc{
//	    "ticker":   func() Response { return client.GetFuturesTickers(ctx, Params{"symbol": "BTC_USDT"}) },
//	    "contract": func() Response { return client.GetFuturesContracts(ctx, Params{"symbol": "BTC_USDT"}) },
//	})
func (m *MexcBypass) Batch(ctx context.Context, requests map[string]BatchFunc) map[string]Response {
	// Включаем batch-режим — все вложенные request() будут ставить запросы в очередь
	m.batchMu.Lock()
	m.batchMode = true
	m.pendingRequests = make(map[string]*pendingRequest)
	m.batchMu.Unlock()

	keyToID := make(map[string]string)
	results := make(map[string]Response)

	for key, fn := range requests {
		ret := fn()
		if id, ok := ret["_request_id"].(string); ok {
			keyToID[key] = id
		} else {
			results[key] = ret
		}
	}

	pending := m.drainPending()
	parallel := m.executePendingMap(ctx, keyToID, pending)
	for k, v := range parallel {
		results[k] = v
	}

	m.batchMu.Lock()
	m.batchMode = false
	m.pendingRequests = make(map[string]*pendingRequest)
	m.batchMu.Unlock()

	return results
}

type AccountConfig struct {
	APIKey    string
	IsTestnet bool
	ProxyURL  string
}

//
//	results, err := BatchAccounts(ctx,
//	    map[string]AccountConfig{
//	        "acc1": {APIKey: "key1"},
//	        "acc2": {APIKey: "key2", IsTestnet: true},
//	    },
//	    func(c *MexcBypass) map[string]BatchFunc {
//	        return map[string]BatchFunc{
//	            "positions": func() Response { return c.GetFuturesOpenPositions(ctx, nil) },
//	            "balance":   func() Response { return c.GetFuturesAssets(ctx, Params{"currency": "USDT"}) },
//	        }
//	    },
//	)
func BatchAccounts(
	ctx context.Context,
	accounts map[string]AccountConfig,
	callback func(*MexcBypass) map[string]BatchFunc,
) (map[string]map[string]Response, error) {
	type accountWork struct {
		client   *MexcBypass
		keyToID  map[string]string
		pending  map[string]*pendingRequest
		prebuilt map[string]Response
	}

	works := make(map[string]*accountWork, len(accounts))

	for accountKey, cfg := range accounts {
		c, err := New(Config{
			APIKey:    cfg.APIKey,
			IsTestnet: cfg.IsTestnet,
			ProxyURL:  cfg.ProxyURL,
		})
		if err != nil {
			return nil, err
		}

		c.batchMu.Lock()
		c.batchMode = true
		c.pendingRequests = make(map[string]*pendingRequest)
		c.batchMu.Unlock()

		work := &accountWork{
			client:   c,
			keyToID:  make(map[string]string),
			prebuilt: make(map[string]Response),
		}

		for reqKey, fn := range callback(c) {
			ret := fn()
			if id, ok := ret["_request_id"].(string); ok {
				work.keyToID[reqKey] = id
			} else {
				work.prebuilt[reqKey] = ret
			}
		}

		work.pending = c.drainPending()
		works[accountKey] = work
	}

	type taskResult struct {
		accountKey string
		reqKey     string
		response   Response
	}

	var totalTasks int
	for _, w := range works {
		totalTasks += len(w.keyToID)
	}

	taskCh := make(chan taskResult, totalTasks)
	var wg sync.WaitGroup

	for accountKey, work := range works {
		for reqKey, pendingID := range work.keyToID {
			wg.Add(1)
			p := work.pending[pendingID]
			c := work.client
			ak := accountKey
			rk := reqKey

			go func() {
				defer wg.Done()
				resp := executeSingle(ctx, c, p)
				taskCh <- taskResult{accountKey: ak, reqKey: rk, response: resp}
			}()
		}
	}

	go func() {
		wg.Wait()
		close(taskCh)
	}()

	results := make(map[string]map[string]Response, len(accounts))
	for accountKey, work := range works {
		results[accountKey] = make(map[string]Response, len(work.keyToID)+len(work.prebuilt))
		for k, v := range work.prebuilt {
			results[accountKey][k] = v
		}
	}

	for tr := range taskCh {
		results[tr.accountKey][tr.reqKey] = tr.response
	}

	return results, nil
}

func (m *MexcBypass) drainPending() map[string]*pendingRequest {
	m.batchMu.Lock()
	defer m.batchMu.Unlock()

	out := make(map[string]*pendingRequest, len(m.pendingRequests))
	for k, v := range m.pendingRequests {
		out[k] = v
	}
	m.pendingRequests = make(map[string]*pendingRequest)
	return out
}

func (m *MexcBypass) executePendingMap(ctx context.Context, keyToID map[string]string, pending map[string]*pendingRequest) map[string]Response {
	if len(keyToID) == 0 {
		return nil
	}

	type result struct {
		key  string
		resp Response
	}

	ch := make(chan result, len(keyToID))
	var wg sync.WaitGroup

	for key, pendingID := range keyToID {
		p, ok := pending[pendingID]
		if !ok {
			continue
		}
		wg.Add(1)
		go func(k string, pr *pendingRequest) {
			defer wg.Done()
			ch <- result{key: k, resp: executeSingle(ctx, m, pr)}
		}(key, p)
	}

	go func() {
		wg.Wait()
		close(ch)
	}()

	out := make(map[string]Response, len(keyToID))
	for r := range ch {
		out[r.key] = r.resp
	}
	return out
}

func executeSingle(ctx context.Context, m *MexcBypass, p *pendingRequest) Response {
	var bodyBytes []byte
	if p.method.HasBody() || p.method == MethodDELETE {
		if len(p.body) > 0 {
			bodyBytes, _ = json.Marshal(p.body)
		}
	}

	raw, status, err := m.http.do(ctx, p.method, p.url, p.headers, bodyBytes)
	if err != nil {
		return m.responseHandler.errResponse(status, "HTTP error: "+err.Error())
	}
	return m.responseHandler.handle(raw, status, p.endpoint)
}

func (m *MexcBypass) Ping(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeGeneral, "/api/common/ping", nil)
}

func (m *MexcBypass) GetServerTime(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeContract, "/contract/ping", nil)
}

func (m *MexcBypass) Validation(ctx context.Context) Response {
	return m.request(ctx, MethodPOST, ApiTypeGeneral, "/ucenter/api/login/validation", nil)
}

func (m *MexcBypass) GetWSToken(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeGeneral, "/ucenter/api/ws_token", nil)
}

func (m *MexcBypass) GetCustomerInfo(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeGeneral, "/ucenter/api/customer_info", nil)
}

func (m *MexcBypass) GetUserInfo(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeGeneral, "/ucenter/api/user_info", nil)
}

func (m *MexcBypass) GetLatestDeposits(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeGeneral, "/api/platform/deposit/v3/latest", nil)
}

func (m *MexcBypass) GetDepositAddresses(ctx context.Context, params Params) Response {
	if currency, ok := params["currency"].(string); ok && currency != "" {
		return m.request(ctx, MethodGET, ApiTypeGeneral,
			fmt.Sprintf("/api/platform/asset/api/asset/spot/currency/v3?currency=%s", currency), nil)
	}
	vcoinID := fmt.Sprintf("%v", params["vcoin_id"])
	return m.request(ctx, MethodGET, ApiTypeGeneral,
		fmt.Sprintf("/api/platform/asset/api/deposit/address/list?vcoinId=%s", vcoinID), nil)
}

func (m *MexcBypass) GetSecureInfo(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeGeneral, "/ucenter/api/secure/check", nil)
}

func (m *MexcBypass) GetOriginInfo(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeGeneral, "/ucenter/api/origin_info", nil)
}

func (m *MexcBypass) Logout(ctx context.Context) Response {
	return m.request(ctx, MethodPOST, ApiTypeGeneral, "/ucenter/api/logout", nil)
}

func (m *MexcBypass) GetAssetsOverview(ctx context.Context, params Params) Response {
	endpoint := "/api/platform/asset/api/asset/overview/v2"
	if convert, ok := params["convert"].(bool); ok && convert {
		endpoint = "/api/platform/asset/api/asset/overview/convert/v2"
	}
	return m.request(ctx, MethodGET, ApiTypeGeneral, endpoint, nil)
}

func (m *MexcBypass) GetReferralsList(ctx context.Context, params Params) Response {
	start, end := weekRange()
	return m.request(ctx, MethodGET, ApiTypeGeneral, "/api/assetbussiness/invite/invites", Params{
		"startTime": orDefault(params["start_time"], start),
		"endTime":   orDefault(params["end_time"], end),
		"page":      orDefault(params["page_num"], 1),
		"pageSize":  orDefault(params["page_size"], 10),
	})
}

func (m *MexcBypass) GetMarketSymbols(ctx context.Context, params Params) Response {
	response := m.request(ctx, MethodGET, ApiTypeGeneral, "/api/platform/spot/market-v2/web/symbolsV2", nil)

	if len(params) == 0 {
		return response
	}

	baseCoin, quoteCoin := parseSymbol(params)
	if baseCoin == "" {
		return response
	}

	data, ok := response["data"].(map[string]any)
	if !ok {
		return m.errResponse(400, "unexpected data format")
	}

	symbolsRaw, ok := data["symbols"].(map[string]any)
	if !ok {
		return m.errResponse(400, "no symbols in response")
	}

	quoteBucket, ok := symbolsRaw[quoteCoin].([]any)
	if !ok {
		return m.errResponse(400, fmt.Sprintf("No %s symbols found", quoteCoin))
	}

	search := strings.ToUpper(baseCoin)
	var matches []any
	for _, item := range quoteBucket {
		obj, ok := item.(map[string]any)
		if !ok {
			continue
		}
		if c, ok := obj["currency"].(string); ok && strings.ToUpper(c) == search {
			matches = append(matches, obj)
		}
	}

	if len(matches) == 0 {
		return m.errResponse(404, "No matching tokens found")
	}

	return Response{
		"success":   true,
		"count":     len(matches),
		"data":      matches,
		"timestamp": nowMillis(),
	}
}

func (m *MexcBypass) GetSpotOrderBook(ctx context.Context, params Params) Response {
	symbols := m.GetMarketSymbols(ctx, Params{"symbol": params["symbol"]})
	if !symbols.Success() {
		return symbols
	}

	items, _ := symbols["data"].([]any)
	if len(items) == 0 {
		return m.errResponse(400, "no symbol data")
	}

	first, _ := items[0].(map[string]any)

	return m.request(ctx, MethodGET, ApiTypeGeneral, "/api/platform/spot/market-v2/web/depth/v2", Params{
		"symbolId": first["id"],
		"decimal":  orDefault(params["decimal"], "0.0000001"),
		"count":    orDefault(params["count"], 100),
	})
}

func (m *MexcBypass) CreateSpotOrder(ctx context.Context, params Params) Response {
	orderType, _ := params["type"].(string)
	path := "/api/platform/spot/v4/order/place"
	if orderType == "LIMIT_ORDER" {
		path = "/api/platform/spot/order/place"
	}

	symbols := m.GetMarketSymbols(ctx, Params{"symbol": params["symbol"]})
	if !symbols.Success() {
		return symbols
	}

	items, _ := symbols["data"].([]any)
	if len(items) == 0 {
		return m.errResponse(400, "no symbol data")
	}
	first, _ := items[0].(map[string]any)

	return m.request(ctx, MethodPOST, ApiTypeGeneral, path, Params{
		"orderType":        orderType,
		"tradeType":        params["side"],
		"price":            params["price"],
		"amount":           params["amount"],
		"quantity":         params["quantity"],
		"marketCurrencyId": first["marketCurrencyId"],
		"currencyId":       first["coinId"],
		"orderSource":      "WEB",
		"ts":               nowMillis(),
	})
}

func (m *MexcBypass) CancelSpotOrder(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodDELETE, ApiTypeGeneral, "/api/platform/spot/order/cancel/v2", Params{
		"orderId": params["order_id"],
	})
}

func (m *MexcBypass) GetFuturesFeeRate(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/account/contract/fee_rate", nil)
}

func (m *MexcBypass) GetFuturesZeroFeeRate(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/account/contract/zero_fee_rate", nil)
}

func (m *MexcBypass) GetFuturesTodayPnL(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/account/asset/analysis/today_pnl", nil)
}

func (m *MexcBypass) GetFuturesAnalysis(ctx context.Context, params Params) Response {
	now := nowMillis()
	endTime := int64Val(orDefault(params["end_time"], now))
	if endTime > now {
		endTime = now
	}
	startTime := int64Val(orDefault(params["start_time"], endTime-7*86_400_000))
	if startTime >= endTime {
		startTime = endTime - 1000
	}

	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/account/asset/analysis/v3", Params{
		"currency":               orDefault(params["currency"], "USDT"),
		"symbol":                 params["symbol"],
		"include_unrealised_pnl": orDefault(params["include_unrealised_pnl"], 0),
		"reverse":                orDefault(params["reverse"], 0),
		"startTime":              startTime,
		"endTime":                endTime,
	})
}

func (m *MexcBypass) GetFuturesAssets(ctx context.Context, params Params) Response {
	if currency, ok := params["currency"].(string); ok && currency != "" {
		return m.request(ctx, MethodGET, ApiTypeFutures, fmt.Sprintf("/private/account/asset/%s", currency), nil)
	}
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/account/assets", nil)
}

func (m *MexcBypass) GetFuturesAssetTransferRecords(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/account/transfer_record", Params{
		"currency":  params["currency"],
		"state":     params["state"],
		"type":      params["type"],
		"page_num":  orDefault(params["page_num"], 1),
		"page_size": orDefault(params["page_size"], 20),
	})
}

func (m *MexcBypass) GetFuturesRiskLimits(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/account/risk_limit", Params{
		"symbol": params["symbol"],
	})
}

func (m *MexcBypass) GetFuturesFundingRate(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeContract,
		fmt.Sprintf("/contract/funding_rate/%v", params["symbol"]), nil)
}

func (m *MexcBypass) GetFuturesContracts(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/contract/detailV2", Params{
		"symbol": params["symbol"],
	})
}

func (m *MexcBypass) GetFuturesContractIndexPrice(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeContract,
		fmt.Sprintf("/contract/index_price/%v", params["symbol"]), nil)
}

func (m *MexcBypass) GetFuturesContractFairPrice(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeContract,
		fmt.Sprintf("/contract/fair_price/%v", params["symbol"]), nil)
}

func (m *MexcBypass) GetFuturesContractKlineData(ctx context.Context, params Params) Response {
	pt := ParsePriceType(stringVal(params["price_type"]))
	segment := pt.KlinePathSegment()

	var path string
	symbol := fmt.Sprintf("%v", params["symbol"])
	if segment != "" {
		path = fmt.Sprintf("/contract/kline/%s/%s", segment, symbol)
	} else {
		path = fmt.Sprintf("/contract/kline/%s", symbol)
	}

	return m.request(ctx, MethodGET, ApiTypeContract, path, Params{
		"interval": orDefault(params["interval"], "Min1"),
		"start":    params["start_time"],
		"end":      params["end_time"],
	})
}

func (m *MexcBypass) GetFuturesTickers(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/contract/ticker", Params{
		"symbol": params["symbol"],
	})
}

func (m *MexcBypass) GetFuturesOpenPositions(ctx context.Context, params Params) Response {
	p := Params{}
	if s, ok := params["symbol"]; ok {
		p["symbol"] = s
	}
	if pid, ok := params["position_id"]; ok {
		p["positionId"] = pid
	}
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/position/open_positions", p)
}

func (m *MexcBypass) GetFuturesPositionsHistory(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/position/list/history_positions", Params{
		"symbol":    params["symbol"],
		"type":      params["type"],
		"page_num":  orDefault(params["page_num"], 1),
		"page_size": orDefault(params["page_size"], 20),
	})
}

func (m *MexcBypass) CloseAllFuturesPositions(ctx context.Context) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/position/close_all", nil)
}

func (m *MexcBypass) ReverseFuturesPosition(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/position/reverse", Params{
		"positionId": params["position_id"],
		"symbol":     params["symbol"],
		"vol":        params["vol"],
	})
}

func (m *MexcBypass) GetFuturesPositionMode(ctx context.Context) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/position/position_mode", nil)
}

func (m *MexcBypass) ChangeFuturesPositionMode(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/position/change_position_mode", Params{
		"positionMode": params["position_mode"],
	})
}

func (m *MexcBypass) GetFuturesLeverage(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/position/leverage", Params{
		"symbol": params["symbol"],
	})
}

func (m *MexcBypass) ChangeFuturesPositionMargin(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/position/change_margin", Params{
		"positionId": params["position_id"],
		"amount":     params["amount"],
		"type":       params["type"],
	})
}

func (m *MexcBypass) ChangeFuturesPositionLeverage(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/position/change_leverage", Params{
		"positionId":   params["position_id"],
		"leverage":     params["leverage"],
		"openType":     params["open_type"],
		"symbol":       params["symbol"],
		"positionType": params["position_type"],
	})
}

func (m *MexcBypass) GetFuturesOrdersDeals(ctx context.Context, params Params) Response {
	start, end := weekRange()
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/order/list/order_deals", Params{
		"symbol":     params["symbol"],
		"start_time": orDefault(params["start_time"], start),
		"end_time":   orDefault(params["end_time"], end),
		"page_num":   orDefault(params["page_num"], 1),
		"page_size":  orDefault(params["page_size"], 20),
	})
}

func (m *MexcBypass) GetFuturesPendingOrders(ctx context.Context, params Params) Response {
	endpoint := "/private/order/list/open_orders/"
	if symbol, ok := params["symbol"].(string); ok && symbol != "" {
		endpoint = fmt.Sprintf("/private/order/list/open_orders/%s", symbol)
	}
	return m.request(ctx, MethodGET, ApiTypeFutures, endpoint, Params{
		"page_num":  orDefault(params["page_num"], 1),
		"page_size": orDefault(params["page_size"], 20),
	})
}

func (m *MexcBypass) GetFuturesOrdersHistory(ctx context.Context, params Params) Response {
	start, end := weekRange()
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/order/list/history_orders", Params{
		"symbol":     params["symbol"],
		"states":     params["states"],
		"category":   params["category"],
		"side":       params["side"],
		"start_time": orDefault(params["start_time"], start),
		"end_time":   orDefault(params["end_time"], end),
		"page_num":   orDefault(params["page_num"], 1),
		"page_size":  orDefault(params["page_size"], 20),
	})
}

func (m *MexcBypass) GetFuturesOpenLimitOrders(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/order/list/open_orders", Params{
		"page_size": orDefault(params["page_size"], 200),
	})
}

func (m *MexcBypass) GetFuturesOpenStopOrders(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/stoporder/open_orders", Params{
		"page_size": orDefault(params["page_size"], 200),
	})
}

// GetFuturesOpenOrders возвращает объединённый список лимитных и стоп-ордеров.
func (m *MexcBypass) GetFuturesOpenOrders(ctx context.Context, params Params) Response {
	pageSize := orDefault(params["page_size"], 200)
	result := m.Batch(ctx, map[string]BatchFunc{
		"limit_orders": func() Response { return m.GetFuturesOpenLimitOrders(ctx, Params{"page_size": pageSize}) },
		"stop_orders":  func() Response { return m.GetFuturesOpenStopOrders(ctx, Params{"page_size": pageSize}) },
	})

	var merged []any
	if r := result["limit_orders"]; r.Success() {
		if data, ok := r["data"].([]any); ok {
			merged = append(merged, data...)
		}
	}
	if r := result["stop_orders"]; r.Success() {
		if data, ok := r["data"].([]any); ok {
			merged = append(merged, data...)
		}
	}

	return Response{"success": true, "code": 0, "data": merged}
}

func (m *MexcBypass) GetFuturesClosedOrders(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/order/close_orders", Params{
		"symbol":    params["symbol"],
		"category":  params["category"],
		"page_num":  orDefault(params["page_num"], 1),
		"page_size": orDefault(params["page_size"], 20),
	})
}

func (m *MexcBypass) GetFuturesOrdersByID(ctx context.Context, params Params) Response {
	idsRaw := fmt.Sprintf("%v", params["ids"])
	ids := strings.Split(idsRaw, ",")
	for i := range ids {
		ids[i] = strings.TrimSpace(ids[i])
	}

	if len(ids) > 1 {
		return m.request(ctx, MethodGET, ApiTypeFutures, "/private/order/batch_query", Params{
			"order_ids": idsRaw,
		})
	}
	return m.request(ctx, MethodGET, ApiTypeFutures, fmt.Sprintf("/private/order/get/%s", idsRaw), nil)
}

func (m *MexcBypass) CreateFuturesOrder(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/order/create", Params{
		"symbol":          params["symbol"],
		"price":           params["price"],
		"type":            params["type"],
		"openType":        params["open_type"],
		"positionMode":    params["position_mode"],
		"side":            params["side"],
		"vol":             params["vol"],
		"leverage":        params["leverage"],
		"positionId":      params["position_id"],
		"externalOid":     params["external_id"],
		"takeProfitPrice": params["take_profit_price"],
		"profitTrend":     orDefault(params["take_profit_trend"], 1),
		"stopLossPrice":   params["stop_loss_price"],
		"lossTrend":       orDefault(params["stop_loss_trend"], 1),
		"priceProtect":    orDefault(params["price_protect"], 0),
		"reduceOnly":      orDefault(params["reduce_only"], false),
		"marketCeiling":   orDefault(params["market_ceiling"], false),
		"flashClose":      params["flash_close"],
		"bboTypeNum":      params["bbo_type"],
	})
}

func (m *MexcBypass) CancelFuturesOrders(ctx context.Context, params Params) Response {
	var ids []string
	switch v := params["ids"].(type) {
	case []string:
		ids = v
	case string:
		for _, s := range strings.Split(v, ",") {
			ids = append(ids, strings.TrimSpace(s))
		}
	case []any:
		for _, item := range v {
			ids = append(ids, fmt.Sprintf("%v", item))
		}
	}
	return m.requestSlice(ctx, MethodPOST, ApiTypeFutures, "/private/order/cancel", ids)
}

func (m *MexcBypass) CancelFuturesOrderWithExternalID(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/order/cancel_with_external", Params{
		"symbol":      params["symbol"],
		"externalOid": params["external_id"],
	})
}

func (m *MexcBypass) CancelAllFuturesOrders(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/order/cancel_all", Params{
		"symbol": params["symbol"],
	})
}

func (m *MexcBypass) ChangeFuturesLimitOrderPrice(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "private/order/change_limit_order", Params{
		"orderId": params["order_id"],
		"price":   params["price"],
		"vol":     params["vol"],
	})
}

func (m *MexcBypass) ChaseFuturesOrder(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/order/chase_limit_order", Params{
		"orderId": params["order_id"],
	})
}

func (m *MexcBypass) CreateFuturesChaseOrder(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/order/chase_limit/place", Params{
		"chaseType":        params["chase_type"],
		"distanceType":     params["distance_type"],
		"distanceValue":    params["distance_value"],
		"maxDistanceType":  params["max_distance_type"],
		"maxDistanceValue": params["max_distance_value"],
		"leverage":         params["leverage"],
		"openType":         params["open_type"],
		"side":             params["side"],
		"symbol":           params["symbol"],
		"vol":              params["vol"],
	})
}

func (m *MexcBypass) CreateFuturesStopOrder(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/stoporder/place/v2", Params{
		"positionId":           params["position_id"],
		"volType":              orDefault(params["vol_type"], 2),
		"vol":                  params["vol"],
		"takeProfitType":       params["take_profit_type"],
		"takeProfitOrderPrice": params["take_profit_order_price"],
		"takeProfitPrice":      params["take_profit_price"],
		"takeProfitReverse":    orDefault(params["take_profit_reverse"], 2),
		"profitTrend":          orDefault(params["take_profit_trend"], 1),
		"takeProfitVol":        params["take_profit_volume"],
		"stopLossType":         params["stop_loss_type"],
		"stopLossOrderPrice":   params["stop_loss_order_price"],
		"stopLossPrice":        params["stop_loss_price"],
		"stopLossReverse":      orDefault(params["stop_loss_reverse"], 2),
		"lossTrend":            orDefault(params["stop_loss_trend"], 1),
		"profitLossVolType":    orDefault(params["profit_loss_vol_type"], "SAME"),
		"priceProtect":         orDefault(params["price_protect"], 0),
	})
}

func (m *MexcBypass) GetFuturesStopLimitOrders(ctx context.Context, params Params) Response {
	start, end := weekRange()
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/stoporder/list/orders", Params{
		"symbol":      params["symbol"],
		"is_finished": params["is_finished"],
		"start_time":  orDefault(params["start_time"], start),
		"end_time":    orDefault(params["end_time"], end),
		"page_num":    orDefault(params["page_num"], 1),
		"page_size":   orDefault(params["page_size"], 20),
	})
}

func (m *MexcBypass) CancelStopLimitOrders(ctx context.Context, params Params) Response {
	var ids []string
	switch v := params["ids"].(type) {
	case string:
		for _, s := range strings.Split(v, ",") {
			if t := strings.TrimSpace(s); t != "" {
				ids = append(ids, t)
			}
		}
	case []string:
		ids = v
	case []any:
		for _, item := range v {
			ids = append(ids, fmt.Sprintf("%v", item))
		}
	}

	type stopOrderID struct {
		StopPlanOrderID string `json:"stopPlanOrderId"`
	}
	payload := make([]stopOrderID, len(ids))
	for i, id := range ids {
		payload[i] = stopOrderID{StopPlanOrderID: id}
	}
	return m.requestSlice(ctx, MethodPOST, ApiTypeFutures, "/private/stoporder/cancel", payload)
}

func (m *MexcBypass) CancelAllFuturesStopLimitOrders(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/stoporder/cancel_all", Params{
		"positionId": params["position_id"],
		"symbol":     params["symbol"],
	})
}

func (m *MexcBypass) ChangeFuturesOrderStopLimitPrice(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/stoporder/change_price", Params{
		"orderId":         params["order_id"],
		"takeProfitPrice": params["take_profit_price"],
		"stopLossPrice":   params["stop_loss_price"],
	})
}

func (m *MexcBypass) ChangeFuturesPlanOrderStopLimitPrice(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/stoporder/change_plan_price", Params{
		"stopPlanOrderId": params["order_id"],
		"takeProfitPrice": params["take_profit_price"],
		"stopLossPrice":   params["stop_loss_price"],
	})
}

func (m *MexcBypass) ChangeFuturesOrderTargets(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/stoporder/change_plan_order", Params{
		"orderId":          params["real_order_id"],
		"profitTrend":      params["take_profit_trend"],
		"takeProfitPrice":  params["take_profit_price"],
		"takeProfitVolume": params["take_profit_volume"],
		"lossTrend":        params["stop_loss_trend"],
		"stopLossPrice":    params["stop_loss_price"],
		"stopLossVolume":   params["stop_loss_volume"],
	})
}

func (m *MexcBypass) CreateFuturesTriggerOrder(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/planorder/place", Params{
		"symbol":       params["symbol"],
		"price":        params["price"],
		"vol":          params["vol"],
		"leverage":     params["leverage"],
		"side":         params["side"],
		"openType":     params["open_type"],
		"triggerPrice": params["trigger_price"],
		"triggerType":  params["trigger_type"],
		"executeCycle": params["execute_cycle"],
		"orderType":    params["order_type"],
		"trend":        params["trend"],
	})
}

func (m *MexcBypass) GetFuturesTriggerOrders(ctx context.Context, params Params) Response {
	start, end := weekRange()
	return m.request(ctx, MethodGET, ApiTypeFutures, "/private/planorder/list/orders", Params{
		"symbol":     params["symbol"],
		"states":     params["states"],
		"start_time": orDefault(params["start_time"], start),
		"end_time":   orDefault(params["end_time"], end),
		"page_num":   orDefault(params["page_num"], 1),
		"page_size":  orDefault(params["page_size"], 20),
	})
}

func (m *MexcBypass) CancelFuturesTriggerOrders(ctx context.Context, params Params) Response {
	return m.requestSlice(ctx, MethodPOST, ApiTypeFutures, "/private/planorder/cancel", params["ids"])
}

func (m *MexcBypass) CancelAllFuturesTriggerOrders(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/planorder/cancel_all", Params{
		"symbol": params["symbol"],
	})
}

func (m *MexcBypass) CreateFuturesTrailingOrder(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/trackorder/place", Params{
		"symbol":       params["symbol"],
		"leverage":     params["leverage"],
		"side":         params["side"],
		"vol":          params["vol"],
		"openType":     params["open_type"],
		"trend":        params["trend"],
		"activePrice":  params["active_price"],
		"backType":     params["back_type"],
		"backValue":    params["back_value"],
		"positionMode": orDefault(params["position_mode"], 0),
		"reduceOnly":   params["reduce_only"],
	})
}

func (m *MexcBypass) CancelFuturesTrailingOrder(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/trackorder/cancel", Params{
		"symbol":       params["symbol"],
		"trackOrderId": params["order_id"],
	})
}

func (m *MexcBypass) ChangeFuturesTrailingOrder(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeFutures, "/private/trackorder/change_order", Params{
		"symbol":       params["symbol"],
		"trackOrderId": params["order_id"],
		"trend":        params["trend"],
		"activePrice":  params["active_price"],
		"backType":     params["back_type"],
		"backValue":    params["back_value"],
		"vol":          params["vol"],
	})
}

func (m *MexcBypass) ReceiveFuturesTestnetAsset(ctx context.Context, params Params) Response {
	return m.request(ctx, MethodPOST, ApiTypeOther,
		"https://futures.testnet.mexc.com/mock/contract/asset/receive", Params{
			"currency": orDefault(params["currency"], "USDT"),
			"amount":   params["amount"],
		})
}

func (m *MexcBypass) CalculateFuturesPositionPnL(ctx context.Context, params Params) Response {
	symbol := stringVal(params["symbol"])

	result := m.Batch(ctx, map[string]BatchFunc{
		"contract": func() Response { return m.GetFuturesContracts(ctx, Params{"symbol": symbol}) },
		"ticker":   func() Response { return m.GetFuturesTickers(ctx, Params{"symbol": symbol}) },
	})

	contractResp := result["contract"]
	tickerResp := result["ticker"]

	contractData, _ := contractResp["data"].([]any)
	if len(contractData) == 0 {
		return m.errResponse(-1, "Unable to calculate PnL: missing contract data")
	}
	contract, _ := contractData[0].(map[string]any)

	ticker, _ := tickerResp["data"].(map[string]any)
	if contract == nil || ticker == nil {
		return m.errResponse(-1, "Unable to calculate PnL: missing market data")
	}

	cs := floatVal(contract["contractSize"])
	if cs <= 0 {
		return m.errResponse(-1, "Unable to calculate PnL: invalid contract size")
	}

	pt := ParsePriceType(stringVal(params["price_type"]))
	price := floatVal(ticker[pt.TickerField()])
	entry := floatVal(params["entry_price"])
	volume := floatVal(params["volume"])
	leverage := float64(intVal(params["leverage"]))
	if leverage < 1 {
		leverage = 1
	}

	isLong := intVal(params["side"]) == 1
	var pnl float64
	if isLong {
		pnl = (price - entry) * volume * cs
	} else {
		pnl = (entry - price) * volume * cs
	}

	initialMargin := entry * volume * cs / leverage

	var pnlPercent float64
	if initialMargin > 0 {
		pnlPercent = roundN(pnl/initialMargin*100, 2)
	}

	return Response{
		"success": true,
		"code":    0,
		"data": map[string]any{
			"pnl":           roundN(pnl, 4),
			"pnl_percent":   pnlPercent,
			"volume":        volume,
			"entry_price":   entry,
			"current_price": price,
			"price_type":    string(pt),
		},
	}
}

func (m *MexcBypass) CalculateFuturesVolume(ctx context.Context, params Params) Response {
	for _, req := range []string{"symbol", "amount", "leverage"} {
		if params[req] == nil || params[req] == "" {
			return m.errResponse(404, "Missing required parameter: "+req)
		}
	}

	symbol := stringVal(params["symbol"])
	market := m.fetchTickerAndContract(ctx, symbol)
	if !market["ok"].(bool) {
		return market["error"].(Response)
	}

	ticker, contract, pt := m.resolveMarket(market, params)
	if ticker == nil {
		return m.errResponse(400, "Invalid price value")
	}

	amount := floatVal(params["amount"])
	if amount <= 0 {
		return m.errResponse(400, "Amount must be positive")
	}
	lev := intVal(params["leverage"])
	if lev <= 0 {
		return m.errResponse(400, "Leverage must be positive")
	}

	cs := floatVal(contract["contractSize"])
	if cs <= 0 {
		return m.errResponse(400, "Invalid contract size")
	}

	price := floatVal(ticker)
	minL := intVal(contract["minLeverage"])
	maxL := intVal(contract["maxLeverage"])
	leverage := clampInt(lev, minL, maxL)

	rawVol := (amount * float64(leverage)) / (price * cs)
	volume := m.normalizeVolume(rawVol, contract)
	usdt := roundN(volume*price*cs/float64(leverage), intVal(contract["priceScale"]))

	return Response{
		"success": true,
		"code":    0,
		"data":    m.volumePayload(contract, volume, leverage, price, usdt, pt),
	}
}

func (m *MexcBypass) CalculateFuturesVolumeFromBaseAmount(ctx context.Context, params Params) Response {
	for _, req := range []string{"symbol", "amount", "leverage"} {
		if params[req] == nil || params[req] == "" {
			return m.errResponse(404, "Missing required parameter: "+req)
		}
	}

	symbol := stringVal(params["symbol"])
	market := m.fetchTickerAndContract(ctx, symbol)
	if !market["ok"].(bool) {
		return market["error"].(Response)
	}

	ticker, contract, pt := m.resolveMarket(market, params)
	if ticker == nil {
		return m.errResponse(400, "Invalid price value")
	}

	amount := floatVal(params["amount"])
	if amount <= 0 {
		return m.errResponse(400, "Base amount must be positive")
	}
	lev := intVal(params["leverage"])
	if lev <= 0 {
		return m.errResponse(400, "Leverage must be positive")
	}

	cs := floatVal(contract["contractSize"])
	if cs <= 0 {
		return m.errResponse(400, "Invalid contract size")
	}

	price := floatVal(ticker)
	minL := intVal(contract["minLeverage"])
	maxL := intVal(contract["maxLeverage"])
	leverage := clampInt(lev, minL, maxL)

	rawVol := amount / cs
	volume := m.normalizeVolume(rawVol, contract)
	notional := amount * price
	usdt := roundN(notional, intVal(contract["priceScale"]))

	base := m.volumePayload(contract, volume, leverage, price, usdt, pt)
	base["required_margin"] = roundN(notional/float64(leverage), intVal(contract["priceScale"]))
	base["amount"] = amount
	base["notional_value"] = notional
	base["contract_size"] = cs

	return Response{"success": true, "code": 0, "data": base}
}

func (m *MexcBypass) fetchTickerAndContract(ctx context.Context, symbol string) map[string]any {
	result := m.Batch(ctx, map[string]BatchFunc{
		"ticker":   func() Response { return m.GetFuturesTickers(ctx, Params{"symbol": symbol}) },
		"contract": func() Response { return m.GetFuturesContracts(ctx, Params{"symbol": symbol}) },
	})

	ticker := result["ticker"]
	contract := result["contract"]

	if !ticker.Success() {
		return map[string]any{"ok": false, "error": m.errResponse(400, "Failed to get ticker data")}
	}
	contractData, _ := contract["data"].([]any)
	if !contract.Success() || len(contractData) == 0 {
		return map[string]any{"ok": false, "error": m.errResponse(400, "Failed to get contract data")}
	}

	return map[string]any{"ok": true, "result": map[string]Response{"ticker": ticker, "contract": contract}}
}

func (m *MexcBypass) resolveMarket(market map[string]any, params Params) (ticker any, contract map[string]any, pt PriceType) {
	result := market["result"].(map[string]Response)
	pt = ParsePriceType(stringVal(params["price_type"]))

	tickerData, _ := result["ticker"]["data"].(map[string]any)
	contractData, _ := result["contract"]["data"].([]any)
	if len(contractData) > 0 {
		contract, _ = contractData[0].(map[string]any)
	}

	if tickerData != nil {
		ticker = tickerData[pt.TickerField()]
	}

	return
}

func (m *MexcBypass) normalizeVolume(raw float64, cd map[string]any) float64 {
	scale := intVal(cd["volScale"])
	unit := floatVal(cd["volUnit"])
	v := roundN(raw, scale)

	if unit > 0 {
		v = math.Floor(v/unit) * unit
	}

	minVol := floatVal(cd["minVol"])
	maxVol := floatVal(cd["maxVol"])
	if maxVol == 0 {
		maxVol = math.MaxFloat64
	}
	return math.Max(minVol, math.Min(v, maxVol))
}

func (m *MexcBypass) volumePayload(cd map[string]any, volume float64, leverage int, price, usdtValue float64, pt PriceType) map[string]any {
	return map[string]any{
		"usdt_value":   usdtValue,
		"volume":       volume,
		"leverage":     leverage,
		"price":        price,
		"min_volume":   cd["minVol"],
		"max_volume":   cd["maxVol"],
		"min_leverage": cd["minLeverage"],
		"max_leverage": cd["maxLeverage"],
		"price_type":   string(pt),
		"volume_scale": cd["volScale"],
		"volume_unit":  cd["volUnit"],
		"price_scale":  cd["priceScale"],
		"price_unit":   cd["priceUnit"],
	}
}

func roundN(v float64, decimals int) float64 {
	factor := math.Pow(10, float64(decimals))
	return math.Round(v*factor) / factor
}

func clampInt(v, min, max int) int {
	if min > 0 && v < min {
		return min
	}
	if max > 0 && v > max {
		return max
	}
	return v
}