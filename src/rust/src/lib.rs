use std::{
    collections::HashMap,
    sync::{
        atomic::{AtomicI64, AtomicU32, Ordering},
        Arc,
    },
    time::{SystemTime, UNIX_EPOCH},
};

use md5;
use reqwest::{
    header::{HeaderMap, HeaderName, HeaderValue, ACCEPT, CACHE_CONTROL, CONNECTION, CONTENT_TYPE},
    Client, ClientBuilder, Proxy,
};
use serde_json::{json, Map, Value};

fn md5_hex(input: &str) -> String {
    format!("{:x}", md5::compute(input.as_bytes()))
}


#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ApiType {
    General,
    Futures,
    Contract,
    Other,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HttpMethod {
    Get,
    Post,
    Put,
    Delete,
}

impl HttpMethod {
    pub fn has_body(self) -> bool {
        matches!(self, HttpMethod::Post | HttpMethod::Put)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PriceType {
    #[default]
    LastPrice,
    FairPrice,
    IndexPrice,
}

impl PriceType {
    pub fn from_str(s: &str) -> Self {
        match s {
            "fair_price"  => Self::FairPrice,
            "index_price" => Self::IndexPrice,
            _             => Self::LastPrice,
        }
    }

    pub fn ticker_field(self) -> &'static str {
        match self {
            Self::FairPrice  => "fairPrice",
            Self::IndexPrice => "indexPrice",
            Self::LastPrice  => "lastPrice",
        }
    }

    pub fn kline_path_segment(self) -> &'static str {
        match self {
            Self::IndexPrice => "index_price",
            Self::FairPrice  => "fair_price",
            Self::LastPrice  => "",
        }
    }

    pub fn value(self) -> &'static str {
        match self {
            Self::LastPrice  => "last_price",
            Self::FairPrice  => "fair_price",
            Self::IndexPrice => "index_price",
        }
    }
}

#[derive(Debug, Clone)]
pub struct ProxyConfig {
    pub url: String,
}

impl ProxyConfig {
    pub fn new(url: impl Into<String>) -> Self {
        Self { url: url.into() }
    }

    fn to_reqwest_proxy(&self) -> reqwest::Result<Proxy> {
        Proxy::all(&self.url)
    }
}

struct SignatureGenerator {
    api_key:      String,
    last_ms:      AtomicI64,
    ms_increment: AtomicU32,
}

impl SignatureGenerator {
    fn new(api_key: impl Into<String>) -> Self {
        Self {
            api_key:      api_key.into(),
            last_ms:      AtomicI64::new(0),
            ms_increment: AtomicU32::new(0),
        }
    }

    fn unique_ms(&self) -> i64 {
        let now = current_ms();
        let last = self.last_ms.load(Ordering::SeqCst);

        if now == last {
            let inc = self.ms_increment.fetch_add(1, Ordering::SeqCst) + 1;
            now + inc as i64
        } else {
            self.last_ms.store(now, Ordering::SeqCst);
            self.ms_increment.store(0, Ordering::SeqCst);
            now
        }
    }

    fn generate(&self, params: &Value, method: HttpMethod, force_time: Option<&str>) -> (String, String) {
        let time = force_time
            .map(|s| s.to_string())
            .unwrap_or_else(|| self.unique_ms().to_string());

        let g = {
            let raw = md5_hex(&format!("{}{}", self.api_key, time));
            raw[7..].to_string()
        };

        let sign = if method.has_body() {
            let body = if params.as_object().map(|m| m.is_empty()).unwrap_or(true) {
                String::new()
            } else {
                serde_json::to_string(params).unwrap_or_default()
            };
            md5_hex(&format!("{}{}{}", time, body, g))
        } else {
            let qs = value_to_query_string(params);
            md5_hex(&format!("{}{}{}", time, qs, g))
        };

        (time, sign)
    }

}

fn contract_detail_map() -> HashMap<&'static str, &'static str> {
    let mut m = HashMap::new();
    let pairs = [
        ("dn","displayName"),("dne","displayNameEn"),("pot","positionOpenType"),
        ("bc","baseCoin"),("qc","quoteCoin"),("bcn","baseCoinName"),("qcn","quoteCoinName"),
        ("ft","futureType"),("sc","settleCoin"),("cs","contractSize"),
        ("minL","minLeverage"),("maxL","maxLeverage"),("ccMaxL","countryConfigContractMaxLeverage"),
        ("ps","priceScale"),("vs","volScale"),("as","amountScale"),
        ("pu","priceUnit"),("vu","volUnit"),("minV","minVol"),("maxV","maxVol"),
        ("blpr","bidLimitPriceRate"),("alpr","askLimitPriceRate"),
        ("tfr","takerFeeRate"),("mfr","makerFeeRate"),
        ("mmr","maintenanceMarginRate"),("imr","initialMarginRate"),
        ("rbv","riskBaseVol"),("riv","riskIncrVol"),("rlss","riskLongShortSwitch"),
        ("rim","riskIncrMmr"),("rii","riskIncrImr"),("rll","riskLevelLimit"),
        ("pcv","priceCoefficientVariation"),("io","indexOrigin"),
        ("in","isNew"),("ih","isHot"),("ihd","isHidden"),("ip","isPromoted"),
        ("cp","conceptPlate"),("cpi","conceptPlateId"),("rlt","riskLimitType"),
        ("mno","maxNumOrders"),("moml","marketOrderMaxLevel"),
        ("moplr1","marketOrderPriceLimitRate1"),("moplr2","marketOrderPriceLimitRate2"),
        ("tp","triggerProtect"),("ae","appraisal"),("sac","showAppraisalCountdown"),
        ("ad","automaticDelivery"),("aa","apiAllowed"),("dsl","depthStepList"),
        ("lmv","limitMaxVol"),("tsd","threshold"),("bciu","baseCoinIconUrl"),
        ("bcid","baseCoinId"),("ct","createTime"),("ot","openingTime"),
        ("oco","openingCountdownOption"),("sbo","showBeforeOpen"),
        ("iml","isMaxLeverage"),("izfr","isZeroFeeRate"),("rlm","riskLimitMode"),
        ("rlcs","riskLimitCustom"),("izfs","isZeroFeeSymbol"),("liqfr","liquidationFeeRate"),
        ("frm","feeRateMode"),("levfrs","leverageFeeRates"),("tiefrs","tieredFeeRates"),
    ];
    for (k, v) in pairs { m.insert(k, v); }
    m
}

fn spot_symbols_map() -> HashMap<&'static str, &'static str> {
    let mut m = HashMap::new();
    let pairs = [
        ("mcd","marketCurrencyId"),("cd","coinId"),("vn","currency"),
        ("fn","currencyFullName"),("srt","sortOrder"),("sts","status"),
        ("tp","marketType"),("in","icon"),("ot","openingTime"),
        ("cp","categories"),("ci","categories_ids"),("ps","priceScale"),
        ("qs","quantityScale"),("cdm","contractDecimalMultiplier"),
        ("st","spotEnabled"),("dst","depositStatus"),("tt","tradingType"),
        ("ca","contractAddress"),("fne","currencyFullNameEn"),
    ];
    for (k, v) in pairs { m.insert(k, v); }
    m
}

