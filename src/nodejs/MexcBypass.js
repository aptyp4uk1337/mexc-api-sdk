'use strict';

import https from 'https';
import http from 'http';
import crypto from 'crypto';
import { URL } from 'url';
import zlib from 'zlib';
import { SocksProxyAgent } from 'socks-proxy-agent';
import { HttpsProxyAgent } from 'https-proxy-agent';

export const ApiType = Object.freeze({
  General: 'general',
  Futures: 'futures',
  Contract: 'contract',
  Other: 'other',
});

export const HttpMethod = Object.freeze({
  GET: 'GET',
  POST: 'POST',
  PUT: 'PUT',
  DELETE: 'DELETE',
});

function methodHasBody(method) {
  return method === HttpMethod.POST || method === HttpMethod.PUT;
}

export const PriceType = Object.freeze({
  LastPrice: 'last_price',
  FairPrice: 'fair_price',
  IndexPrice: 'index_price',
});

function priceTypeTickerField(pt) {
  switch (pt) {
    case PriceType.FairPrice: return 'fairPrice';
    case PriceType.IndexPrice: return 'indexPrice';
    default: return 'lastPrice';
  }
}

function priceTypeKlineSegment(pt) {
  switch (pt) {
    case PriceType.IndexPrice: return 'index_price';
    case PriceType.FairPrice: return 'fair_price';
    default: return '';
  }
}

const RENAME_MAPPINGS = {
  '/contract/detailV2': {
    dn: 'displayName', dne: 'displayNameEn', pot: 'positionOpenType',
    bc: 'baseCoin', qc: 'quoteCoin', bcn: 'baseCoinName', qcn: 'quoteCoinName',
    ft: 'futureType', sc: 'settleCoin', cs: 'contractSize',
    minL: 'minLeverage', maxL: 'maxLeverage', ccMaxL: 'countryConfigContractMaxLeverage',
    ps: 'priceScale', vs: 'volScale', as: 'amountScale',
    pu: 'priceUnit', vu: 'volUnit', minV: 'minVol', maxV: 'maxVol',
    blpr: 'bidLimitPriceRate', alpr: 'askLimitPriceRate',
    tfr: 'takerFeeRate', mfr: 'makerFeeRate',
    mmr: 'maintenanceMarginRate', imr: 'initialMarginRate',
    rbv: 'riskBaseVol', riv: 'riskIncrVol', rlss: 'riskLongShortSwitch',
    rim: 'riskIncrMmr', rii: 'riskIncrImr', rll: 'riskLevelLimit',
    pcv: 'priceCoefficientVariation', io: 'indexOrigin',
    in: 'isNew', ih: 'isHot', ihd: 'isHidden', ip: 'isPromoted',
    cp: 'conceptPlate', cpi: 'conceptPlateId', rlt: 'riskLimitType',
    mno: 'maxNumOrders', moml: 'marketOrderMaxLevel',
    moplr1: 'marketOrderPriceLimitRate1', moplr2: 'marketOrderPriceLimitRate2',
    tp: 'triggerProtect', ae: 'appraisal', sac: 'showAppraisalCountdown',
    ad: 'automaticDelivery', aa: 'apiAllowed', dsl: 'depthStepList',
    lmv: 'limitMaxVol', tsd: 'threshold', bciu: 'baseCoinIconUrl',
    bcid: 'baseCoinId', ct: 'createTime', ot: 'openingTime',
    oco: 'openingCountdownOption', sbo: 'showBeforeOpen',
    iml: 'isMaxLeverage', izfr: 'isZeroFeeRate', rlm: 'riskLimitMode',
    rlcs: 'riskLimitCustom', izfs: 'isZeroFeeSymbol',
    liqfr: 'liquidationFeeRate', frm: 'feeRateMode',
    levfrs: 'leverageFeeRates', tiefrs: 'tieredFeeRates',
  },
  '/api/platform/spot/market-v2/web/symbolsV2': {
    mcd: 'marketCurrencyId', cd: 'coinId', vn: 'currency',
    fn: 'currencyFullName', srt: 'sortOrder', sts: 'status',
    tp: 'marketType', in: 'icon', ot: 'openingTime',
    cp: 'categories', ci: 'categories_ids', ps: 'priceScale',
    qs: 'quantityScale', cdm: 'contractDecimalMultiplier',
    st: 'spotEnabled', dst: 'depositStatus', tt: 'tradingType',
    ca: 'contractAddress', fne: 'currencyFullNameEn',
  },
};

function renameFields(data, endpoint) {
  for (const [pattern, map] of Object.entries(RENAME_MAPPINGS)) {
    if (endpoint.includes(pattern)) {
      return recursiveRename(data, map);
    }
  }
  return data;
}

function recursiveRename(arr, map) {
  if (Array.isArray(arr)) {
    return arr.map(item => (typeof item === 'object' && item !== null ? recursiveRename(item, map) : item));
  }
  if (typeof arr !== 'object' || arr === null) return arr;
  const out = {};
  for (const [k, v] of Object.entries(arr)) {
    out[map[k] ?? k] = (typeof v === 'object' && v !== null) ? recursiveRename(v, map) : v;
  }
  return out;
}

class SignatureGenerator {
  #apiKey;
  #lastMs = 0;
  #msIncrement = 0;

