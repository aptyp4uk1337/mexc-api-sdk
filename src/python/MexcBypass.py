from __future__ import annotations

import asyncio
import hashlib
import json
import time
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Callable, Optional
from urllib.parse import urlencode, urlparse

import aiohttp
from aiohttp_socks import ProxyConnector, ProxyType

class ApiType(str, Enum):
    GENERAL  = "general"
    FUTURES  = "futures"
    CONTRACT = "contract"
    OTHER    = "other"


class HttpMethod(str, Enum):
    GET    = "GET"
    POST   = "POST"
    PUT    = "PUT"
    DELETE = "DELETE"

    @property
    def has_body(self) -> bool:
        return self in (HttpMethod.POST, HttpMethod.PUT)


class PriceType(str, Enum):
    LAST_PRICE  = "last_price"
    FAIR_PRICE  = "fair_price"
    INDEX_PRICE = "index_price"

    @property
    def ticker_field(self) -> str:
        return {
            PriceType.FAIR_PRICE:  "fairPrice",
            PriceType.INDEX_PRICE: "indexPrice",
            PriceType.LAST_PRICE:  "lastPrice",
        }[self]

    @property
    def kline_path_segment(self) -> str:
        return {
            PriceType.INDEX_PRICE: "index_price",
            PriceType.FAIR_PRICE:  "fair_price",
            PriceType.LAST_PRICE:  "",
        }[self]


@dataclass(frozen=True, slots=True)
class ProxyConfig:
    host:     str
    port:     int
    scheme:   str
    username: Optional[str] = None
    password: Optional[str] = None

    @classmethod
    def from_url(cls, url: str) -> Optional["ProxyConfig"]:
        p = urlparse(url)
        if not p.hostname:
            return None
        return cls(
            host     = p.hostname,
            port     = p.port or 1080,
            scheme   = p.scheme or "http",
            username = p.username,
            password = p.password,
        )

    @property
    def url(self) -> str:
        auth = f"{self.username}:{self.password}@" if self.username else ""
        return f"{self.scheme}://{auth}{self.host}:{self.port}"


@dataclass(frozen=True, slots=True)
class SignatureResult:
    timestamp: str
    sign:      str


_RENAME_MAPPINGS: dict[str, dict[str, str]] = {
    "/contract/detailV2": {
        "dn": "displayName", "dne": "displayNameEn", "pot": "positionOpenType",
        "bc": "baseCoin", "qc": "quoteCoin", "bcn": "baseCoinName", "qcn": "quoteCoinName",
        "ft": "futureType", "sc": "settleCoin", "cs": "contractSize",
        "minL": "minLeverage", "maxL": "maxLeverage",
        "ccMaxL": "countryConfigContractMaxLeverage",
        "ps": "priceScale", "vs": "volScale", "as": "amountScale",
        "pu": "priceUnit", "vu": "volUnit", "minV": "minVol", "maxV": "maxVol",
        "blpr": "bidLimitPriceRate", "alpr": "askLimitPriceRate",
        "tfr": "takerFeeRate", "mfr": "makerFeeRate",
        "mmr": "maintenanceMarginRate", "imr": "initialMarginRate",
        "rbv": "riskBaseVol", "riv": "riskIncrVol", "rlss": "riskLongShortSwitch",
        "rim": "riskIncrMmr", "rii": "riskIncrImr", "rll": "riskLevelLimit",
        "pcv": "priceCoefficientVariation", "io": "indexOrigin",
        "in": "isNew", "ih": "isHot", "ihd": "isHidden", "ip": "isPromoted",
        "cp": "conceptPlate", "cpi": "conceptPlateId", "rlt": "riskLimitType",
        "mno": "maxNumOrders", "moml": "marketOrderMaxLevel",
        "moplr1": "marketOrderPriceLimitRate1", "moplr2": "marketOrderPriceLimitRate2",
        "tp": "triggerProtect", "ae": "appraisal",
        "sac": "showAppraisalCountdown", "ad": "automaticDelivery",
        "aa": "apiAllowed", "dsl": "depthStepList", "lmv": "limitMaxVol",
        "tsd": "threshold", "bciu": "baseCoinIconUrl", "bcid": "baseCoinId",
        "ct": "createTime", "ot": "openingTime", "oco": "openingCountdownOption",
        "sbo": "showBeforeOpen", "iml": "isMaxLeverage", "izfr": "isZeroFeeRate",
        "rlm": "riskLimitMode", "rlcs": "riskLimitCustom",
        "izfs": "isZeroFeeSymbol", "liqfr": "liquidationFeeRate",
        "frm": "feeRateMode", "levfrs": "leverageFeeRates", "tiefrs": "tieredFeeRates",
    },
    "/api/platform/spot/market-v2/web/symbolsV2": {
        "mcd": "marketCurrencyId", "cd": "coinId", "vn": "currency",
        "fn": "currencyFullName", "srt": "sortOrder", "sts": "status",
        "tp": "marketType", "in": "icon", "ot": "openingTime",
        "cp": "categories", "ci": "categories_ids", "ps": "priceScale",
        "qs": "quantityScale", "cdm": "contractDecimalMultiplier",
        "st": "spotEnabled", "dst": "depositStatus", "tt": "tradingType",
        "ca": "contractAddress", "fne": "currencyFullNameEn",
    },
}