fn rename_fields(data: Value, endpoint: &str) -> Value {
    if endpoint.contains("/contract/detailV2") {
        recursive_rename(data, &contract_detail_map())
    } else if endpoint.contains("/api/platform/spot/market-v2/web/symbolsV2") {
        recursive_rename(data, &spot_symbols_map())
    } else {
        data
    }
}

fn recursive_rename(value: Value, map: &HashMap<&str, &str>) -> Value {
    match value {
        Value::Object(obj) => {
            let mut new_obj = Map::new();
            for (k, v) in obj {
                let new_key = map.get(k.as_str()).copied().unwrap_or(k.as_str()).to_string();
                new_obj.insert(new_key, recursive_rename(v, map));
            }
            Value::Object(new_obj)
        }
        Value::Array(arr) => Value::Array(arr.into_iter().map(|v| recursive_rename(v, map)).collect()),
        other => other,
    }
}

fn current_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

fn current_secs() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

fn value_to_query_string(v: &Value) -> String {
    let Some(obj) = v.as_object() else { return String::new() };
    let mut pairs: Vec<String> = obj
        .iter()
        .filter(|(_, v)| !v.is_null())
        .map(|(k, v)| {
            let s = match v {
                Value::String(s) => s.clone(),
                other => other.to_string(),
            };
            format!("{}={}", percent_encode(k), percent_encode(&s))
        })
        .collect();
    pairs.sort();
    pairs.join("&")
}

fn percent_encode(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9'
            | b'-' | b'_' | b'.' | b'~' => out.push(b as char),
            b => out.push_str(&format!("%{:02X}", b)),
        }
    }
    out
}

fn week_range() -> (i64, i64) {
    let now_secs = current_secs();
    let days_since_monday = (now_secs / 86400 + 3) % 7;
    let monday = (now_secs - days_since_monday * 86400) / 86400 * 86400;
    let sunday = monday + 7 * 86400 * 1000 - 1;
    (monday * 1000, sunday)
}

fn normalize_volume(raw: f64, vol_scale: u32, vol_unit: f64, min_vol: f64, max_vol: f64) -> f64 {
    let factor = 10f64.powi(vol_scale as i32);
    let mut v = (raw * factor).round() / factor;
    if vol_unit > 0.0 {
        v = (v / vol_unit).floor() * vol_unit;
    }
    v.max(min_vol).min(max_vol)
}

fn build_error(code: i64, message: &str, is_testnet: bool) -> Value {
    json!({
        "success":    false,
        "code":       code,
        "message":    message,
        "timestamp":  current_ms(),
        "is_testnet": is_testnet,
    })
}

fn extract_origin(base_url: &str) -> &str {
    let after_scheme = base_url.find("://").map(|i| i + 3).unwrap_or(0);
    if let Some(pos) = base_url[after_scheme..].find('/') {
        &base_url[..after_scheme + pos]
    } else {
        base_url
    }
}

fn filter_nulls(v: &mut Value) {
    if let Value::Object(map) = v {
        map.retain(|_, val| !val.is_null());
        for val in map.values_mut() {
            filter_nulls(val);
        }
    }
}

fn build_client(proxy: Option<&ProxyConfig>) -> Result<Client, reqwest::Error> {
    let mut builder = ClientBuilder::new()
        .timeout(std::time::Duration::from_secs(30))  // Увеличил таймаут
        .connect_timeout(std::time::Duration::from_secs(10))
        .danger_accept_invalid_certs(true)
        .user_agent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36")
        .tcp_keepalive(std::time::Duration::from_secs(30))
        .pool_max_idle_per_host(10)
        .https_only(false);

    if let Some(cfg) = proxy {
        match cfg.to_reqwest_proxy() {
            Ok(proxy) => {
                builder = builder.proxy(proxy);
            },
            Err(e) => eprintln!("Ошибка прокси: {}", e),
        }
    }

    builder.build()
}


#[derive(Clone)]
pub struct MexcBypass {
    api_key:    String,
    is_testnet: bool,
    client:     Client,
    signer:     Arc<SignatureGenerator>,
    base_urls:  HashMap<String, String>,
}

impl MexcBypass {
    /// Create a new client.
    ///
    /// # Arguments
    /// * `api_key`   — web session token (ucenter token / cookie) or futures API key.
    /// * `is_testnet`— route futures requests to testnet.
    /// * `proxy_url` — optional `socks5://user:pass@host:port` or `http://host:port`.
    pub fn new(
        api_key:    impl Into<String>,
        is_testnet: bool,
        proxy_url:  Option<&str>,
    ) -> Result<Self, reqwest::Error> {
        let api_key = api_key.into();
        let proxy   = proxy_url.map(|u| ProxyConfig::new(u));
        let client  = build_client(proxy.as_ref())?;

        let futures_base = if is_testnet {
            "https://futures.testnet.mexc.com/api/v1".to_string()
        } else {
            "https://futures.mexc.com/api/v1".to_string()
        };

        let mut base_urls = HashMap::new();
        base_urls.insert("general".into(),  "https://www.mexc.com".into());
        base_urls.insert("futures".into(),  futures_base);
        base_urls.insert("contract".into(), "https://contract.mexc.com/api/v1".into());
        base_urls.insert("other".into(),    String::new());

        Ok(Self {
            signer: Arc::new(SignatureGenerator::new(&api_key)),
            api_key,
            is_testnet,
            client,
            base_urls,
        })
    }

    async fn request(
        &self,
        method:   HttpMethod,
        api_type: ApiType,
        endpoint: &str,
        mut params: Value,
    ) -> Value {
        filter_nulls(&mut params);

        let (timestamp, sign) = self.signer.generate(&params, method, None);
        let base_url          = self.resolve_base(api_type, endpoint);
        let origin            = extract_origin(&base_url).to_string();
        let url               = self.build_url(&base_url, endpoint, method, &params);

        let headers = self.build_headers(api_type, &timestamp, &sign, &origin);

        let req_builder = match method {
            HttpMethod::Get    => self.client.get(&url),
            HttpMethod::Post   => self.client.post(&url),
            HttpMethod::Put    => self.client.put(&url),
            HttpMethod::Delete => self.client.delete(&url),
        };

        let req_builder = req_builder.headers(headers);

        let req_builder = if method.has_body() || method == HttpMethod::Delete {
            req_builder.json(&params)
        } else {
            req_builder
        };

        let result = req_builder.send().await;

        match result {
            Err(e) => build_error(0, &format!("Request error: {e}"), self.is_testnet),
            Ok(resp) => {
                let status = resp.status().as_u16() as i64;
                match resp.text().await {
                    Err(e) => build_error(status, &format!("Read error: {e}"), self.is_testnet),
                    Ok(raw) if raw.is_empty() => {
                        build_error(status, "Empty response", self.is_testnet)
                    }
                    Ok(raw) => self.handle_response(&raw, status, endpoint),
                }
            }
        }
    }