  constructor(apiKey) {
    this.#apiKey = apiKey;
  }

  generate(payload, method, forceTime = null) {
    const time = forceTime ?? this.#uniqueMs();
    const g = crypto.createHash('md5').update(this.#apiKey + time).digest('hex').slice(7);
    let sign;

    if (methodHasBody(method)) {
      const body = Object.keys(payload).length === 0 ? '' : JSON.stringify(payload);
      sign = crypto.createHash('md5').update(time + body + g).digest('hex');
    } else {
      const qs = new URLSearchParams(payload).toString();
      sign = crypto.createHash('md5').update(time + qs + g).digest('hex');
    }

    return { timestamp: time, sign };
  }

  #uniqueMs() {
    const now = Date.now();
    if (now === this.#lastMs) {
      this.#msIncrement++;
    } else {
      this.#lastMs = now;
      this.#msIncrement = 0;
    }
    return String(now + this.#msIncrement);
  }
}

const _agents = new Map();

function _getAgent(isHttps, keepAlive = true) {
  const key = `${isHttps ? 'https' : 'http'}:${keepAlive}`;
  if (!_agents.has(key)) {
    const Mod = isHttps ? https : http;
    _agents.set(key, new Mod.Agent({ keepAlive, maxSockets: 256, maxFreeSockets: 64 }));
  }
  return _agents.get(key);
}

/**
 * Perform a single request. Returns { body: string, status: number }.
 *
 * @param {string}   url
 * @param {string}   method
 * @param {string[]} headers  flat array: ['Header-Name: value', ...]
 * @param {object|null} body
 * @param {object|null} proxy  { host, port, auth } — HTTP CONNECT only
 * @returns {Promise<{body: string, status: number}>}
 */
function rawRequest(url, method, headers, body = null, proxy = null) {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url);
    const isHttps = parsed.protocol === 'https:';
    const jsonBody = body && (methodHasBody(method) || method === 'DELETE')
      ? JSON.stringify(body)
      : null;

    const headerObj = {
      'Content-Type': 'application/json',
      'Accept': '*/*',
      'User-Agent': 'Mozilla/5.0 (compatible; MEXC-API/1.0)',
      'Connection': 'keep-alive',
      'Cache-Control': 'no-cache',
      'Accept-Encoding': 'gzip, deflate, br',
    };

    for (const h of headers) {
      const idx = h.indexOf(':');
      if (idx > 0) {
        const name = h.slice(0, idx).trim();
        const val = h.slice(idx + 1).trim();
        headerObj[name] = val;
      }
    }

    if (jsonBody) {
      headerObj['Content-Length'] = Buffer.byteLength(jsonBody);
    }

    const requestOptions = {
      method,
      headers: headerObj,
      timeout: 5000,
    };

    if (proxy) {
      const proxyUrl = `${proxy.type}://${proxy.auth ? proxy.auth + '@' : ''}${proxy.host}:${proxy.port}`;

      if (proxy.type.startsWith('socks')) {
        requestOptions.agent = new SocksProxyAgent(proxyUrl);
      } else {
        requestOptions.agent = new HttpsProxyAgent(proxyUrl);
      }
    } else {
      requestOptions.agent = _getAgent(isHttps);
    }

    const mod = isHttps ? https : http;
    const req = mod.request(parsed, requestOptions, (res) => {
      const chunks = [];
      res.on('data', chunk => chunks.push(chunk));
      res.on('end', () => {
        const raw = Buffer.concat(chunks);
        const enc = res.headers['content-encoding'] || '';

        const decompress = () => {
          if (enc.includes('br')) {
            return new Promise((r, e) => zlib.brotliDecompress(raw, (err, buf) => err ? e(err) : r(buf.toString())));
          }
          if (enc.includes('gzip')) {
            return new Promise((r, e) => zlib.gunzip(raw, (err, buf) => err ? e(err) : r(buf.toString())));
          }
          if (enc.includes('deflate')) {
            return new Promise((r, e) => zlib.inflate(raw, (err, buf) => err ? e(err) : r(buf.toString())));
          }
          return Promise.resolve(raw.toString('utf8'));
        };

        decompress()
          .then(body => resolve({ body, status: res.statusCode }))
          .catch(reject);
      });
      res.on('error', reject);
    });

    req.on('error', reject);
    req.setTimeout(5000, () => { req.destroy(new Error('timeout')); });

    if (jsonBody) req.write(jsonBody);
    req.end();
  });
}

function parseProxyUrl(proxyUrl) {
  if (!proxyUrl) return null;
  try {
    const p = new URL(proxyUrl);
    return {
      host: p.hostname,
      port: parseInt(p.port, 10) || 1080,
      type: p.protocol.replace(':', ''),
      auth: p.username ? `${p.username}:${p.password}` : null,
    };
  } catch {
    return null;
  }
}

function handleResponse(raw, status, isTestnet, endpoint = null) {
  if (!raw) {
    return makeError(status, 'Request failed: empty response', isTestnet);
  }

  let decoded;
  try {
    decoded = JSON.parse(raw);
  } catch (e) {
    const preview = raw.slice(0, 200);
    return makeError(status, `Invalid JSON: ${e.message} | raw: ${preview}`, isTestnet);
  }

  if (decoded?.data && typeof decoded.data === 'object' && endpoint) {
    decoded.data = renameFields(decoded.data, endpoint);
  }

  decoded.is_testnet = isTestnet;
  return decoded;
}