def _recursive_rename(data: Any, mapping: dict[str, str]) -> Any:
    if isinstance(data, dict):
        return {mapping.get(k, k): _recursive_rename(v, mapping) for k, v in data.items()}
    if isinstance(data, list):
        return [_recursive_rename(item, mapping) for item in data]
    return data


def _apply_rename(data: Any, endpoint: str) -> Any:
    for pattern, mapping in _RENAME_MAPPINGS.items():
        if pattern in endpoint:
            return _recursive_rename(data, mapping)
    return data


class _SignatureGenerator:
    __slots__ = ("_api_key", "_last_ms", "_ms_increment")

    def __init__(self, api_key: str) -> None:
        self._api_key      = api_key
        self._last_ms      = 0
        self._ms_increment = 0

    def _unique_ms(self) -> str:
        now = int(time.time() * 1000)
        if now == self._last_ms:
            self._ms_increment += 1
        else:
            self._last_ms      = now
            self._ms_increment = 0
        return str(now + self._ms_increment)

    def generate(
        self,
        payload:     dict[str, Any],
        method:      HttpMethod,
        force_time:  Optional[str] = None,
    ) -> SignatureResult:
        ts = force_time or self._unique_ms()
        g  = hashlib.md5(f"{self._api_key}{ts}".encode()).hexdigest()[7:]

        if method.has_body:
            body = json.dumps(payload, separators=(",", ":"), ensure_ascii=False) if payload else ""
            sign = hashlib.md5(f"{ts}{body}{g}".encode()).hexdigest()
        else:
            qs   = urlencode(payload, quote_via=lambda s, *_: s) if payload else ""
            sign = hashlib.md5(f"{ts}{qs}{g}".encode()).hexdigest()

        return SignatureResult(timestamp=ts, sign=sign)

class _ResponseHandler:
    __slots__ = ("_is_testnet",)

    def __init__(self, is_testnet: bool) -> None:
        self._is_testnet = is_testnet

    def handle(self, raw: str, status: int, endpoint: Optional[str] = None) -> dict:
        if not raw:
            return self.error(status, "Request failed: empty response")
        try:
            decoded = json.loads(raw)
        except json.JSONDecodeError as exc:
            preview = raw[:200]
            return self.error(status, f"Invalid JSON: {exc} | raw: {preview}")

        if isinstance(decoded.get("data"), (dict, list)) and endpoint:
            decoded["data"] = _apply_rename(decoded["data"], endpoint)

        decoded["is_testnet"] = self._is_testnet
        return decoded

    def error(self, code: int, message: str) -> dict:
        return {
            "success":    False,
            "code":       code,
            "message":    message,
            "timestamp":  int(time.time() * 1000),
            "is_testnet": self._is_testnet,
        }