    fn handle_response(&self, raw: &str, status: i64, endpoint: &str) -> Value {
        match serde_json::from_str::<Value>(raw) {
            Err(e) => {
                let preview = &raw[..raw.len().min(200)];
                build_error(
                    status,
                    &format!("Invalid JSON: {} | raw: {}", e, preview),
                    self.is_testnet,
                )
            }
            Ok(mut decoded) => {
                if let Some(data) = decoded.get("data").cloned() {
                    if data.is_array() || data.is_object() {
                        decoded["data"] = rename_fields(data, endpoint);
                    }
                }
                decoded["is_testnet"] = json!(self.is_testnet);
                decoded
            }
        }
    }

    fn resolve_base(&self, api_type: ApiType, endpoint: &str) -> String {
        if api_type == ApiType::Other {
            let after = endpoint.find("://").map(|i| i + 3).unwrap_or(0);
            if let Some(pos) = endpoint[after..].find('/') {
                return endpoint[..after + pos].to_string();
            }
            return endpoint.to_string();
        }
        let key = match api_type {
            ApiType::General  => "general",
            ApiType::Futures  => "futures",
            ApiType::Contract => "contract",
            ApiType::Other    => unreachable!(),
        };
        self.base_urls[key].clone()
    }

    fn build_url(&self, base: &str, endpoint: &str, method: HttpMethod, params: &Value) -> String {
        let full = if base.is_empty() {
            endpoint.to_string()
        } else {
            format!("{}{}", base, endpoint)
        };

        if !method.has_body() {
            if let Some(obj) = params.as_object() {
                if !obj.is_empty() {
                    let qs = value_to_query_string(params);
                    return format!("{}?{}", full, qs);
                }
            }
        }
        full
    }

    fn build_headers(
        &self,
        api_type:  ApiType,
        timestamp: &str,
        sign:      &str,
        origin:    &str,
    ) -> HeaderMap {
        let mut map = HeaderMap::new();
        map.insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
        map.insert(ACCEPT,       HeaderValue::from_static("*/*"));
        map.insert(
            reqwest::header::USER_AGENT,
            HeaderValue::from_static("Mozilla/5.0 (compatible; MEXC-API/1.0)"),
        );
        map.insert(CONNECTION,    HeaderValue::from_static("keep-alive"));
        map.insert(CACHE_CONTROL, HeaderValue::from_static("no-cache"));
        map.insert(
            HeaderName::from_static("origin"),
            HeaderValue::from_str(origin).unwrap_or(HeaderValue::from_static("")),
        );

        if api_type == ApiType::General {
            let cookie = format!("u_id={}; uc_token={};", self.api_key, self.api_key);
            map.insert(
                HeaderName::from_static("cookie"),
                HeaderValue::from_str(&cookie).unwrap_or(HeaderValue::from_static("")),
            );
            map.insert(
                HeaderName::from_static("ucenter-token"),
                HeaderValue::from_str(&self.api_key).unwrap_or(HeaderValue::from_static("")),
            );
        } else {
            map.insert(
                HeaderName::from_static("authorization"),
                HeaderValue::from_str(&self.api_key).unwrap_or(HeaderValue::from_static("")),
            );
            map.insert(
                HeaderName::from_static("x-mxc-nonce"),
                HeaderValue::from_str(timestamp).unwrap_or(HeaderValue::from_static("")),
            );
            map.insert(
                HeaderName::from_static("x-mxc-sign"),
                HeaderValue::from_str(sign).unwrap_or(HeaderValue::from_static("")),
            );
        }
        map
    }

    /// Execute multiple async closures in parallel and return a map of results.
    ///
    /// let result = client.batch(vec![
    ///     ("ticker",   Box::pin(client.get_futures_tickers(json!({"symbol":"BTC_USDT"})))),
    ///     ("contract", Box::pin(client.get_futures_contracts(json!({"symbol":"BTC_USDT"})))),
    /// ]).await;

    pub async fn batch(
        &self,
        tasks: Vec<(&'static str, std::pin::Pin<Box<dyn std::future::Future<Output = Value> + Send>>)>,
    ) -> HashMap<String, Value> {
        let mut handles = Vec::with_capacity(tasks.len());
        for (key, fut) in tasks {
            let handle = tokio::spawn(async move { fut.await });
            handles.push((key, handle));
        }
        let mut results = HashMap::new();
        for (key, handle) in handles {
            let val = handle.await.unwrap_or_else(|e| json!({"success":false,"message":e.to_string()}));
            results.insert(key.to_string(), val);
        }
        results
    }

    /// Execute the same set of requests across multiple accounts in parallel.
    ///
    /// let results = MexcBypass::batch_accounts(
    ///     vec![
    ///         ("acc1", MexcBypass::new("key1", false, None)?),
    ///         ("acc2", MexcBypass::new("key2", true,  Some("socks5://user:pass@host:1080"))?),
    ///     ],
    ///     |client| async move {
    ///         let mut m = HashMap::new();
    ///         m.insert("positions", client.get_futures_open_positions(Value::Null).await);
    ///         m.insert("balance",   client.get_futures_assets(json!({"currency":"USDT"})).await);
    ///         m
    ///     },
    /// ).await;

    pub async fn get_server_time(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::Contract, "/contract/ping", json!({})).await
    }