function makeError(code, message, isTestnet = false) {
  return {
    success: false,
    code,
    message,
    timestamp: Date.now(),
    is_testnet: isTestnet,
  };
}

export class MexcBypass {
  #apiKey;
  #isTestnet;
  #proxy;
  #signer;
  #baseUrls;

  constructor(apiKey = '', isTestnet = false, proxyUrl = null) {
    this.#apiKey = apiKey;
    this.#isTestnet = isTestnet;
    this.#proxy = parseProxyUrl(proxyUrl);
    this.#signer = new SignatureGenerator(apiKey);

    this.#baseUrls = {
      [ApiType.General]: 'https://www.mexc.com',
      [ApiType.Futures]: isTestnet
        ? 'https://futures.testnet.mexc.com/api/v1'
        : 'https://futures.mexc.com/api/v1',
      [ApiType.Contract]: 'https://contract.mexc.com/api/v1',
      [ApiType.Other]: '',
    };
  }

  async #request(method, apiType, endpoint, params = {}) {
    const cleanParams = Object.fromEntries(
      Object.entries(params).filter(([, v]) => v !== null && v !== undefined && v !== ''),
    );

    const sig = this.#signer.generate(cleanParams, method);
    const baseUrl = this.#resolveBase(apiType, endpoint);
    const origin = this.#extractOrigin(baseUrl);
    const headers = this.#buildHeaders(apiType, sig, origin);
    const url = this.#buildUrl(baseUrl, endpoint, method, cleanParams);
    const body = methodHasBody(method) ? cleanParams : null;

    let rawBody, status;
    try {
      ({ body: rawBody, status } = await rawRequest(url, method, headers, body, this.#proxy));
    } catch (err) {
      return makeError(0, `Request error: ${err.message}`, this.#isTestnet);
    }

    return handleResponse(rawBody, status, this.#isTestnet, endpoint);
  }

  /**
   * Execute multiple named requests in parallel.
   *
   * @param {Record<string, () => Promise<any>>} requests
   * @returns {Promise<Record<string, any>>}
   *
   * @example
   * const results = await client.batch({
   *   ticker:   () => client.getFuturesTickers({ symbol: 'BTC_USDT' }),
   *   contract: () => client.getFuturesContracts({ symbol: 'BTC_USDT' }),
   * });
   */
  async batch(requests) {
    const keys = Object.keys(requests);
    const results = await Promise.all(keys.map(k => requests[k]()));
    return Object.fromEntries(keys.map((k, i) => [k, results[i]]));
  }

  /**
   * Execute the same callback for multiple accounts simultaneously.
   *
   * @param {Record<string, {apiKey?: string, isTestnet?: boolean, proxy?: string}>} accounts
   * @param {(client: MexcBypass) => Record<string, () => Promise<any>>} callback
   * @returns {Promise<Record<string, Record<string, any>>>}
   *
   * @example
   * const results = await MexcBypass.batchAccounts({
   *   acc1: { apiKey: 'key1' },
   *   acc2: { apiKey: 'key2', isTestnet: true, proxy: 'socks5://user:pass@host:1080' },
   * }, client => ({
   *   positions: () => client.getFuturesOpenPositions(),
   *   balance:   () => client.getFuturesAssets({ currency: 'USDT' }),
   * }));
   */
  static async batchAccounts(accounts, callback) {
    const entries = Object.entries(accounts);
    const results = await Promise.all(
      entries.map(async ([accountKey, cfg]) => {
        const client = new MexcBypass(cfg.apiKey ?? cfg.webkey ?? '', cfg.isTestnet ?? false, cfg.proxy ?? null);
        const requestMap = callback(client);
        const batchResult = await client.batch(requestMap);
        return [accountKey, batchResult];
      }),
    );
    return Object.fromEntries(results);
  }

  #resolveBase(apiType, endpoint) {
    if (apiType === ApiType.Other) {
      const p = new URL(endpoint);
      return `${p.protocol}//${p.host}`;
    }
    return this.#baseUrls[apiType];
  }

  #extractOrigin(baseUrl) {
    const idx = baseUrl.indexOf('/', 8);
    return idx !== -1 ? baseUrl.slice(0, idx) : baseUrl;
  }

  #buildHeaders(apiType, sig, origin) {
    const shared = [
      `Origin: ${origin}`,
    ];

    if (apiType === ApiType.General) {
      return [
        ...shared,
        `Cookie: u_id=${this.#apiKey}; uc_token=${this.#apiKey};`,
        `Ucenter-Token: ${this.#apiKey}`,
      ];
    }

    return [
      ...shared,
      `Authorization: ${this.#apiKey}`,
      `X-Mxc-Nonce: ${sig.timestamp}`,
      `X-Mxc-Sign: ${sig.sign}`,
    ];
  }

  #buildUrl(baseUrl, endpoint, method, params) {
    let url = baseUrl + endpoint;
    if (!methodHasBody(method) && Object.keys(params).length > 0) {
      url += '?' + new URLSearchParams(params).toString();
    }
    return url;
  }

  #weekRange() {
    const now = new Date();
    const day = now.getDay(); // 0=Sun
    const diff = (day === 0 ? -6 : 1 - day);
    const mon = new Date(now);
    mon.setDate(now.getDate() + diff);
    mon.setHours(0, 0, 0, 0);
    const sun = new Date(mon);
    sun.setDate(mon.getDate() + 6);
    sun.setHours(23, 59, 59, 99);
    return { start: mon.getTime(), end: sun.getTime() };
  }

  #normalizeVolume(raw, cd) {
    const scale = parseInt(cd.volScale ?? 0, 10);
    const unit = parseFloat(cd.volUnit ?? 0);
    let v = parseFloat(raw.toFixed(scale));
    if (unit > 0) v = Math.floor(v / unit) * unit;
    return Math.max(parseFloat(cd.minVol ?? 0), Math.min(v, parseFloat(cd.maxVol ?? Number.MAX_VALUE)));
  }

  #volumePayload(cd, volume, leverage, price, usdtValue, pt) {
    return {
      usdt_value: usdtValue,
      volume,
      leverage,
      price,
      min_volume: cd.minVol ?? null,
      max_volume: cd.maxVol ?? null,
      min_leverage: cd.minLeverage ?? null,
      max_leverage: cd.maxLeverage ?? null,
      price_type: pt,
      volume_scale: cd.volScale ?? null,
      volume_unit: cd.volUnit ?? null,
      price_scale: cd.priceScale ?? null,
      price_unit: cd.priceUnit ?? null,
    };
  }

  #error(code, message) {
    return makeError(code, message, this.#isTestnet);
  }

  async #fetchTickerAndContract(symbol) {
    const result = await this.batch({
      ticker: () => this.getFuturesTickers({ symbol }),
      contract: () => this.getFuturesContracts({ symbol }),
    });

    if (!result.ticker?.success || !result.ticker?.data) return { ok: false, error: this.#error(400, 'Failed to get ticker data') };
    if (!result.contract?.success || !result.contract?.data?.[0]) return { ok: false, error: this.#error(400, 'Failed to get contract data') };

    return { ok: true, result };
  }

  #resolveMarket(market, params) {
    const pt = Object.values(PriceType).includes(params.price_type) ? params.price_type : PriceType.LastPrice;
    return {
      ticker: market.result.ticker.data?.[priceTypeTickerField(pt)] ?? null,
      contract: market.result.contract.data[0],
      pt,
    };
  }

  #parseSymbol(params) {
    if (params.symbol) {
      const [base = '', quote = ''] = params.symbol.split('_', 2);
      return [base, quote];
    }
    return [params.base_coin ?? '', params.quote_coin ?? ''];
  }

  getServerTime() { return this.#request(HttpMethod.GET, ApiType.Contract, '/contract/ping'); }
  ping() { return this.#request(HttpMethod.GET, ApiType.General, '/api/common/ping'); }
  validation() { return this.#request(HttpMethod.POST, ApiType.General, '/ucenter/api/login/validation'); }
  getWSToken() { return this.#request(HttpMethod.GET, ApiType.General, '/ucenter/api/ws_token'); }
  getCustomerInfo() { return this.#request(HttpMethod.GET, ApiType.General, '/ucenter/api/customer_info'); }
  getUserInfo() { return this.#request(HttpMethod.GET, ApiType.General, '/ucenter/api/user_info'); }
  getLatestsDeposits() { return this.#request(HttpMethod.GET, ApiType.General, '/api/platform/deposit/v3/latest'); }
  getSecureInfo() { return this.#request(HttpMethod.GET, ApiType.General, '/ucenter/api/secure/check'); }
  getOriginInfo() { return this.#request(HttpMethod.GET, ApiType.General, '/ucenter/api/origin_info'); }
  logout() { return this.#request(HttpMethod.POST, ApiType.General, '/ucenter/api/logout'); }

  getDepositAddresses(params) {
    if (params.currency) {
      return this.#request(HttpMethod.GET, ApiType.General, `/api/platform/asset/api/asset/spot/currency/v3?currency=${params.currency}`);
    }
    return this.#request(HttpMethod.GET, ApiType.General, `/api/platform/asset/api/deposit/address/list?vcoinId=${params.vcoin_id}`);
  }

  async getMarketSymbols(params = {}) {
    const response = await this.#request(HttpMethod.GET, ApiType.General, '/api/platform/spot/market-v2/web/symbolsV2');

    if (!Object.keys(params).length) return response;

    const [baseCoin, quoteCoin] = this.#parseSymbol(params);
    if (!baseCoin) return response;

    if (!response.data?.symbols?.[quoteCoin]) {
      return this.#error(400, `No ${quoteCoin} symbols found`);
    }

    const search = baseCoin.toUpperCase();
    const matches = response.data.symbols[quoteCoin].filter(item => item.currency?.toUpperCase() === search);

    if (!matches.length) return this.#error(404, 'No matching tokens found');

    return { success: true, count: matches.length, data: matches, timestamp: Math.floor(Date.now() / 1000) };
  }

  async getSpotOrderBook(params) {
    const symbols = await this.getMarketSymbols({ symbol: params.symbol });
    if (!symbols.success) return symbols;

    return this.#request(HttpMethod.GET, ApiType.General, '/api/platform/spot/market-v2/web/depth/v2', {
      symbolId: symbols.data[0].id,
      decimal: params.decimal ?? '0.0000001',
      count: params.count ?? 100,
    });
  }

  async createSpotOrder(params) {
    const path = params.type === 'LIMIT_ORDER'
      ? '/api/platform/spot/order/place'
      : '/api/platform/spot/v4/order/place';
    const symbols = await this.getMarketSymbols({ symbol: params.symbol });
    if (!symbols.success) return symbols;

    return this.#request(HttpMethod.POST, ApiType.General, path, {
      orderType: params.type,
      tradeType: params.side,
      price: params.price ?? null,
      amount: params.amount ?? null,
      quantity: params.quantity ?? null,
      marketCurrencyId: symbols.data[0].marketCurrencyId,
      currencyId: symbols.data[0].coinId,
      orderSource: 'WEB',
      ts: Date.now(),
    });
  }

  cancelSpotOrder(params) {
    return this.#request(HttpMethod.DELETE, ApiType.General, '/api/platform/spot/order/cancel/v2', {
      orderId: params.order_id,
    });
  }

  getReferralsList(params = {}) {
    const week = this.#weekRange();
    return this.#request(HttpMethod.GET, ApiType.General, '/api/assetbussiness/invite/invites', {
      startTime: params.start_time ?? week.start,
      endTime: params.end_time ?? week.end,
      page: params.page_num ?? 1,
      pageSize: params.page_size ?? 10,
    });
  }

  getAssetsOverview(params = {}) {
    const endpoint = params.convert
      ? '/api/platform/asset/api/asset/overview/convert/v2'
      : '/api/platform/asset/api/asset/overview/v2';
    return this.#request(HttpMethod.GET, ApiType.General, endpoint);
  }

  getFuturesFeeRate() { return this.#request(HttpMethod.GET, ApiType.Futures, '/private/account/contract/fee_rate'); }
  getFuturesZeroFeeRate() { return this.#request(HttpMethod.GET, ApiType.Futures, '/private/account/contract/zero_fee_rate'); }
  getFuturesTodayPnL() { return this.#request(HttpMethod.GET, ApiType.Futures, '/private/account/asset/analysis/today_pnl'); }

  getFuturesAnalysis(params = {}) {
    const now = Date.now();
    const endTime = Math.min(params.end_time ?? now, now);
    const startTime = params.start_time ?? (endTime - 7 * 86_400 * 1000);

    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/account/asset/analysis/v3', {
      currency: params.currency ?? 'USDT',
      symbol: params.symbol ?? null,
      include_unrealised_pnl: params.include_unrealised_pnl ?? 0,
      reverse: params.reverse ?? 0,
      startTime: startTime < endTime ? startTime : endTime - 1000,
      endTime,
    });
  }

  getFuturesAssets(params = {}) {
    const endpoint = params.currency
      ? `/private/account/asset/${params.currency}`
      : '/private/account/assets';
    return this.#request(HttpMethod.GET, ApiType.Futures, endpoint);
  }

  getFuturesAssetTransferRecords(params = {}) {
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/account/transfer_record', {
      currency: params.currency ?? null,
      state: params.state ?? null,
      type: params.type ?? null,
      page_num: params.page_num ?? 1,
      page_size: params.page_size ?? 20,
    });
  }

  getFuturesRiskLimits(params = {}) {
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/account/risk_limit', {
      symbol: params.symbol ?? null,
    });
  }

  getFuturesFundingRate(params) { return this.#request(HttpMethod.GET, ApiType.Contract, `/contract/funding_rate/${params.symbol}`); }
  getFuturesContracts(params = {}) { return this.#request(HttpMethod.GET, ApiType.Futures, '/contract/detailV2', { symbol: params.symbol ?? null }); }
  getFuturesContractIndexPrice(params) { return this.#request(HttpMethod.GET, ApiType.Contract, `/contract/index_price/${params.symbol}`); }
  getFuturesContractFairPrice(params) { return this.#request(HttpMethod.GET, ApiType.Contract, `/contract/fair_price/${params.symbol}`); }

  getFuturesContractKlineData(params) {
    const pt = Object.values(PriceType).includes(params.price_type) ? params.price_type : PriceType.LastPrice;
    const segment = priceTypeKlineSegment(pt);
    const path = segment ? `/contract/kline/${segment}/${params.symbol}` : `/contract/kline/${params.symbol}`;

    return this.#request(HttpMethod.GET, ApiType.Contract, path, {
      interval: params.interval ?? 'Min1',
      start: params.start_time ?? null,
      end: params.end_time ?? null,
    });
  }

  getFuturesTickers(params = {}) {
    return this.#request(HttpMethod.GET, ApiType.Futures, '/contract/ticker', { symbol: params.symbol ?? null });
  }

  getFuturesOpenPositions(params = {}) {
    const clean = {};
    if (params.symbol) clean.symbol = params.symbol;
    if (params.position_id) clean.positionId = params.position_id;
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/position/open_positions', clean);
  }

  getFuturesPositionsHistory(params = {}) {
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/position/list/history_positions', {
      symbol: params.symbol ?? null,
      type: params.type ?? null,
      page_num: params.page_num ?? 1,
      page_size: params.page_size ?? 20,
    });
  }

  closeAllFuturesPositions() { return this.#request(HttpMethod.POST, ApiType.Futures, '/private/position/close_all'); }

  reverseFuturesPosition(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/position/reverse', {
      positionId: params.position_id,
      symbol: params.symbol,
      vol: params.vol,
    });
  }

  getFuturesPositionMode() { return this.#request(HttpMethod.GET, ApiType.Futures, '/private/position/position_mode'); }

  changeFuturesPositionMode(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/position/change_position_mode', {
      positionMode: params.position_mode,
    });
  }

  getFuturesLeverage(params) {
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/position/leverage', { symbol: params.symbol });
  }

  changeFuturesPositionMargin(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/position/change_margin', {
      positionId: params.position_id,
      amount: params.amount,
      type: params.type,
    });
  }

  changeFuturesPositionLeverage(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/position/change_leverage', {
      positionId: params.position_id,
      leverage: params.leverage,
      openType: params.open_type ?? null,
      symbol: params.symbol ?? null,
      positionType: params.position_type ?? null,
    });
  }

  getFuturesOrdersDeals(params) {
    const week = this.#weekRange();
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/order/list/order_deals', {
      symbol: params.symbol,
      start_time: params.start_time ?? week.start,
      end_time: params.end_time ?? week.end,
      page_num: params.page_num ?? 1,
      page_size: params.page_size ?? 20,
    });
  }

  getFuturesPendingOrders(params = {}) {
    const endpoint = params.symbol
      ? `/private/order/list/open_orders/${params.symbol}`
      : '/private/order/list/open_orders/';
    return this.#request(HttpMethod.GET, ApiType.Futures, endpoint, {
      page_num: params.page_num ?? 1,
      page_size: params.page_size ?? 20,
    });
  }

  getFuturesOrdersHistory(params = {}) {
    const week = this.#weekRange();
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/order/list/history_orders', {
      symbol: params.symbol ?? null,
      states: params.states ?? null,
      category: params.category ?? null,
      side: params.side ?? null,
      start_time: params.start_time ?? week.start,
      end_time: params.end_time ?? week.end,
      page_num: params.page_num ?? 1,
      page_size: params.page_size ?? 20,
    });
  }

  getFuturesOpenLimitOrders(params = {}) {
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/order/list/open_orders', {
      page_size: params.page_size ?? 200,
    });
  }

  getFuturesOpenStopOrders(params = {}) {
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/stoporder/open_orders', {
      page_size: params.page_size ?? 200,
    });
  }

  async getFuturesOpenOrders(params = {}) {
    const pageSize = params.page_size ?? 200;
    const result = await this.batch({
      limit_orders: () => this.getFuturesOpenLimitOrders({ page_size: pageSize }),
      stop_orders: () => this.getFuturesOpenStopOrders({ page_size: pageSize }),
    });

    const merged = [
      ...(result.limit_orders?.success ? (result.limit_orders.data ?? []) : []),
      ...(result.stop_orders?.success ? (result.stop_orders.data ?? []) : []),
    ];

    return { success: true, code: 0, data: merged };
  }

  getFuturesClosedOrders(params = {}) {
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/order/close_orders', {
      symbol: params.symbol ?? null,
      category: params.category ?? null,
      page_num: params.page_num ?? 1,
      page_size: params.page_size ?? 20,
    });
  }

  getFuturesOrdersById(params) {
    const ids = String(params.ids).split(',').map(s => s.trim());
    return ids.length > 1
      ? this.#request(HttpMethod.GET, ApiType.Futures, '/private/order/batch_query', { order_ids: params.ids })
      : this.#request(HttpMethod.GET, ApiType.Futures, `/private/order/get/${params.ids}`);
  }

  createFuturesOrder(params) {
    const payload = {
      symbol: params.symbol,
      price: params.price ?? null,
      type: params.type,
      openType: params.open_type ?? null,
      positionMode: params.position_mode ?? null,
      side: params.side,
      vol: params.vol,
      leverage: params.leverage ?? null,
      positionId: params.position_id ?? null,
      externalOid: params.external_id ?? null,
      takeProfitPrice: params.take_profit_price ?? null,
      profitTrend: params.take_profit_trend ?? 1,
      stopLossPrice: params.stop_loss_price ?? null,
      lossTrend: params.stop_loss_trend ?? 1,
      priceProtect: params.price_protect ?? 0,
      reduceOnly: params.reduce_only ?? false,
      marketCeiling: params.market_ceiling ?? false,
      flashClose: params.flash_close ?? null,
      bboTypeNum: params.bbo_type ?? null,
    };
    const clean = Object.fromEntries(Object.entries(payload).filter(([, v]) => v !== null && v !== undefined));
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/order/create', clean);
  }

  cancelFuturesOrders(params) {
    const ids = Array.isArray(params.ids)
      ? params.ids
      : String(params.ids).split(',').map(s => s.trim());
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/order/cancel', ids);
  }

  cancelFuturesOrderWithExternalId(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/order/cancel_with_external', {
      symbol: params.symbol,
      externalOid: params.external_id,
    });
  }

  cancelAllFuturesOrders(params = {}) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/order/cancel_all', {
      symbol: params.symbol ?? null,
    });
  }

  changeFuturesLimitOrderPrice(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, 'private/order/change_limit_order', {
      orderId: params.order_id,
      price: params.price,
      vol: params.vol,
    });
  }

  chaseFuturesOrder(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/order/chase_limit_order', {
      orderId: params.order_id,
    });
  }

  createFuturesChaseOrder(params) {
    const payload = {
      chaseType: params.chase_type,
      distanceType: params.distance_type ?? null,
      distanceValue: params.distance_value ?? null,
      maxDistanceType: params.max_distance_type ?? null,
      maxDistanceValue: params.max_distance_value ?? null,
      leverage: params.leverage,
      openType: params.open_type,
      side: params.side,
      symbol: params.symbol,
      vol: params.vol,
    };
    const clean = Object.fromEntries(Object.entries(payload).filter(([, v]) => v !== null && v !== undefined));
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/order/chase_limit/place', clean);
  }

  createFuturesStopOrder(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/stoporder/place/v2', {
      positionId: params.position_id,
      volType: params.vol_type ?? 2,
      vol: params.vol ?? null,
      takeProfitType: params.take_profit_type ?? null,
      takeProfitOrderPrice: params.take_profit_order_price ?? null,
      takeProfitPrice: params.take_profit_price ?? null,
      takeProfitReverse: params.take_profit_reverse ?? 2,
      profitTrend: params.take_profit_trend ?? 1,
      takeProfitVol: params.take_profit_volume ?? null,
      stopLossType: params.stop_loss_type ?? null,
      stopLossOrderPrice: params.stop_loss_order_price ?? null,
      stopLossPrice: params.stop_loss_price ?? null,
      stopLossReverse: params.stop_loss_reverse ?? 2,
      lossTrend: params.stop_loss_trend ?? 1,
      profitLossVolType: params.profit_loss_vol_type ?? 'SAME',
      priceProtect: params.price_protect ?? 0,
    });
  }

  getFuturesStopLimitOrders(params = {}) {
    const week = this.#weekRange();
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/stoporder/list/orders', {
      symbol: params.symbol ?? null,
      is_finished: params.is_finished ?? null,
      start_time: params.start_time ?? week.start,
      end_time: params.end_time ?? week.end,
      page_num: params.page_num ?? 1,
      page_size: params.page_size ?? 20,
    });
  }

  cancelStopLimitOrders(params) {
    const ids = typeof params.ids === 'string'
      ? params.ids.split(',').map(s => s.trim()).filter(Boolean)
      : params.ids;
    const payload = ids.map(id => ({ stopPlanOrderId: parseInt(id, 10) }));
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/stoporder/cancel', payload);
  }

  cancelAllFuturesStopLimitOrders(params = {}) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/stoporder/cancel_all', {
      positionId: params.position_id ?? null,
      symbol: params.symbol ?? null,
    });
  }

  changeFuturesOrderStopLimitPrice(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/stoporder/change_price', {
      orderId: params.order_id,
      takeProfitPrice: params.take_profit_price ?? null,
      stopLossPrice: params.stop_loss_pirce ?? null,
    });
  }

  changeFuturesPlanOrderStopLimitPrice(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/stoporder/change_plan_price', {
      stopPlanOrderId: params.order_id,
      takeProfitPrice: params.take_profit_price ?? null,
      stopLossPrice: params.stop_loss_pirce ?? null,
    });
  }

  changeFuturesOrderTargets(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/stoporder/change_plan_order', {
      orderId: params.real_order_id,
      profitTrend: params.take_profit_trend ?? null,
      takeProfitPrice: params.take_profit_price ?? null,
      takeProfitVolume: params.take_profit_volume ?? null,
      lossTrend: params.stop_loss_trend ?? null,
      stopLossPrice: params.stop_loss_price ?? null,
      stopLossVolume: params.stop_loss_volume ?? null,
    });
  }

  createFuturesTriggerOrder(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/planorder/place', {
      symbol: params.symbol,
      price: params.price ?? null,
      vol: params.vol,
      leverage: params.leverage ?? null,
      side: params.side,
      openType: params.open_type,
      triggerPrice: params.trigger_price,
      triggerType: params.trigger_type,
      executeCycle: params.execute_cycle,
      orderType: params.order_type,
      trend: params.trend,
    });
  }

  getFuturesTriggerOrders(params = {}) {
    const week = this.#weekRange();
    return this.#request(HttpMethod.GET, ApiType.Futures, '/private/planorder/list/orders', {
      symbol: params.symbol ?? null,
      states: params.states ?? null,
      start_time: params.start_time ?? week.start,
      end_time: params.end_time ?? week.end,
      page_num: params.page_num ?? 1,
      page_size: params.page_size ?? 20,
    });
  }

  cancelFuturesTriggerOrders(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/planorder/cancel', params.ids);
  }

  cancelAllFuturesTriggerOrders(params = {}) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/planorder/cancel_all', {
      symbol: params.symbol ?? null,
    });
  }

  createFuturesTrailingOrder(params) {
    const payload = {
      symbol: params.symbol,
      leverage: params.leverage,
      side: params.side,
      vol: params.vol,
      openType: params.open_type,
      trend: params.trend,
      activePrice: params.active_price,
      backType: params.back_type,
      backValue: params.back_value,
      positionMode: params.position_mode ?? 0,
      reduceOnly: params.reduce_only ?? null,
    };
    const clean = Object.fromEntries(Object.entries(payload).filter(([, v]) => v !== null && v !== undefined));
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/trackorder/place', clean);
  }

  cancelFuturesTrailingOrder(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/trackorder/cancel', {
      symbol: params.symbol ?? null,
      trackOrderId: params.order_id ?? null,
    });
  }

  changeFuturesTrailingOrder(params) {
    return this.#request(HttpMethod.POST, ApiType.Futures, '/private/trackorder/change_order', {
      symbol: params.symbol,
      trackOrderId: params.order_id,
      trend: params.trend,
      activePrice: params.active_price,
      backType: params.back_type,
      backValue: params.back_value,
      vol: params.vol,
    });
  }

  async calculateFuturesPositionPnL(params) {
    const result = await this.batch({
      contract: () => this.getFuturesContracts({ symbol: params.symbol }),
      ticker: () => this.getFuturesTickers({ symbol: params.symbol }),
    });

    const contract = result.contract?.data?.[0];
    const ticker = result.ticker?.data;

    if (!contract || !ticker || !contract.contractSize) {
      return this.#error(-1, 'Unable to calculate PnL: missing market data');
    }

    const pt = Object.values(PriceType).includes(params.price_type) ? params.price_type : PriceType.LastPrice;
    const price = parseFloat(ticker[priceTypeTickerField(pt)] ?? 0);
    const entry = parseFloat(params.entry_price);
    const volume = parseFloat(params.volume);
    const leverage = Math.max(1, parseInt(params.leverage ?? 1, 10));
    const cs = parseFloat(contract.contractSize);
    const isLong = parseInt(params.side, 10) === 1;
    const pnl = isLong
      ? (price - entry) * volume * cs
      : (entry - price) * volume * cs;
    const initialMargin = entry * volume * cs / leverage;

    return {
      success: true,
      code: 0,
      data: {
        pnl: +pnl.toFixed(4),
        pnl_percent: initialMargin > 0 ? +(pnl / initialMargin * 100).toFixed(2) : 0,
        volume,
        entry_price: entry,
        current_price: price,
        price_type: pt,
      },
    };
  }

  async calculateFuturesVolume(params) {
    for (const req of ['symbol', 'amount', 'leverage']) {
      if (!params[req]) return this.#error(404, `Missing required parameter: ${req}`);
    }

    const market = await this.#fetchTickerAndContract(params.symbol);
    if (!market.ok) return market.error;

    const { ticker, contract: cd, pt } = this.#resolveMarket(market, params);

    if (ticker === null) return this.#error(400, 'Invalid price value');
    if (params.amount <= 0) return this.#error(400, 'Amount must be positive');
    if (params.leverage <= 0) return this.#error(400, 'Leverage must be positive');
    if (!cd.contractSize || cd.contractSize <= 0) return this.#error(400, 'Invalid contract size');

    const price = parseFloat(ticker);
    const leverage = Math.max(cd.minLeverage, Math.min(params.leverage, cd.maxLeverage)) | 0;
    const volume = this.#normalizeVolume((params.amount * leverage) / (price * cd.contractSize), cd);
    const usdt = parseFloat((volume * price * cd.contractSize / leverage).toFixed(cd.priceScale));

    return { success: true, code: 0, data: this.#volumePayload(cd, volume, leverage, price, usdt, pt) };
  }

  async calculateFuturesVolumeFromBaseAmount(params) {
    for (const req of ['symbol', 'amount', 'leverage']) {
      if (!params[req]) return this.#error(404, `Missing required parameter: ${req}`);
    }

    const market = await this.#fetchTickerAndContract(params.symbol);
    if (!market.ok) return market.error;

    const { ticker, contract: cd, pt } = this.#resolveMarket(market, params);

    if (ticker === null) return this.#error(400, 'Invalid price value');
    if (params.amount <= 0) return this.#error(400, 'Base amount must be positive');
    if (params.leverage <= 0) return this.#error(400, 'Leverage must be positive');
    if (!cd.contractSize || cd.contractSize <= 0) return this.#error(400, 'Invalid contract size');

    const price = parseFloat(ticker);
    const leverage = Math.max(cd.minLeverage, Math.min(params.leverage, cd.maxLeverage)) | 0;
    const volume = this.#normalizeVolume(params.amount / cd.contractSize, cd);
    const notional = params.amount * price;
    const usdt = parseFloat(notional.toFixed(cd.priceScale));

    return {
      success: true,
      code: 0,
      data: {
        ...this.#volumePayload(cd, volume, leverage, price, usdt, pt),
        required_margin: parseFloat((notional / leverage).toFixed(cd.priceScale)),
        amount: params.amount,
        notional_value: notional,
        contract_size: cd.contractSize,
      },
    };
  }

  receiveFuturesTestnetAsset(params = {}) {
    return this.#request(
      HttpMethod.POST,
      ApiType.Other,
      'https://futures.testnet.mexc.com/mock/contract/asset/receive',
      {
        currency: params.currency ?? 'USDT',
        amount: params.amount ?? null,
      },
    );
  }
}