class MexcBypass:
    """
    Async MEXC web/futures API client with batch support.

    Usage (sync-friendly wrapper available via `run`):

        client = MexcBypass(api_key="...", is_testnet=False)

        # Single call
        result = asyncio.run(client.get_futures_assets({"currency": "USDT"}))

        # Parallel batch
        results = asyncio.run(client.batch({
            "positions": client.get_futures_open_positions,
            "balance":   lambda: client.get_futures_assets({"currency": "USDT"}),
        }))

        # Multi-account parallel batch
        results = asyncio.run(MexcBypass.batch_accounts(
            accounts={
                "acc1": {"api_key": "key1"},
                "acc2": {"api_key": "key2", "is_testnet": True,
                         "proxy": "socks5://user:pass@host:1080"},
            },
            callback=lambda c: {
                "positions": c.get_futures_open_positions,
                "balance":   lambda: c.get_futures_assets({"currency": "USDT"}),
            },
        ))
    """

    _BASE_URLS = {
        ApiType.GENERAL:  "https://www.mexc.com",
        ApiType.CONTRACT: "https://contract.mexc.com/api/v1",
    }

    def __init__(
        self,
        api_key:    str            = "",
        is_testnet: bool           = False,
        proxy_url:  Optional[str]  = None,
    ) -> None:
        self._api_key         = api_key
        self._is_testnet      = is_testnet
        self._proxy           = ProxyConfig.from_url(proxy_url) if proxy_url else None
        self._signer          = _SignatureGenerator(api_key)
        self._response_handler = _ResponseHandler(is_testnet)

        futures_base = (
            "https://futures.testnet.mexc.com/api/v1"
            if is_testnet
            else "https://futures.mexc.com/api/v1"
        )
        self._base_urls: dict[ApiType, str] = {
            **self._BASE_URLS,
            ApiType.FUTURES: futures_base,
            ApiType.OTHER:   "",
        }

        self._connector: Optional[aiohttp.TCPConnector] = None
        self._session:   Optional[aiohttp.ClientSession] = None

    async def _get_session(self) -> aiohttp.ClientSession:
        if self._session is None or self._session.closed:
            if self._proxy:
                if self._proxy.scheme.startswith('socks'):
                    proxy_type = ProxyType.SOCKS5 if '5' in self._proxy.scheme else ProxyType.SOCKS4
                    self._connector = ProxyConnector(
                        proxy_type=proxy_type,
                        host=self._proxy.host,
                        port=self._proxy.port,
                        username=self._proxy.username,
                        password=self._proxy.password,
                        limit=100,
                        ttl_dns_cache=600,
                        enable_cleanup_closed=True,
                    )
                else:
                    self._connector = aiohttp.TCPConnector(
                        limit=100,
                        ttl_dns_cache=600,
                        enable_cleanup_closed=True,
                    )
            else:
                self._connector = aiohttp.TCPConnector(
                    limit=100,
                    ttl_dns_cache=600,
                    enable_cleanup_closed=True,
                )
            
            self._session = aiohttp.ClientSession(
                connector=self._connector,
                timeout=aiohttp.ClientTimeout(total=5, connect=3),
            )
        return self._session


    async def close(self) -> None:
        if self._session and not self._session.closed:
            await self._session.close()

    async def __aenter__(self) -> "MexcBypass":
        return self

    async def __aexit__(self, *_: Any) -> None:
        await self.close()

    async def _request(
        self,
        method: HttpMethod,
        api_type: ApiType,
        endpoint: str,
        params: Optional[dict[str, Any]] = None,
    ) -> dict:
        params = {k: v for k, v in (params or {}).items() if v is not None and v != ""}

        sig = self._signer.generate(params, method)
        base_url = self._resolve_base(api_type, endpoint)
        origin = self._extract_origin(base_url)
        headers = self._build_headers(api_type, sig, origin)
        url = self._build_url(base_url, endpoint, method, params)

        if method.has_body and params:
            raw_body = json.dumps(params, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        elif method.has_body:
            raw_body = b""
        else:
            raw_body = None

        session = await self._get_session()

        use_proxy_params = {}
        if self._proxy and not self._proxy.scheme.startswith('socks'):
            use_proxy_params = {
                'proxy': self._proxy.url,
                'proxy_auth': aiohttp.BasicAuth(self._proxy.username, self._proxy.password or '')
                if self._proxy.username else None
            }

        try:
            async with session.request(
                method=method.value,
                url=url,
                headers=headers,
                data=raw_body,
                ssl=False,
                allow_redirects=False,
                **use_proxy_params
            ) as resp:
                raw = await resp.text()
                status = resp.status
        except aiohttp.ClientError as exc:
            return self._response_handler.error(0, f"aiohttp error: {exc}")

        return self._response_handler.handle(raw, status, endpoint)


    async def batch(
        self,
        requests: dict[str, Callable[[], Any]],
    ) -> dict[str, dict]:
        """Execute multiple coroutine-returning callables in parallel."""
        keys = list(requests.keys())

        tasks = []
        for key in keys:
            coro = requests[key]()
            task = asyncio.create_task(coro)
            tasks.append(task)
        
        results_list = await asyncio.gather(*tasks, return_exceptions=True)
        
        out: dict[str, dict] = {}
        for key, res in zip(keys, results_list):
            if isinstance(res, Exception):
                out[key] = self._response_handler.error(0, str(res))
            else:
                out[key] = res
        return out


    @staticmethod
    async def batch_accounts(
        accounts: dict[str, dict[str, Any]],
        callback: Callable[["MexcBypass"], dict[str, Callable[[], Any]]],
    ) -> dict[str, dict[str, dict]]:
        """
        Execute the same set of requests for multiple accounts in parallel.

        accounts = {
            "acc1": {"api_key": "key1"},
            "acc2": {"api_key": "key2", "is_testnet": True, "proxy": "socks5://..."},
        }
        callback = lambda c: {
            "positions": c.get_futures_open_positions,
            "balance":   lambda: c.get_futures_assets({"currency": "USDT"}),
        }
        """
        clients: dict[str, MexcBypass] = {}
        all_tasks: dict[str, dict[str, asyncio.Task]] = {}

        async def _run_account(account_key: str, cfg: dict) -> dict[str, dict]:
            client = MexcBypass(
                api_key    = cfg.get("api_key") or cfg.get("webkey", ""),
                is_testnet = cfg.get("is_testnet", False),
                proxy_url  = cfg.get("proxy"),
            )
            async with client:
                reqs = callback(client)
                keys = list(reqs.keys())
                tasks = [asyncio.create_task(reqs[k]()) for k in keys]
                results = await asyncio.gather(*tasks, return_exceptions=True)
                return {
                    k: client._response_handler.error(0, str(r)) if isinstance(r, Exception) else r
                    for k, r in zip(keys, results)
                }

        account_tasks = {
            key: asyncio.create_task(_run_account(key, cfg))
            for key, cfg in accounts.items()
        }
        results_list = await asyncio.gather(*account_tasks.values(), return_exceptions=True)

        return {
            key: (
                {"error": str(res)}
                if isinstance(res, Exception)
                else res
            )
            for key, res in zip(account_tasks.keys(), results_list)
        }

    def _resolve_base(self, api_type: ApiType, endpoint: str) -> str:
        if api_type == ApiType.OTHER:
            p = urlparse(endpoint)
            return f"https://{p.netloc}"
        return self._base_urls[api_type]

    @staticmethod
    def _extract_origin(base_url: str) -> str:
        p = urlparse(base_url)
        return f"{p.scheme}://{p.netloc}"

    def _build_headers(
        self,
        api_type: ApiType,
        sig:      SignatureResult,
        origin:   str,
    ) -> dict[str, str]:
        base = {
            "Content-Type":    "application/json",
            "Accept":          "*/*",
            "Accept-Encoding": "gzip, deflate, br",
            "User-Agent":      "Mozilla/5.0 (compatible; MEXC-API/1.0)",
            "Connection":      "keep-alive",
            "Cache-Control":   "no-cache",
            "Origin":          origin,
        }
        if api_type == ApiType.GENERAL:
            return {
                **base,
                "Cookie":        f"u_id={self._api_key}; uc_token={self._api_key};",
                "Ucenter-Token": self._api_key,
            }
        return {
            **base,
            "Authorization": self._api_key,
            "X-Mxc-Nonce":  sig.timestamp,
            "X-Mxc-Sign":   sig.sign,
        }

    @staticmethod
    def _build_url(
        base_url: str,
        endpoint: str,
        method:   HttpMethod,
        params:   dict[str, Any],
    ) -> str:
        url = base_url + endpoint
        if not method.has_body and params:
            url += "?" + urlencode(
                {k: v for k, v in params.items() if v is not None},
                quote_via=lambda s, *_: s,
            )
        return url

    @staticmethod
    def _week_range() -> dict[str, int]:
        now   = time.time()
        day   = int(now // 86400) * 86400
        # Monday = weekday 0
        import datetime
        today = datetime.date.today()
        monday = today - datetime.timedelta(days=today.weekday())
        sunday = monday + datetime.timedelta(days=6)
        return {
            "start": int(datetime.datetime.combine(monday, datetime.time.min).timestamp()) * 1000,
            "end":   int(datetime.datetime.combine(sunday, datetime.time.max).timestamp() * 1000),
        }

    @staticmethod
    def _normalize_volume(raw: float, cd: dict) -> float:
        import math
        scale = int(cd.get("volScale") or 0)
        unit  = float(cd.get("volUnit") or 0)
        v     = round(raw, scale)
        if unit > 0:
            v = math.floor(v / unit) * unit
        return float(max(float(cd.get("minVol") or 0), min(v, float(cd.get("maxVol") or float("inf")))))

    @staticmethod
    def _volume_payload(
        cd:         dict,
        volume:     float,
        leverage:   int,
        price:      float,
        usdt_value: float,
        pt:         PriceType,
    ) -> dict:
        return {
            "usdt_value":   usdt_value,
            "volume":       volume,
            "leverage":     leverage,
            "price":        price,
            "min_volume":   cd.get("minVol"),
            "max_volume":   cd.get("maxVol"),
            "min_leverage": cd.get("minLeverage"),
            "max_leverage": cd.get("maxLeverage"),
            "price_type":   pt.value,
            "volume_scale": cd.get("volScale"),
            "volume_unit":  cd.get("volUnit"),
            "price_scale":  cd.get("priceScale"),
            "price_unit":   cd.get("priceUnit"),
        }

    def _error(self, code: int, message: str) -> dict:
        return self._response_handler.error(code, message)

    def _parse_symbol(self, params: dict) -> tuple[str, str]:
        if params.get("symbol"):
            parts = params["symbol"].split("_", 1)
            return parts[0], parts[1] if len(parts) > 1 else ""
        return params.get("base_coin", ""), params.get("quote_coin", "")

    async def _fetch_ticker_and_contract(self, symbol: str) -> dict:
        result = await self.batch({
            "ticker":   lambda: self.get_futures_tickers({"symbol": symbol}),
            "contract": lambda: self.get_futures_contracts({"symbol": symbol}),
        })
        if not result["ticker"].get("success") or not result["ticker"].get("data"):
            return {"ok": False, "error": self._error(400, "Failed to get ticker data")}
        if not result["contract"].get("success") or not result["contract"].get("data", [None])[0]:
            return {"ok": False, "error": self._error(400, "Failed to get contract data")}
        return {"ok": True, "result": result}

    def _resolve_market(self, market: dict, params: dict) -> dict:
        pt = PriceType(params.get("price_type", PriceType.LAST_PRICE.value))
        return {
            "ticker":   market["result"]["ticker"]["data"].get(pt.ticker_field),
            "contract": market["result"]["contract"]["data"][0],
            "pt":       pt,
        }

    async def get_server_time(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.CONTRACT, "/contract/ping")

    async def ping(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.GENERAL, "/api/common/ping")

    async def validation(self) -> dict:
        return await self._request(HttpMethod.POST, ApiType.GENERAL, "/ucenter/api/login/validation")

    async def get_ws_token(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.GENERAL, "/ucenter/api/ws_token")

    async def get_customer_info(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.GENERAL, "/ucenter/api/customer_info")

    async def get_user_info(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.GENERAL, "/ucenter/api/user_info")

    async def get_latest_deposits(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.GENERAL, "/api/platform/deposit/v3/latest")

    async def get_deposit_addresses(self, params: dict) -> dict:
        if "currency" in params:
            endpoint = f"/api/platform/asset/api/asset/spot/currency/v3?currency={params['currency']}"
        else:
            endpoint = f"/api/platform/asset/api/deposit/address/list?vcoinId={params['vcoin_id']}"
        return await self._request(HttpMethod.GET, ApiType.GENERAL, endpoint)

    async def get_secure_info(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.GENERAL, "/ucenter/api/secure/check")

    async def get_origin_info(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.GENERAL, "/ucenter/api/origin_info")

    async def logout(self) -> dict:
        return await self._request(HttpMethod.POST, ApiType.GENERAL, "/ucenter/api/logout")

    async def get_assets_overview(self, params: dict = {}) -> dict:
        endpoint = (
            "/api/platform/asset/api/asset/overview/convert/v2"
            if params.get("convert")
            else "/api/platform/asset/api/asset/overview/v2"
        )
        return await self._request(HttpMethod.GET, ApiType.GENERAL, endpoint)

    async def get_market_symbols(self, params: dict = {}) -> dict:
        response = await self._request(
            HttpMethod.GET, ApiType.GENERAL,
            "/api/platform/spot/market-v2/web/symbolsV2",
        )
        if not params:
            return response

        base_coin, quote_coin = self._parse_symbol(params)
        if not base_coin:
            return response

        symbols = response.get("data", {}).get("symbols", {}).get(quote_coin)
        if not symbols:
            return self._error(400, f"No {quote_coin} symbols found")

        search  = base_coin.upper()
        matches = [s for s in symbols if s.get("currency", "").upper() == search]

        if not matches:
            return self._error(404, "No matching tokens found")

        return {"success": True, "count": len(matches), "data": matches, "timestamp": int(time.time())}

    async def get_spot_order_book(self, params: dict) -> dict:
        symbols = await self.get_market_symbols({"symbol": params["symbol"]})
        if not symbols.get("success"):
            return symbols
        return await self._request(HttpMethod.GET, ApiType.GENERAL,
            "/api/platform/spot/market-v2/web/depth/v2", {
                "symbolId": symbols["data"][0]["id"],
                "decimal":  params.get("decimal", "0.0000001"),
                "count":    params.get("count", 100),
            })

    async def create_spot_order(self, params: dict) -> dict:
        path    = (
            "/api/platform/spot/order/place"
            if params["type"] == "LIMIT_ORDER"
            else "/api/platform/spot/v4/order/place"
        )
        symbols = await self.get_market_symbols({"symbol": params["symbol"]})
        if not symbols.get("success"):
            return symbols
        return await self._request(HttpMethod.POST, ApiType.GENERAL, path, {
            "orderType":        params["type"],
            "tradeType":        params["side"],
            "price":            params.get("price"),
            "amount":           params.get("amount"),
            "quantity":         params.get("quantity"),
            "marketCurrencyId": symbols["data"][0]["marketCurrencyId"],
            "currencyId":       symbols["data"][0]["coinId"],
            "orderSource":      "WEB",
            "ts":               int(time.time() * 1000),
        })

    async def cancel_spot_order(self, params: dict) -> dict:
        return await self._request(HttpMethod.DELETE, ApiType.GENERAL,
            "/api/platform/spot/order/cancel/v2", {"orderId": params["order_id"]})

    async def get_referrals_list(self, params: dict = {}) -> dict:
        week = self._week_range()
        return await self._request(HttpMethod.GET, ApiType.GENERAL,
            "/api/assetbussiness/invite/invites", {
                "startTime": params.get("start_time", week["start"]),
                "endTime":   params.get("end_time",   week["end"]),
                "page":      params.get("page_num",   1),
                "pageSize":  params.get("page_size",  10),
            })

    async def get_futures_fee_rate(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/account/contract/fee_rate")

    async def get_futures_zero_fee_rate(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/account/contract/zero_fee_rate")

    async def get_futures_today_pnl(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/account/asset/analysis/today_pnl")

    async def get_futures_analysis(self, params: dict = {}) -> dict:
        now        = int(time.time() * 1000)
        end_time   = min(params.get("end_time", now), now)
        start_time = params.get("start_time", end_time - 7 * 86_400 * 1000)
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/account/asset/analysis/v3", {
            "currency":               params.get("currency", "USDT"),
            "symbol":                 params.get("symbol"),
            "include_unrealised_pnl": params.get("include_unrealised_pnl", 0),
            "reverse":                params.get("reverse", 0),
            "startTime":              start_time if start_time < end_time else end_time - 1000,
            "endTime":                end_time,
        })

    async def get_futures_assets(self, params: dict = {}) -> dict:
        endpoint = (
            f"/private/account/asset/{params['currency']}"
            if "currency" in params
            else "/private/account/assets"
        )
        return await self._request(HttpMethod.GET, ApiType.FUTURES, endpoint)

    async def get_futures_asset_transfer_records(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/account/transfer_record", {
            "currency":  params.get("currency"),
            "state":     params.get("state"),
            "type":      params.get("type"),
            "page_num":  params.get("page_num",  1),
            "page_size": params.get("page_size", 20),
        })

    async def get_futures_risk_limits(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/account/risk_limit", {
            "symbol": params.get("symbol"),
        })

    async def get_futures_funding_rate(self, params: dict) -> dict:
        return await self._request(HttpMethod.GET, ApiType.CONTRACT, f"/contract/funding_rate/{params['symbol']}")

    async def get_futures_contracts(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/contract/detailV2", {
            "symbol": params.get("symbol"),
        })

    async def get_futures_contract_index_price(self, params: dict) -> dict:
        return await self._request(HttpMethod.GET, ApiType.CONTRACT, f"/contract/index_price/{params['symbol']}")

    async def get_futures_contract_fair_price(self, params: dict) -> dict:
        return await self._request(HttpMethod.GET, ApiType.CONTRACT, f"/contract/fair_price/{params['symbol']}")

    async def get_futures_contract_kline_data(self, params: dict) -> dict:
        pt      = PriceType(params.get("price_type", PriceType.LAST_PRICE.value))
        segment = pt.kline_path_segment
        path    = (
            f"/contract/kline/{segment}/{params['symbol']}"
            if segment
            else f"/contract/kline/{params['symbol']}"
        )
        return await self._request(HttpMethod.GET, ApiType.CONTRACT, path, {
            "interval": params.get("interval", "Min1"),
            "start":    params.get("start_time"),
            "end":      params.get("end_time"),
        })

    async def get_futures_tickers(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/contract/ticker", {
            "symbol": params.get("symbol"),
        })

    async def get_futures_open_positions(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/position/open_positions", {
            k: v for k, v in {
                "symbol":     params.get("symbol"),
                "positionId": params.get("position_id"),
            }.items() if v is not None
        })

    async def get_futures_positions_history(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/position/list/history_positions", {
            "symbol":    params.get("symbol"),
            "type":      params.get("type"),
            "page_num":  params.get("page_num",  1),
            "page_size": params.get("page_size", 20),
        })

    async def close_all_futures_positions(self) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/position/close_all")

    async def reverse_futures_position(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/position/reverse", {
            "positionId": params["position_id"],
            "symbol":     params["symbol"],
            "vol":        params["vol"],
        })

    async def get_futures_position_mode(self) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/position/position_mode")

    async def change_futures_position_mode(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/position/change_position_mode", {
            "positionMode": params["position_mode"],
        })

    async def get_futures_leverage(self, params: dict) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/position/leverage", {
            "symbol": params["symbol"],
        })

    async def change_futures_position_margin(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/position/change_margin", {
            "positionId": params["position_id"],
            "amount":     params["amount"],
            "type":       params["type"],
        })

    async def change_futures_position_leverage(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/position/change_leverage", {
            "positionId":   params["position_id"],
            "leverage":     params["leverage"],
            "openType":     params.get("open_type"),
            "symbol":       params.get("symbol"),
            "positionType": params.get("position_type"),
        })

    async def get_futures_orders_deals(self, params: dict) -> dict:
        week = self._week_range()
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/order/list/order_deals", {
            "symbol":     params["symbol"],
            "start_time": params.get("start_time", week["start"]),
            "end_time":   params.get("end_time",   week["end"]),
            "page_num":   params.get("page_num",   1),
            "page_size":  params.get("page_size",  20),
        })

    async def get_futures_pending_orders(self, params: dict = {}) -> dict:
        endpoint = (
            f"/private/order/list/open_orders/{params['symbol']}"
            if "symbol" in params
            else "/private/order/list/open_orders/"
        )
        return await self._request(HttpMethod.GET, ApiType.FUTURES, endpoint, {
            "page_num":  params.get("page_num",  1),
            "page_size": params.get("page_size", 20),
        })

    async def get_futures_orders_history(self, params: dict = {}) -> dict:
        week = self._week_range()
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/order/list/history_orders", {
            "symbol":     params.get("symbol"),
            "states":     params.get("states"),
            "category":   params.get("category"),
            "side":       params.get("side"),
            "start_time": params.get("start_time", week["start"]),
            "end_time":   params.get("end_time",   week["end"]),
            "page_num":   params.get("page_num",   1),
            "page_size":  params.get("page_size",  20),
        })

    async def get_futures_open_limit_orders(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/order/list/open_orders", {
            "page_size": params.get("page_size", 200),
        })

    async def get_futures_open_stop_orders(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/stoporder/open_orders", {
            "page_size": params.get("page_size", 200),
        })

    async def get_futures_open_orders(self, params: dict = {}) -> dict:
        page_size = params.get("page_size", 200)
        result    = await self.batch({
            "limit_orders": lambda: self.get_futures_open_limit_orders({"page_size": page_size}),
            "stop_orders":  lambda: self.get_futures_open_stop_orders({"page_size": page_size}),
        })
        merged = [
            *(result["limit_orders"].get("data", []) if result["limit_orders"].get("success") else []),
            *(result["stop_orders"].get("data",  []) if result["stop_orders"].get("success")  else []),
        ]
        return {"success": True, "code": 0, "data": merged}

    async def get_futures_closed_orders(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/order/close_orders", {
            "symbol":    params.get("symbol"),
            "category":  params.get("category"),
            "page_num":  params.get("page_num",  1),
            "page_size": params.get("page_size", 20),
        })

    async def get_futures_orders_by_id(self, params: dict) -> dict:
        ids = [s.strip() for s in str(params["ids"]).split(",")]
        if len(ids) > 1:
            return await self._request(HttpMethod.GET, ApiType.FUTURES,
                "/private/order/batch_query", {"order_ids": params["ids"]})
        return await self._request(HttpMethod.GET, ApiType.FUTURES, f"/private/order/get/{params['ids']}")

    async def create_futures_order(self, params: dict) -> dict:
        payload = {
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
            "profitTrend":     params.get("take_profit_trend", 1),
            "stopLossPrice":   params.get("stop_loss_price"),
            "lossTrend":       params.get("stop_loss_trend", 1),
            "priceProtect":    params.get("price_protect", 0),
            "reduceOnly":      params.get("reduce_only", False),
            "marketCeiling":   params.get("market_ceiling", False),
            "flashClose":      params.get("flash_close"),
            "bboTypeNum":      params.get("bbo_type"),
        }
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/order/create",
            {k: v for k, v in payload.items() if v is not None})

    async def cancel_futures_orders(self, params: dict) -> dict:
        ids = (
            params["ids"]
            if isinstance(params["ids"], list)
            else [s.strip() for s in str(params["ids"]).split(",")]
        )
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/order/cancel", ids)

    async def cancel_futures_order_with_external_id(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/order/cancel_with_external", {
            "symbol":      params["symbol"],
            "externalOid": params["external_id"],
        })

    async def cancel_all_futures_orders(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/order/cancel_all", {
            "symbol": params.get("symbol"),
        })

    async def change_futures_limit_order_price(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "private/order/change_limit_order", {
            "orderId": params["order_id"],
            "price":   params["price"],
            "vol":     params["vol"],
        })

    async def chase_futures_order(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/order/chase_limit_order", {
            "orderId": params["order_id"],
        })

    async def create_futures_chase_order(self, params: dict) -> dict:
        payload = {
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
        }
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/order/chase_limit/place",
            {k: v for k, v in payload.items() if v is not None})

    async def create_futures_stop_order(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/place/v2", {
            "positionId":           params["position_id"],
            "volType":              params.get("vol_type", 2),
            "vol":                  params.get("vol"),
            "takeProfitType":       params.get("take_profit_type"),
            "takeProfitOrderPrice": params.get("take_profit_order_price"),
            "takeProfitPrice":      params.get("take_profit_price"),
            "takeProfitReverse":    params.get("take_profit_reverse", 2),
            "profitTrend":          params.get("take_profit_trend", 1),
            "takeProfitVol":        params.get("take_profit_volume"),
            "stopLossType":         params.get("stop_loss_type"),
            "stopLossOrderPrice":   params.get("stop_loss_order_price"),
            "stopLossPrice":        params.get("stop_loss_price"),
            "stopLossReverse":      params.get("stop_loss_reverse", 2),
            "lossTrend":            params.get("stop_loss_trend", 1),
            "profitLossVolType":    params.get("profit_loss_vol_type", "SAME"),
            "priceProtect":         params.get("price_protect", 0),
        })

    async def get_futures_stop_limit_orders(self, params: dict = {}) -> dict:
        week = self._week_range()
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/stoporder/list/orders", {
            "symbol":      params.get("symbol"),
            "is_finished": params.get("is_finished"),
            "start_time":  params.get("start_time", week["start"]),
            "end_time":    params.get("end_time",   week["end"]),
            "page_num":    params.get("page_num",   1),
            "page_size":   params.get("page_size",  20),
        })

    async def cancel_stop_limit_orders(self, params: dict) -> dict:
        ids = (
            [s.strip() for s in str(params["ids"]).split(",") if s.strip()]
            if isinstance(params["ids"], str)
            else params["ids"]
        )
        payload = [{"stopPlanOrderId": int(i)} for i in ids]
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/cancel", payload)

    async def cancel_all_futures_stop_limit_orders(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/cancel_all", {
            "positionId": params.get("position_id"),
            "symbol":     params.get("symbol"),
        })

    async def change_futures_order_stop_limit_price(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/change_price", {
            "orderId":         params["order_id"],
            "takeProfitPrice": params.get("take_profit_price"),
            "stopLossPrice":   params.get("stop_loss_price"),
        })

    async def change_futures_plan_order_stop_limit_price(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/change_plan_price", {
            "stopPlanOrderId": params["order_id"],
            "takeProfitPrice": params.get("take_profit_price"),
            "stopLossPrice":   params.get("stop_loss_price"),
        })

    async def change_futures_order_targets(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/stoporder/change_plan_order", {
            "orderId":          params["real_order_id"],
            "profitTrend":      params.get("take_profit_trend"),
            "takeProfitPrice":  params.get("take_profit_price"),
            "takeProfitVolume": params.get("take_profit_volume"),
            "lossTrend":        params.get("stop_loss_trend"),
            "stopLossPrice":    params.get("stop_loss_price"),
            "stopLossVolume":   params.get("stop_loss_volume"),
        })

    async def create_futures_trigger_order(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/planorder/place", {
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
        })

    async def get_futures_trigger_orders(self, params: dict = {}) -> dict:
        week = self._week_range()
        return await self._request(HttpMethod.GET, ApiType.FUTURES, "/private/planorder/list/orders", {
            "symbol":     params.get("symbol"),
            "states":     params.get("states"),
            "start_time": params.get("start_time", week["start"]),
            "end_time":   params.get("end_time",   week["end"]),
            "page_num":   params.get("page_num",   1),
            "page_size":  params.get("page_size",  20),
        })

    async def cancel_futures_trigger_orders(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/planorder/cancel", params["ids"])

    async def cancel_all_futures_trigger_orders(self, params: dict = {}) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/planorder/cancel_all", {
            "symbol": params.get("symbol"),
        })

    async def create_futures_trailing_order(self, params: dict) -> dict:
        payload = {
            "symbol":       params["symbol"],
            "leverage":     params["leverage"],
            "side":         params["side"],
            "vol":          params["vol"],
            "openType":     params["open_type"],
            "trend":        params["trend"],
            "activePrice":  params["active_price"],
            "backType":     params["back_type"],
            "backValue":    params["back_value"],
            "positionMode": params.get("position_mode", 0),
            "reduceOnly":   params.get("reduce_only"),
        }
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/trackorder/place",
            {k: v for k, v in payload.items() if v is not None})

    async def cancel_futures_trailing_order(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/trackorder/cancel", {
            "symbol":       params.get("symbol"),
            "trackOrderId": params.get("order_id"),
        })

    async def change_futures_trailing_order(self, params: dict) -> dict:
        return await self._request(HttpMethod.POST, ApiType.FUTURES, "/private/trackorder/change_order", {
            "symbol":       params["symbol"],
            "trackOrderId": params["order_id"],
            "trend":        params["trend"],
            "activePrice":  params["active_price"],
            "backType":     params["back_type"],
            "backValue":    params["back_value"],
            "vol":          params["vol"],
        })

    async def calculate_futures_position_pnl(self, params: dict) -> dict:
        result   = await self.batch({
            "contract": lambda: self.get_futures_contracts({"symbol": params["symbol"]}),
            "ticker":   lambda: self.get_futures_tickers({"symbol": params["symbol"]}),
        })
        contract = result["contract"].get("data", [None])[0]
        ticker   = result["ticker"].get("data")

        if not contract or not ticker or not float(contract.get("contractSize") or 0):
            return self._error(-1, "Unable to calculate PnL: missing market data")

        pt             = PriceType(params.get("price_type", PriceType.LAST_PRICE.value))
        price          = float(ticker.get(pt.ticker_field) or 0)
        entry          = float(params["entry_price"])
        volume         = float(params["volume"])
        leverage       = max(1, int(params.get("leverage", 1)))
        cs             = float(contract["contractSize"])
        is_long        = int(params["side"]) == 1
        pnl            = (price - entry if is_long else entry - price) * volume * cs
        initial_margin = entry * volume * cs / leverage

        return {
            "success": True,
            "code":    0,
            "data": {
                "pnl":           round(pnl, 4),
                "pnl_percent":   round(pnl / initial_margin * 100, 2) if initial_margin > 0 else 0,
                "volume":        volume,
                "entry_price":   entry,
                "current_price": price,
                "price_type":    pt.value,
            },
        }

    async def calculate_futures_volume(self, params: dict) -> dict:
        for req in ("symbol", "amount", "leverage"):
            if not params.get(req):
                return self._error(404, f"Missing required parameter: {req}")

        market = await self._fetch_ticker_and_contract(params["symbol"])
        if not market["ok"]:
            return market["error"]

        resolved = self._resolve_market(market, params)
        ticker, cd, pt = resolved["ticker"], resolved["contract"], resolved["pt"]

        if ticker is None:                                         return self._error(400, "Invalid price value")
        if float(params["amount"]) <= 0:                          return self._error(400, "Amount must be positive")
        if float(params["leverage"]) <= 0:                        return self._error(400, "Leverage must be positive")
        if not float(cd.get("contractSize") or 0):                return self._error(400, "Invalid contract size")

        price    = float(ticker)
        leverage = int(max(cd["minLeverage"], min(params["leverage"], cd["maxLeverage"])))
        volume   = self._normalize_volume(
            float(params["amount"]) * leverage / (price * float(cd["contractSize"])), cd
        )
        usdt = round(volume * price * float(cd["contractSize"]) / leverage, int(cd.get("priceScale") or 0))

        return {"success": True, "code": 0, "data": self._volume_payload(cd, volume, leverage, price, usdt, pt)}

    async def calculate_futures_volume_from_base_amount(self, params: dict) -> dict:
        for req in ("symbol", "amount", "leverage"):
            if not params.get(req):
                return self._error(404, f"Missing required parameter: {req}")

        market = await self._fetch_ticker_and_contract(params["symbol"])
        if not market["ok"]:
            return market["error"]

        resolved = self._resolve_market(market, params)
        ticker, cd, pt = resolved["ticker"], resolved["contract"], resolved["pt"]

        if ticker is None:                                         return self._error(400, "Invalid price value")
        if float(params["amount"]) <= 0:                          return self._error(400, "Base amount must be positive")
        if float(params["leverage"]) <= 0:                        return self._error(400, "Leverage must be positive")
        if not float(cd.get("contractSize") or 0):                return self._error(400, "Invalid contract size")

        price    = float(ticker)
        leverage = int(max(cd["minLeverage"], min(params["leverage"], cd["maxLeverage"])))
        volume   = self._normalize_volume(float(params["amount"]) / float(cd["contractSize"]), cd)
        notional = float(params["amount"]) * price
        usdt     = round(notional, int(cd.get("priceScale") or 0))

        return {
            "success": True,
            "code":    0,
            "data": {
                **self._volume_payload(cd, volume, leverage, price, usdt, pt),
                "required_margin": round(notional / leverage, int(cd.get("priceScale") or 0)),
                "amount":          params["amount"],
                "notional_value":  notional,
                "contract_size":   cd["contractSize"],
            },
        }

    async def receive_futures_testnet_asset(self, params: dict = {}) -> dict:
        return await self._request(
            HttpMethod.POST,
            ApiType.OTHER,
            "https://futures.testnet.mexc.com/mock/contract/asset/receive",
            {
                "currency": params.get("currency", "USDT"),
                "amount":   params.get("amount"),
            },
        )


def run(coro):
    """Run an async coroutine from synchronous code."""
    return asyncio.run(coro)