    pub async fn ping(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::General, "/api/common/ping", json!({})).await
    }

    pub async fn validation(&self) -> Value {
        self.request(HttpMethod::Post, ApiType::General, "/ucenter/api/login/validation", json!({})).await
    }

    pub async fn get_ws_token(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::General, "/ucenter/api/ws_token", json!({})).await
    }

    pub async fn get_customer_info(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::General, "/ucenter/api/customer_info", json!({})).await
    }

    pub async fn get_user_info(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::General, "/ucenter/api/user_info", json!({})).await
    }

    pub async fn get_latest_deposits(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::General, "/api/platform/deposit/v3/latest", json!({})).await
    }

    pub async fn get_deposit_addresses(&self, params: Value) -> Value {
        if let Some(currency) = params.get("currency").and_then(|v| v.as_str()) {
            let ep = format!("/api/platform/asset/api/asset/spot/currency/v3?currency={}", currency);
            self.request(HttpMethod::Get, ApiType::General, &ep, json!({})).await
        } else {
            let vcoin_id = params.get("vcoin_id").and_then(|v| v.as_str()).unwrap_or_default();
            let ep = format!("/api/platform/asset/api/deposit/address/list?vcoinId={}", vcoin_id);
            self.request(HttpMethod::Get, ApiType::General, &ep, json!({})).await
        }
    }

    pub async fn get_secure_info(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::General, "/ucenter/api/secure/check", json!({})).await
    }

    pub async fn get_origin_info(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::General, "/ucenter/api/origin_info", json!({})).await
    }

    pub async fn logout(&self) -> Value {
        self.request(HttpMethod::Post, ApiType::General, "/ucenter/api/logout", json!({})).await
    }

    pub async fn get_assets_overview(&self, params: Value) -> Value {
        let convert = params.get("convert").and_then(|v| v.as_bool()).unwrap_or(false);
        let endpoint = if convert {
            "/api/platform/asset/api/asset/overview/convert/v2"
        } else {
            "/api/platform/asset/api/asset/overview/v2"
        };
        self.request(HttpMethod::Get, ApiType::General, endpoint, json!({})).await
    }

    pub async fn get_referrals_list(&self, params: Value) -> Value {
        let (week_start, week_end) = week_range();
        self.request(HttpMethod::Get, ApiType::General, "/api/assetbussiness/invite/invites", json!({
            "startTime": params.get("start_time").and_then(|v| v.as_i64()).unwrap_or(week_start),
            "endTime":   params.get("end_time").and_then(|v| v.as_i64()).unwrap_or(week_end),
            "page":      params.get("page_num").and_then(|v| v.as_u64()).unwrap_or(1),
            "pageSize":  params.get("page_size").and_then(|v| v.as_u64()).unwrap_or(10),
        })).await
    }

    pub async fn get_market_symbols(&self, params: Value) -> Value {
        let response = self
            .request(HttpMethod::Get, ApiType::General, "/api/platform/spot/market-v2/web/symbolsV2", json!({}))
            .await;

        if params.as_object().map(|m| m.is_empty()).unwrap_or(true) {
            return response;
        }

        let (base_coin, quote_coin) = parse_symbol(&params);
        if base_coin.is_empty() {
            return response;
        }

        let symbols_map = match response.get("data").and_then(|d| d.get("symbols")) {
            Some(m) => m.clone(),
            None    => return self.mk_error(400, "No symbols data found"),
        };

        let quote = if quote_coin.is_empty() { "USDT" } else { &quote_coin };
        let list = match symbols_map.get(quote).and_then(|v| v.as_array()) {
            Some(l) => l.clone(),
            None    => return self.mk_error(400, &format!("No {} symbols found", quote)),
        };

        let search = base_coin.to_uppercase();
        let matches: Vec<Value> = list
            .into_iter()
            .filter(|item| {
                item.get("currency")
                    .and_then(|v| v.as_str())
                    .map(|s| s.to_uppercase() == search)
                    .unwrap_or(false)
            })
            .collect();

        if matches.is_empty() {
            return self.mk_error(404, "No matching tokens found");
        }

        json!({
            "success":   true,
            "count":     matches.len(),
            "data":      matches,
            "timestamp": current_secs(),
        })
    }

    pub async fn get_spot_order_book(&self, params: Value) -> Value {
        let symbol = params.get("symbol").and_then(|v| v.as_str()).unwrap_or_default().to_string();
        let symbols = self.get_market_symbols(json!({"symbol": symbol})).await;
        if symbols.get("success").and_then(|v| v.as_bool()) != Some(true) {
            return symbols;
        }
        let symbol_id = symbols["data"][0]["id"].clone();
        self.request(HttpMethod::Get, ApiType::General, "/api/platform/spot/market-v2/web/depth/v2", json!({
            "symbolId": symbol_id,
            "decimal":  params.get("decimal").cloned().unwrap_or(json!("0.0000001")),
            "count":    params.get("count").cloned().unwrap_or(json!(100)),
        })).await
    }

    pub async fn create_spot_order(&self, params: Value) -> Value {
        let order_type = params.get("type").and_then(|v| v.as_str()).unwrap_or_default().to_string();
        let symbol     = params.get("symbol").and_then(|v| v.as_str()).unwrap_or_default().to_string();
        let path = if order_type == "LIMIT_ORDER" {
            "/api/platform/spot/order/place"
        } else {
            "/api/platform/spot/v4/order/place"
        };
        let symbols = self.get_market_symbols(json!({"symbol": symbol})).await;
        if symbols.get("success").and_then(|v| v.as_bool()) != Some(true) {
            return symbols;
        }
        self.request(HttpMethod::Post, ApiType::General, path, json!({
            "orderType":        order_type,
            "tradeType":        params.get("side"),
            "price":            params.get("price"),
            "amount":           params.get("amount"),
            "quantity":         params.get("quantity"),
            "marketCurrencyId": symbols["data"][0]["marketCurrencyId"].clone(),
            "currencyId":       symbols["data"][0]["coinId"].clone(),
            "orderSource":      "WEB",
            "ts":               current_ms(),
        })).await
    }

    pub async fn cancel_spot_order(&self, params: Value) -> Value {
        self.request(HttpMethod::Delete, ApiType::General, "/api/platform/spot/order/cancel/v2", json!({
            "orderId": params.get("order_id"),
        })).await
    }

    pub async fn get_futures_fee_rate(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/account/contract/fee_rate", json!({})).await
    }

    pub async fn get_futures_zero_fee_rate(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/account/contract/zero_fee_rate", json!({})).await
    }

    pub async fn get_futures_today_pnl(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/account/asset/analysis/today_pnl", json!({})).await
    }

    pub async fn get_futures_analysis(&self, params: Value) -> Value {
        let now        = current_ms();
        let end_time   = params.get("end_time").and_then(|v| v.as_i64()).unwrap_or(now).min(now);
        let start_time = params.get("start_time").and_then(|v| v.as_i64())
            .unwrap_or(end_time - 7 * 86_400 * 1000);
        let start_time = if start_time < end_time { start_time } else { end_time - 1000 };

        self.request(HttpMethod::Post, ApiType::Futures, "/private/account/asset/analysis/v3", json!({
            "currency":               params.get("currency").cloned().unwrap_or(json!("USDT")),
            "symbol":                 params.get("symbol"),
            "include_unrealised_pnl": params.get("include_unrealised_pnl").cloned().unwrap_or(json!(0)),
            "reverse":                params.get("reverse").cloned().unwrap_or(json!(0)),
            "startTime":              start_time,
            "endTime":                end_time,
        })).await
    }

    pub async fn get_futures_assets(&self, params: Value) -> Value {
        let endpoint = if let Some(currency) = params.get("currency").and_then(|v| v.as_str()) {
            format!("/private/account/asset/{}", currency)
        } else {
            "/private/account/assets".to_string()
        };
        self.request(HttpMethod::Get, ApiType::Futures, &endpoint, json!({})).await
    }

    pub async fn get_futures_asset_transfer_records(&self, params: Value) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/account/transfer_record", json!({
            "currency":  params.get("currency"),
            "state":     params.get("state"),
            "type":      params.get("type"),
            "page_num":  params.get("page_num").cloned().unwrap_or(json!(1)),
            "page_size": params.get("page_size").cloned().unwrap_or(json!(20)),
        })).await
    }

    pub async fn get_futures_risk_limits(&self, params: Value) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/account/risk_limit", json!({
            "symbol": params.get("symbol"),
        })).await
    }

    pub async fn get_futures_funding_rate(&self, params: Value) -> Value {
        let symbol = params.get("symbol").and_then(|v| v.as_str()).unwrap_or_default();
        let ep = format!("/contract/funding_rate/{}", symbol);
        self.request(HttpMethod::Get, ApiType::Contract, &ep, json!({})).await
    }

    pub async fn get_futures_contracts(&self, params: Value) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/contract/detailV2", json!({
            "symbol": params.get("symbol"),
        })).await
    }

    pub async fn get_futures_contract_index_price(&self, params: Value) -> Value {
        let symbol = params.get("symbol").and_then(|v| v.as_str()).unwrap_or_default();
        let ep = format!("/contract/index_price/{}", symbol);
        self.request(HttpMethod::Get, ApiType::Contract, &ep, json!({})).await
    }

    pub async fn get_futures_contract_fair_price(&self, params: Value) -> Value {
        let symbol = params.get("symbol").and_then(|v| v.as_str()).unwrap_or_default();
        let ep = format!("/contract/fair_price/{}", symbol);
        self.request(HttpMethod::Get, ApiType::Contract, &ep, json!({})).await
    }

    pub async fn get_futures_contract_kline_data(&self, params: Value) -> Value {
        let pt      = PriceType::from_str(params.get("price_type").and_then(|v| v.as_str()).unwrap_or(""));
        let segment = pt.kline_path_segment();
        let symbol  = params.get("symbol").and_then(|v| v.as_str()).unwrap_or_default();
        let path = if segment.is_empty() {
            format!("/contract/kline/{}", symbol)
        } else {
            format!("/contract/kline/{}/{}", segment, symbol)
        };
        self.request(HttpMethod::Get, ApiType::Contract, &path, json!({
            "interval": params.get("interval").cloned().unwrap_or(json!("Min1")),
            "start":    params.get("start_time"),
            "end":      params.get("end_time"),
        })).await
    }

    pub async fn get_futures_tickers(&self, params: Value) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/contract/ticker", json!({
            "symbol": params.get("symbol"),
        })).await
    }

    pub async fn get_futures_open_positions(&self, params: Value) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/position/open_positions", json!({
            "symbol":     params.get("symbol"),
            "positionId": params.get("position_id"),
        })).await
    }

    pub async fn get_futures_positions_history(&self, params: Value) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/position/list/history_positions", json!({
            "symbol":    params.get("symbol"),
            "type":      params.get("type"),
            "page_num":  params.get("page_num").cloned().unwrap_or(json!(1)),
            "page_size": params.get("page_size").cloned().unwrap_or(json!(20)),
        })).await
    }

    pub async fn close_all_futures_positions(&self) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/position/close_all", json!({})).await
    }

    pub async fn reverse_futures_position(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/position/reverse", json!({
            "positionId": params["position_id"],
            "symbol":     params["symbol"],
            "vol":        params["vol"],
        })).await
    }

    pub async fn get_futures_position_mode(&self) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/position/position_mode", json!({})).await
    }

    pub async fn change_futures_position_mode(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/position/change_position_mode", json!({
            "positionMode": params["position_mode"],
        })).await
    }

    pub async fn get_futures_leverage(&self, params: Value) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/position/leverage", json!({
            "symbol": params["symbol"],
        })).await
    }

    pub async fn change_futures_position_margin(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/position/change_margin", json!({
            "positionId": params["position_id"],
            "amount":     params["amount"],
            "type":       params["type"],
        })).await
    }

    pub async fn change_futures_position_leverage(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/position/change_leverage", json!({
            "positionId":   params["position_id"],
            "leverage":     params["leverage"],
            "openType":     params.get("open_type"),
            "symbol":       params.get("symbol"),
            "positionType": params.get("position_type"),
        })).await
    }

    pub async fn get_futures_orders_deals(&self, params: Value) -> Value {
        let (w_start, w_end) = week_range();
        self.request(HttpMethod::Get, ApiType::Futures, "/private/order/list/order_deals", json!({
            "symbol":     params["symbol"],
            "start_time": params.get("start_time").cloned().unwrap_or(json!(w_start)),
            "end_time":   params.get("end_time").cloned().unwrap_or(json!(w_end)),
            "page_num":   params.get("page_num").cloned().unwrap_or(json!(1)),
            "page_size":  params.get("page_size").cloned().unwrap_or(json!(20)),
        })).await
    }

    pub async fn get_futures_pending_orders(&self, params: Value) -> Value {
        let endpoint = if let Some(symbol) = params.get("symbol").and_then(|v| v.as_str()) {
            format!("/private/order/list/open_orders/{}", symbol)
        } else {
            "/private/order/list/open_orders/".to_string()
        };
        self.request(HttpMethod::Get, ApiType::Futures, &endpoint, json!({
            "page_num":  params.get("page_num").cloned().unwrap_or(json!(1)),
            "page_size": params.get("page_size").cloned().unwrap_or(json!(20)),
        })).await
    }

    pub async fn get_futures_orders_history(&self, params: Value) -> Value {
        let (w_start, w_end) = week_range();
        self.request(HttpMethod::Get, ApiType::Futures, "/private/order/list/history_orders", json!({
            "symbol":     params.get("symbol"),
            "states":     params.get("states"),
            "category":   params.get("category"),
            "side":       params.get("side"),
            "start_time": params.get("start_time").cloned().unwrap_or(json!(w_start)),
            "end_time":   params.get("end_time").cloned().unwrap_or(json!(w_end)),
            "page_num":   params.get("page_num").cloned().unwrap_or(json!(1)),
            "page_size":  params.get("page_size").cloned().unwrap_or(json!(20)),
        })).await
    }

    pub async fn get_futures_open_limit_orders(&self, params: Value) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/order/list/open_orders", json!({
            "page_size": params.get("page_size").cloned().unwrap_or(json!(200)),
        })).await
    }

    pub async fn get_futures_open_stop_orders(&self, params: Value) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/stoporder/open_orders", json!({
            "page_size": params.get("page_size").cloned().unwrap_or(json!(200)),
        })).await
    }

    pub async fn get_futures_open_orders(&self, params: Value) -> Value {
        let page_size = params.get("page_size").cloned().unwrap_or(json!(200));
        let limit_fut = self.get_futures_open_limit_orders(json!({"page_size": page_size}));
        let stop_fut  = self.get_futures_open_stop_orders(json!({"page_size": page_size}));
        let (limit_res, stop_res) = tokio::join!(limit_fut, stop_fut);

        let limit_data = if limit_res.get("success").and_then(|v| v.as_bool()) == Some(true) {
            limit_res.get("data").and_then(|v| v.as_array()).cloned().unwrap_or_default()
        } else { vec![] };

        let stop_data = if stop_res.get("success").and_then(|v| v.as_bool()) == Some(true) {
            stop_res.get("data").and_then(|v| v.as_array()).cloned().unwrap_or_default()
        } else { vec![] };

        let mut merged = limit_data;
        merged.extend(stop_data);
        json!({ "success": true, "code": 0, "data": merged })
    }

    pub async fn get_futures_closed_orders(&self, params: Value) -> Value {
        self.request(HttpMethod::Get, ApiType::Futures, "/private/order/close_orders", json!({
            "symbol":    params.get("symbol"),
            "category":  params.get("category"),
            "page_num":  params.get("page_num").cloned().unwrap_or(json!(1)),
            "page_size": params.get("page_size").cloned().unwrap_or(json!(20)),
        })).await
    }

    pub async fn get_futures_orders_by_id(&self, params: Value) -> Value {
        let ids_str = params.get("ids").and_then(|v| v.as_str()).unwrap_or_default();
        let ids: Vec<&str> = ids_str.split(',').map(|s| s.trim()).collect();
        if ids.len() > 1 {
            self.request(HttpMethod::Get, ApiType::Futures, "/private/order/batch_query", json!({
                "order_ids": ids_str,
            })).await
        } else {
            let ep = format!("/private/order/get/{}", ids_str.trim());
            self.request(HttpMethod::Get, ApiType::Futures, &ep, json!({})).await
        }
    }

    pub async fn create_futures_order(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/order/create", json!({
            "symbol":          params["symbol"],
            "price":           params.get("price"),
            "type":            params["type"],
            "openType":        params.get("open_type"),
            "positionMode":    params.get("position_mode"),
            "side":            params["side"],
            "vol":             params["vol"],
            "leverage":        params.get("leverage"),
            "positionId":      params.get("position_id"),
            "externalOid":     params.get("external_id"),
            "takeProfitPrice": params.get("take_profit_price"),
            "profitTrend":     params.get("take_profit_trend").cloned().unwrap_or(json!(1)),
            "stopLossPrice":   params.get("stop_loss_price"),
            "lossTrend":       params.get("stop_loss_trend").cloned().unwrap_or(json!(1)),
            "priceProtect":    params.get("price_protect").cloned().unwrap_or(json!(0)),
            "reduceOnly":      params.get("reduce_only").cloned().unwrap_or(json!(false)),
            "marketCeiling":   params.get("market_ceiling").cloned().unwrap_or(json!(false)),
            "flashClose":      params.get("flash_close"),
            "bboTypeNum":      params.get("bbo_type"),
        })).await
    }

    pub async fn cancel_futures_orders(&self, params: Value) -> Value {
        let ids: Vec<String> = match params.get("ids") {
            Some(Value::Array(arr)) => arr.iter()
                .filter_map(|v| v.as_str().map(|s| s.trim().to_string()))
                .collect(),
            Some(Value::String(s)) => s.split(',').map(|v| v.trim().to_string()).collect(),
            _ => vec![],
        };
        self.request(HttpMethod::Post, ApiType::Futures, "/private/order/cancel", json!(ids)).await
    }

    pub async fn cancel_futures_order_with_external_id(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/order/cancel_with_external", json!({
            "symbol":      params["symbol"],
            "externalOid": params["external_id"],
        })).await
    }

    pub async fn cancel_all_futures_orders(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/order/cancel_all", json!({
            "symbol": params.get("symbol"),
        })).await
    }

    pub async fn change_futures_limit_order_price(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "private/order/change_limit_order", json!({
            "orderId": params["order_id"],
            "price":   params["price"],
            "vol":     params["vol"],
        })).await
    }

    pub async fn chase_futures_order(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/order/chase_limit_order", json!({
            "orderId": params["order_id"],
        })).await
    }

    pub async fn create_futures_chase_order(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/order/chase_limit/place", json!({
            "chaseType":        params["chase_type"],
            "distanceType":     params.get("distance_type"),
            "distanceValue":    params.get("distance_value"),
            "maxDistanceType":  params.get("max_distance_type"),
            "maxDistanceValue": params.get("max_distance_value"),
            "leverage":         params["leverage"],
            "openType":         params["open_type"],
            "side":             params["side"],
            "symbol":           params["symbol"],
            "vol":              params["vol"],
        })).await
    }

    pub async fn create_futures_stop_order(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/stoporder/place/v2", json!({
            "positionId":           params["position_id"],
            "volType":              params.get("vol_type").cloned().unwrap_or(json!(2)),
            "vol":                  params.get("vol"),
            "takeProfitType":       params.get("take_profit_type"),
            "takeProfitOrderPrice": params.get("take_profit_order_price"),
            "takeProfitPrice":      params.get("take_profit_price"),
            "takeProfitReverse":    params.get("take_profit_reverse").cloned().unwrap_or(json!(2)),
            "profitTrend":          params.get("take_profit_trend").cloned().unwrap_or(json!(1)),
            "takeProfitVol":        params.get("take_profit_volume"),
            "stopLossType":         params.get("stop_loss_type"),
            "stopLossOrderPrice":   params.get("stop_loss_order_price"),
            "stopLossPrice":        params.get("stop_loss_price"),
            "stopLossReverse":      params.get("stop_loss_reverse").cloned().unwrap_or(json!(2)),
            "lossTrend":            params.get("stop_loss_trend").cloned().unwrap_or(json!(1)),
            "profitLossVolType":    params.get("profit_loss_vol_type").cloned().unwrap_or(json!("SAME")),
            "priceProtect":         params.get("price_protect").cloned().unwrap_or(json!(0)),
        })).await
    }

    pub async fn get_futures_stop_limit_orders(&self, params: Value) -> Value {
        let (w_start, w_end) = week_range();
        self.request(HttpMethod::Get, ApiType::Futures, "/private/stoporder/list/orders", json!({
            "symbol":      params.get("symbol"),
            "is_finished": params.get("is_finished"),
            "start_time":  params.get("start_time").cloned().unwrap_or(json!(w_start)),
            "end_time":    params.get("end_time").cloned().unwrap_or(json!(w_end)),
            "page_num":    params.get("page_num").cloned().unwrap_or(json!(1)),
            "page_size":   params.get("page_size").cloned().unwrap_or(json!(20)),
        })).await
    }

    pub async fn cancel_stop_limit_orders(&self, params: Value) -> Value {
        let ids: Vec<u64> = match params.get("ids") {
            Some(Value::Array(arr)) => arr.iter()
                .filter_map(|v| v.as_u64().or_else(|| v.as_str()?.trim().parse().ok()))
                .collect(),
            Some(Value::String(s)) => s.split(',')
                .filter_map(|v| v.trim().parse().ok())
                .collect(),
            _ => vec![],
        };
        let payload: Vec<Value> = ids.into_iter()
            .map(|id| json!({"stopPlanOrderId": id}))
            .collect();
        self.request(HttpMethod::Post, ApiType::Futures, "/private/stoporder/cancel", json!(payload)).await
    }

    pub async fn cancel_all_futures_stop_limit_orders(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/stoporder/cancel_all", json!({
            "positionId": params.get("position_id"),
            "symbol":     params.get("symbol"),
        })).await
    }

    pub async fn change_futures_order_stop_limit_price(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/stoporder/change_price", json!({
            "orderId":         params["order_id"],
            "takeProfitPrice": params.get("take_profit_price"),
            "stopLossPrice":   params.get("stop_loss_price"),
        })).await
    }

    pub async fn change_futures_plan_order_stop_limit_price(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/stoporder/change_plan_price", json!({
            "stopPlanOrderId": params["order_id"],
            "takeProfitPrice": params.get("take_profit_price"),
            "stopLossPrice":   params.get("stop_loss_price"),
        })).await
    }

    pub async fn change_futures_order_targets(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/stoporder/change_plan_order", json!({
            "orderId":          params["real_order_id"],
            "profitTrend":      params.get("take_profit_trend"),
            "takeProfitPrice":  params.get("take_profit_price"),
            "takeProfitVolume": params.get("take_profit_volume"),
            "lossTrend":        params.get("stop_loss_trend"),
            "stopLossPrice":    params.get("stop_loss_price"),
            "stopLossVolume":   params.get("stop_loss_volume"),
        })).await
    }

    pub async fn create_futures_trigger_order(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/planorder/place", json!({
            "symbol":       params["symbol"],
            "price":        params.get("price"),
            "vol":          params["vol"],
            "leverage":     params.get("leverage"),
            "side":         params["side"],
            "openType":     params["open_type"],
            "triggerPrice": params["trigger_price"],
            "triggerType":  params["trigger_type"],
            "executeCycle": params["execute_cycle"],
            "orderType":    params["order_type"],
            "trend":        params["trend"],
        })).await
    }

    pub async fn get_futures_trigger_orders(&self, params: Value) -> Value {
        let (w_start, w_end) = week_range();
        self.request(HttpMethod::Get, ApiType::Futures, "/private/planorder/list/orders", json!({
            "symbol":     params.get("symbol"),
            "states":     params.get("states"),
            "start_time": params.get("start_time").cloned().unwrap_or(json!(w_start)),
            "end_time":   params.get("end_time").cloned().unwrap_or(json!(w_end)),
            "page_num":   params.get("page_num").cloned().unwrap_or(json!(1)),
            "page_size":  params.get("page_size").cloned().unwrap_or(json!(20)),
        })).await
    }

    pub async fn cancel_futures_trigger_orders(&self, params: Value) -> Value {
        let ids = params["ids"].clone();
        self.request(HttpMethod::Post, ApiType::Futures, "/private/planorder/cancel", ids).await
    }

    pub async fn cancel_all_futures_trigger_orders(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/planorder/cancel_all", json!({
            "symbol": params.get("symbol"),
        })).await
    }

    pub async fn create_futures_trailing_order(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/trackorder/place", json!({
            "symbol":       params["symbol"],
            "leverage":     params["leverage"],
            "side":         params["side"],
            "vol":          params["vol"],
            "openType":     params["open_type"],
            "trend":        params["trend"],
            "activePrice":  params["active_price"],
            "backType":     params["back_type"],
            "backValue":    params["back_value"],
            "positionMode": params.get("position_mode").cloned().unwrap_or(json!(0)),
            "reduceOnly":   params.get("reduce_only"),
        })).await
    }

    pub async fn cancel_futures_trailing_order(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/trackorder/cancel", json!({
            "symbol":       params.get("symbol"),
            "trackOrderId": params.get("order_id"),
        })).await
    }

    pub async fn change_futures_trailing_order(&self, params: Value) -> Value {
        self.request(HttpMethod::Post, ApiType::Futures, "/private/trackorder/change_order", json!({
            "symbol":       params["symbol"],
            "trackOrderId": params["order_id"],
            "trend":        params["trend"],
            "activePrice":  params["active_price"],
            "backType":     params["back_type"],
            "backValue":    params["back_value"],
            "vol":          params["vol"],
        })).await
    }

    pub async fn calculate_futures_position_pnl(&self, params: Value) -> Value {
        let symbol = params.get("symbol").and_then(|v| v.as_str()).unwrap_or_default().to_string();
        let contract_fut = self.get_futures_contracts(json!({"symbol": symbol}));
        let ticker_fut   = self.get_futures_tickers(json!({"symbol": symbol}));
        let (contract_res, ticker_res) = tokio::join!(contract_fut, ticker_fut);

        let contract = match contract_res.get("data").and_then(|d| d.get(0)) {
            Some(c) => c.clone(),
            None    => return self.mk_error(-1, "Unable to calculate PnL: missing contract data"),
        };
        let ticker = match ticker_res.get("data") {
            Some(t) => t.clone(),
            None    => return self.mk_error(-1, "Unable to calculate PnL: missing ticker data"),
        };

        let pt         = PriceType::from_str(params.get("price_type").and_then(|v| v.as_str()).unwrap_or(""));
        let price      = ticker.get(pt.ticker_field()).and_then(|v| v.as_f64()).unwrap_or(0.0);
        let entry      = params.get("entry_price").and_then(|v| v.as_f64()).unwrap_or(0.0);
        let volume     = params.get("volume").and_then(|v| v.as_f64()).unwrap_or(0.0);
        let leverage   = params.get("leverage").and_then(|v| v.as_f64()).unwrap_or(1.0).max(1.0);
        let cs         = contract.get("contractSize").and_then(|v| v.as_f64()).unwrap_or(0.0);
        let is_long    = params.get("side").and_then(|v| v.as_i64()) == Some(1);

        if cs == 0.0 {
            return self.mk_error(-1, "Invalid contract size");
        }

        let pnl = if is_long {
            (price - entry) * volume * cs
        } else {
            (entry - price) * volume * cs
        };
        let initial_margin = entry * volume * cs / leverage;
        let pnl_percent    = if initial_margin > 0.0 { pnl / initial_margin * 100.0 } else { 0.0 };

        json!({
            "success": true,
            "code": 0,
            "data": {
                "pnl":           round4(pnl),
                "pnl_percent":   round2(pnl_percent),
                "volume":        volume,
                "entry_price":   entry,
                "current_price": price,
                "price_type":    pt.value(),
            }
        })
    }

    pub async fn calculate_futures_volume(&self, params: Value) -> Value {
        for req in &["symbol", "amount", "leverage"] {
            if params.get(req).is_none() {
                return self.mk_error(404, &format!("Missing required parameter: {}", req));
            }
        }

        let symbol = params["symbol"].as_str().unwrap_or_default().to_string();
        let contract_fut = self.get_futures_contracts(json!({"symbol": symbol}));
        let ticker_fut   = self.get_futures_tickers(json!({"symbol": symbol}));
        let (contract_res, ticker_res) = tokio::join!(contract_fut, ticker_fut);

        let (cd, ticker, pt) = match self.extract_market_data(&contract_res, &ticker_res, &params) {
            Ok(v)  => v,
            Err(e) => return e,
        };

        let amount   = params["amount"].as_f64().unwrap_or(0.0);
        let leverage = params["leverage"].as_f64().unwrap_or(1.0);
        if amount   <= 0.0 { return self.mk_error(400, "Amount must be positive"); }
        if leverage <= 0.0 { return self.mk_error(400, "Leverage must be positive"); }

        let price    = ticker;
        let lev_clamped = leverage.max(cd.min_leverage).min(cd.max_leverage) as u32;
        let raw_vol  = (amount * leverage) / (price * cd.contract_size);
        let volume   = normalize_volume(raw_vol, cd.vol_scale, cd.vol_unit, cd.min_vol, cd.max_vol);
        let usdt     = round_scale(volume * price * cd.contract_size / lev_clamped as f64, cd.price_scale);

        json!({
            "success": true,
            "code": 0,
            "data": self.volume_payload(&cd, volume, lev_clamped, price, usdt, pt),
        })
    }

    pub async fn calculate_futures_volume_from_base_amount(&self, params: Value) -> Value {
        for req in &["symbol", "amount", "leverage"] {
            if params.get(req).is_none() {
                return self.mk_error(404, &format!("Missing required parameter: {}", req));
            }
        }

        let symbol = params["symbol"].as_str().unwrap_or_default().to_string();
        let contract_fut = self.get_futures_contracts(json!({"symbol": symbol}));
        let ticker_fut   = self.get_futures_tickers(json!({"symbol": symbol}));
        let (contract_res, ticker_res) = tokio::join!(contract_fut, ticker_fut);

        let (cd, ticker, pt) = match self.extract_market_data(&contract_res, &ticker_res, &params) {
            Ok(v)  => v,
            Err(e) => return e,
        };

        let amount   = params["amount"].as_f64().unwrap_or(0.0);
        let leverage = params["leverage"].as_f64().unwrap_or(1.0);
        if amount   <= 0.0 { return self.mk_error(400, "Base amount must be positive"); }
        if leverage <= 0.0 { return self.mk_error(400, "Leverage must be positive"); }

        let price       = ticker;
        let lev_clamped = leverage.max(cd.min_leverage).min(cd.max_leverage) as u32;
        let volume      = normalize_volume(amount / cd.contract_size, cd.vol_scale, cd.vol_unit, cd.min_vol, cd.max_vol);
        let notional    = amount * price;
        let usdt        = round_scale(notional, cd.price_scale);

        let mut payload = self.volume_payload(&cd, volume, lev_clamped, price, usdt, pt);
        payload["required_margin"] = json!(round_scale(notional / lev_clamped as f64, cd.price_scale));
        payload["amount"]          = json!(amount);
        payload["notional_value"]  = json!(notional);
        payload["contract_size"]   = json!(cd.contract_size);

        json!({ "success": true, "code": 0, "data": payload })
    }

    pub async fn receive_futures_testnet_asset(&self, params: Value) -> Value {
        self.request(
            HttpMethod::Post,
            ApiType::Other,
            "https://futures.testnet.mexc.com/mock/contract/asset/receive",
            json!({
                "currency": params.get("currency").cloned().unwrap_or(json!("USDT")),
                "amount":   params.get("amount"),
            }),
        ).await
    }

    fn mk_error(&self, code: i64, msg: &str) -> Value {
        build_error(code, msg, self.is_testnet)
    }

    fn extract_market_data(
        &self,
        contract_res: &Value,
        ticker_res:   &Value,
        params:       &Value,
    ) -> Result<(ContractData, f64, PriceType), Value> {
        let cd_raw = contract_res.get("data").and_then(|d| d.get(0))
            .ok_or_else(|| self.mk_error(400, "Failed to get contract data"))?;

        let pt     = PriceType::from_str(params.get("price_type").and_then(|v| v.as_str()).unwrap_or(""));
        let ticker = ticker_res.get("data").and_then(|d| d.get(pt.ticker_field()))
            .and_then(|v| v.as_f64())
            .ok_or_else(|| self.mk_error(400, "Failed to get ticker data"))?;

        let cs = cd_raw.get("contractSize").and_then(|v| v.as_f64()).unwrap_or(0.0);
        if cs <= 0.0 {
            return Err(self.mk_error(400, "Invalid contract size"));
        }

        let cd = ContractData {
            contract_size: cs,
            min_leverage:  cd_raw.get("minLeverage").and_then(|v| v.as_f64()).unwrap_or(1.0),
            max_leverage:  cd_raw.get("maxLeverage").and_then(|v| v.as_f64()).unwrap_or(125.0),
            vol_scale:     cd_raw.get("volScale").and_then(|v| v.as_u64()).unwrap_or(0) as u32,
            vol_unit:      cd_raw.get("volUnit").and_then(|v| v.as_f64()).unwrap_or(0.0),
            min_vol:       cd_raw.get("minVol").and_then(|v| v.as_f64()).unwrap_or(0.0),
            max_vol:       cd_raw.get("maxVol").and_then(|v| v.as_f64()).unwrap_or(f64::MAX),
            price_scale:   cd_raw.get("priceScale").and_then(|v| v.as_u64()).unwrap_or(2) as u32,
            price_unit:    cd_raw.get("priceUnit").and_then(|v| v.as_f64()).unwrap_or(0.0),
            raw:           cd_raw.clone(),
        };

        Ok((cd, ticker, pt))
    }

    fn volume_payload(
        &self,
        cd:        &ContractData,
        volume:    f64,
        leverage:  u32,
        price:     f64,
        usdt:      f64,
        pt:        PriceType,
    ) -> Value {
        json!({
            "usdt_value":   usdt,
            "volume":       volume,
            "leverage":     leverage,
            "price":        price,
            "min_volume":   cd.min_vol,
            "max_volume":   if cd.max_vol == f64::MAX { Value::Null } else { json!(cd.max_vol) },
            "min_leverage": cd.min_leverage,
            "max_leverage": cd.max_leverage,
            "price_type":   pt.value(),
            "volume_scale": cd.vol_scale,
            "volume_unit":  cd.vol_unit,
            "price_scale":  cd.price_scale,
            "price_unit":   cd.price_unit,
        })
    }
}

struct ContractData {
    contract_size: f64,
    min_leverage:  f64,
    max_leverage:  f64,
    vol_scale:     u32,
    vol_unit:      f64,
    min_vol:       f64,
    max_vol:       f64,
    price_scale:   u32,
    price_unit:    f64,
    raw:           Value,
}

fn round4(v: f64) -> f64 { (v * 10000.0).round() / 10000.0 }
fn round2(v: f64) -> f64 { (v * 100.0).round() / 100.0 }
fn round_scale(v: f64, scale: u32) -> f64 {
    let f = 10f64.powi(scale as i32);
    (v * f).round() / f
}

fn parse_symbol(params: &Value) -> (String, String) {
    if let Some(symbol) = params.get("symbol").and_then(|v| v.as_str()) {
        let mut parts = symbol.splitn(2, '_');
        let base  = parts.next().unwrap_or("").to_string();
        let quote = parts.next().unwrap_or("").to_string();
        return (base, quote);
    }
    let base  = params.get("base_coin").and_then(|v| v.as_str()).unwrap_or("").to_string();
    let quote = params.get("quote_coin").and_then(|v| v.as_str()).unwrap_or("").to_string();
    (base, quote)
}