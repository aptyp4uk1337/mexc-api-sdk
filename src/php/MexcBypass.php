<?php

declare(strict_types=1);

enum ApiType: string
{
  case General  = 'general';
  case Futures  = 'futures';
  case Contract = 'contract';
  case Other    = 'other';
}

enum HttpMethod: string
{
  case GET    = 'GET';
  case POST   = 'POST';
  case PUT    = 'PUT';
  case DELETE = 'DELETE';

  public function hasBody(): bool
  {
    return match ($this) {
      self::POST, self::PUT => true,
      default               => false,
    };
  }
}

enum PriceType: string
{
  case LastPrice  = 'last_price';
  case FairPrice  = 'fair_price';
  case IndexPrice = 'index_price';

  public function tickerField(): string
  {
    return match ($this) {
      self::FairPrice  => 'fairPrice',
      self::IndexPrice => 'indexPrice',
      self::LastPrice  => 'lastPrice',
    };
  }

  public function klinePathSegment(): string
  {
    return match ($this) {
      self::IndexPrice => 'index_price',
      self::FairPrice  => 'fair_price',
      self::LastPrice  => '',
    };
  }
}

final readonly class ProxyConfig
{
  public function __construct(
    public string  $host,
    public int     $port,
    public int     $type,
    public ?string $auth = null,
  ) {}

  public static function fromUrl(string $url): ?self
  {
    $p = parse_url($url);
    if (!$p || empty($p['host'])) {
      return null;
    }

    return new self(
      host: $p['host'],
      port: $p['port'] ?? 1080,
      type: match (strtolower($p['scheme'] ?? '')) {
        'socks5'        => CURLPROXY_SOCKS5,
        'http', 'https' => CURLPROXY_HTTP,
        default         => CURLPROXY_HTTP,
      },
      auth: isset($p['user']) ? "{$p['user']}:{$p['pass']}" : null,
    );
  }
}

final readonly class SignatureResult
{
  public function __construct(
    public string $timestamp,
    public string $sign,
  ) {}
}

final readonly class PendingRequest
{
  public string $id;

  public function __construct(
    public string     $url,
    public HttpMethod $method,
    public array      $headers,
    public array      $body,
    public string     $endpoint,
  ) {
    $this->id = uniqid('req_', true);
  }
}

final class SignatureGenerator
{
  private int $lastMs      = 0;
  private int $msIncrement = 0;

  public function __construct(private readonly string $apiKey) {}

  public function generate(array $payload, HttpMethod $method, ?string $forceTime = null): SignatureResult
  {
    $time = $forceTime ?? $this->uniqueMs();
    $g    = substr(md5($this->apiKey . $time), 7);

    if ($method->hasBody()) {
      $body = empty($payload) ? '' : json_encode($payload, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
      $sign = md5($time . $body . $g);
    } else {
      $sign = md5($time . http_build_query($payload, '', '&', PHP_QUERY_RFC3986) . $g);
    }

    return new SignatureResult($time, $sign);
  }

  private function uniqueMs(): string
  {
    $now = (int) (microtime(true) * 1000);

    if ($now === $this->lastMs) {
      $this->msIncrement++;
    } else {
      $this->lastMs      = $now;
      $this->msIncrement = 0;
    }

    return (string) ($now + $this->msIncrement);
  }
}

final class CurlFactory
{
  private const BASE_OPTIONS = [
    CURLOPT_RETURNTRANSFER    => true,
    CURLOPT_TIMEOUT           => 5,
    CURLOPT_CONNECTTIMEOUT    => 3,
    CURLOPT_SSL_VERIFYPEER    => false,
    CURLOPT_SSL_VERIFYHOST    => false,
    CURLOPT_ENCODING          => 'gzip, deflate, br, zstd',
    CURLOPT_FOLLOWLOCATION    => false,
    CURLOPT_MAXREDIRS         => 0,
    CURLOPT_FORBID_REUSE      => false,
    CURLOPT_FRESH_CONNECT     => false,
    CURLOPT_TCP_KEEPALIVE     => 1,
    CURLOPT_TCP_KEEPIDLE      => 60,
    CURLOPT_TCP_KEEPINTVL     => 30,
    CURLOPT_DNS_CACHE_TIMEOUT => 600,
    CURLOPT_HTTP_VERSION      => CURL_HTTP_VERSION_2_0,
    CURLOPT_PIPEWAIT          => true,
    CURLOPT_BUFFERSIZE        => 131072,
    CURLOPT_NOSIGNAL          => 1,
  ];

  private static ?\CurlShareHandle $shareHandle = null;

  public function __construct(private readonly ?ProxyConfig $proxy = null) {}

  private static function getShareHandle(): \CurlShareHandle
  {
    if (self::$shareHandle === null) {
      $sh = curl_share_init();
      curl_share_setopt($sh, CURLSHOPT_SHARE, CURL_LOCK_DATA_DNS);
      curl_share_setopt($sh, CURLSHOPT_SHARE, CURL_LOCK_DATA_SSL_SESSION);
      curl_share_setopt($sh, CURLSHOPT_SHARE, CURL_LOCK_DATA_CONNECT);
      self::$shareHandle = $sh;
    }

    return self::$shareHandle;
  }

  public function make(string $url, HttpMethod $method, array $headers, ?array $body = null): \CurlHandle
  {
    $ch = curl_init();

    curl_setopt_array($ch, self::BASE_OPTIONS + [
      CURLOPT_URL           => $url,
      CURLOPT_CUSTOMREQUEST => $method->value,
      CURLOPT_HTTPHEADER    => $headers,
      CURLOPT_SHARE         => self::getShareHandle(),
    ]);

    if ($body !== null && ($method->hasBody() || $method === HttpMethod::DELETE)) {
      curl_setopt($ch, CURLOPT_POSTFIELDS, json_encode($body, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE));
    }

    if ($this->proxy !== null) {
      $this->applyProxy($ch);
    }

    return $ch;
  }

  private function applyProxy(\CurlHandle $ch): void
  {
    curl_setopt_array($ch, [
      CURLOPT_PROXY     => $this->proxy->host,
      CURLOPT_PROXYPORT => $this->proxy->port,
      CURLOPT_PROXYTYPE => $this->proxy->type,
    ]);

    if ($this->proxy->auth !== null) {
      curl_setopt_array($ch, [
        CURLOPT_PROXYAUTH    => CURLAUTH_BASIC,
        CURLOPT_PROXYUSERPWD => $this->proxy->auth,
      ]);
    }
  }
}

final class ResponseHandler
{
  /** @var array<string, array<string, string>> */
  private const RENAME_MAPPINGS = [
    '/contract/detailV2' => [
      'dn'     => 'displayName',
      'dne'    => 'displayNameEn',
      'pot'    => 'positionOpenType',
      'bc'     => 'baseCoin',
      'qc'     => 'quoteCoin',
      'bcn'    => 'baseCoinName',
      'qcn'    => 'quoteCoinName',
      'ft'     => 'futureType',
      'sc'     => 'settleCoin',
      'cs'     => 'contractSize',
      'minL'   => 'minLeverage',
      'maxL'   => 'maxLeverage',
      'ccMaxL' => 'countryConfigContractMaxLeverage',
      'ps'     => 'priceScale',
      'vs'     => 'volScale',
      'as'     => 'amountScale',
      'pu'     => 'priceUnit',
      'vu'     => 'volUnit',
      'minV'   => 'minVol',
      'maxV'   => 'maxVol',
      'blpr'   => 'bidLimitPriceRate',
      'alpr'   => 'askLimitPriceRate',
      'tfr'    => 'takerFeeRate',
      'mfr'    => 'makerFeeRate',
      'mmr'    => 'maintenanceMarginRate',
      'imr'    => 'initialMarginRate',
      'rbv'    => 'riskBaseVol',
      'riv'    => 'riskIncrVol',
      'rlss'   => 'riskLongShortSwitch',
      'rim'    => 'riskIncrMmr',
      'rii'    => 'riskIncrImr',
      'rll'    => 'riskLevelLimit',
      'pcv'    => 'priceCoefficientVariation',
      'io'     => 'indexOrigin',
      'in'     => 'isNew',
      'ih'     => 'isHot',
      'ihd'    => 'isHidden',
      'ip'     => 'isPromoted',
      'cp'     => 'conceptPlate',
      'cpi'    => 'conceptPlateId',
      'rlt'    => 'riskLimitType',
      'mno'    => 'maxNumOrders',
      'moml'   => 'marketOrderMaxLevel',
      'moplr1' => 'marketOrderPriceLimitRate1',
      'moplr2' => 'marketOrderPriceLimitRate2',
      'tp'     => 'triggerProtect',
      'ae'     => 'appraisal',
      'sac'    => 'showAppraisalCountdown',
      'ad'     => 'automaticDelivery',
      'aa'     => 'apiAllowed',
      'dsl'    => 'depthStepList',
      'lmv'    => 'limitMaxVol',
      'tsd'    => 'threshold',
      'bciu'   => 'baseCoinIconUrl',
      'bcid'   => 'baseCoinId',
      'ct'     => 'createTime',
      'ot'     => 'openingTime',
      'oco'    => 'openingCountdownOption',
      'sbo'    => 'showBeforeOpen',
      'iml'    => 'isMaxLeverage',
      'izfr'   => 'isZeroFeeRate',
      'rlm'    => 'riskLimitMode',
      'rlcs'   => 'riskLimitCustom',
      'izfs'   => 'isZeroFeeSymbol',
      'liqfr'  => 'liquidationFeeRate',
      'frm'    => 'feeRateMode',
      'levfrs' => 'leverageFeeRates',
      'tiefrs' => 'tieredFeeRates',
    ],
    '/api/platform/spot/market-v2/web/symbolsV2' => [
      'mcd' => 'marketCurrencyId',
      'cd'  => 'coinId',
      'vn'  => 'currency',
      'fn'  => 'currencyFullName',
      'srt' => 'sortOrder',
      'sts' => 'status',
      'tp'  => 'marketType',
      'in'  => 'icon',
      'ot'  => 'openingTime',
      'cp'  => 'categories',
      'ci'  => 'categories_ids',
      'ps'  => 'priceScale',
      'qs'  => 'quantityScale',
      'cdm' => 'contractDecimalMultiplier',
      'st'  => 'spotEnabled',
      'dst' => 'depositStatus',
      'tt'  => 'tradingType',
      'ca'  => 'contractAddress',
      'fne' => 'currencyFullNameEn',
    ],
  ];

  public function __construct(private readonly bool $isTestnet) {}

  public function handle(string|false $raw, int $status, ?string $endpoint = null): array
  {
    if ($raw === false || $raw === '') {
      return $this->error($status, 'Request failed: empty response');
    }

    // Попытка декодировать; без JSON_THROW_ON_ERROR — проверяем вручную
    $decoded = json_decode($raw, true);

    if ($decoded === null || json_last_error() !== JSON_ERROR_NONE) {
      // Отладочная информация: первые 200 символов сырого ответа
      $preview = mb_substr($raw, 0, 200);
      return $this->error($status, 'Invalid JSON: ' . json_last_error_msg() . ' | raw: ' . $preview);
    }

    if (isset($decoded['data']) && is_array($decoded['data']) && $endpoint !== null) {
      $decoded['data'] = $this->renameFields($decoded['data'], $endpoint);
    }

    $decoded['is_testnet'] = $this->isTestnet;
    return $decoded;
  }

  public function error(int $code, string $message): array
  {
    return [
      'success'    => false,
      'code'       => $code,
      'message'    => $message,
      'timestamp'  => (int)(microtime(true) * 1000),
      'is_testnet' => $this->isTestnet,
    ];
  }

  private function renameFields(array $data, string $endpoint): array
  {
    foreach (self::RENAME_MAPPINGS as $pattern => $map) {
      if (str_contains($endpoint, $pattern)) {
        return $this->recursiveRename($data, $map);
      }
    }
    return $data;
  }

  private function recursiveRename(array $arr, array $map): array
  {
    $out = [];
    foreach ($arr as $k => $v) {
      $out[$map[$k] ?? $k] = is_array($v) ? $this->recursiveRename($v, $map) : $v;
    }
    return $out;
  }
}

final class MexcBypass
{
  private readonly SignatureGenerator $signer;
  private readonly CurlFactory        $curlFactory;
  private readonly ResponseHandler    $responseHandler;

  /** @var array<string, string> */
  private readonly array $baseUrls;

  private bool  $batchMode       = false;
  /** @var array<string, PendingRequest> */
  private array $pendingRequests = [];

  public function __construct(
    private readonly string $apiKey    = '',
    private readonly bool   $isTestnet = false,
    ?string                 $proxyUrl  = null,
  ) {
    $proxy                 = $proxyUrl !== null ? ProxyConfig::fromUrl($proxyUrl) : null;
    $this->signer          = new SignatureGenerator($this->apiKey);
    $this->curlFactory     = new CurlFactory($proxy);
    $this->responseHandler = new ResponseHandler($this->isTestnet);
    $this->baseUrls        = [
      ApiType::General->value  => 'https://www.mexc.com',
      ApiType::Futures->value  => $this->isTestnet
        ? 'https://futures.testnet.mexc.com/api/v1'
        : 'https://futures.mexc.com/api/v1',
      ApiType::Contract->value => 'https://contract.mexc.com/api/v1',
      ApiType::Other->value    => '',
    ];
  }

  private function request(HttpMethod $method, ApiType $apiType, string $endpoint, array $params = []): array
  {
    $params = array_filter($params, static fn($v) => $v !== null && $v !== '');

    $sig     = $this->signer->generate($params, $method);
    $baseUrl = $this->resolveBase($apiType, $endpoint);
    $origin  = $this->extractOrigin($baseUrl);
    $headers = $this->buildHeaders($apiType, $sig, $origin);
    $url     = $this->buildUrl($baseUrl, $endpoint, $method, $params);
    $body    = $method->hasBody() ? $params : [];

    if ($this->batchMode) {
      $pending                             = new PendingRequest($url, $method, $headers, $body, $endpoint);
      $this->pendingRequests[$pending->id] = $pending;
      return ['_request_id' => $pending->id];
    }

    $ch     = $this->curlFactory->make($url, $method, $headers, $body ?: null);
    $raw    = curl_exec($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_HTTP_CODE);
    $errno  = curl_errno($ch);
    $error  = $errno ? curl_error($ch) : null;
    unset($ch);

    if ($error !== null) {
      return $this->responseHandler->error($status, "cURL error: {$error}");
    }

    return $this->responseHandler->handle($raw, $status, $endpoint);
  }

  /**
   * Execute multiple requests in parallel via curl_multi.
   *
   * @param  array<string, callable> $requests  e.g. ['key' => fn() => $this->someMethod()]
   * @return array<string, array>
   */
  public function batch(array $requests): array
  {
    $this->batchMode       = true;
    $this->pendingRequests = [];

    $keyToId = [];
    $results = [];

    foreach ($requests as $key => $callable) {
      $ret = $callable();
      if (isset($ret['_request_id'])) {
        $keyToId[$key] = $ret['_request_id'];
      } else {
        $results[$key] = $ret;
      }
    }

    $results = array_merge($results, $this->executePending($keyToId));

    $this->batchMode       = false;
    $this->pendingRequests = [];

    return $results;
  }

  /**
   * Execute the same set of requests for multiple accounts simultaneously.
   *
   * @param  array<string, array{apiKey?: string, webkey?: string, isTestnet?: bool, proxy?: string|null}> $accounts
   * @param  callable(MexcBypass): array<string, callable> $callback
   * @return array<string, array<string, array>>
   *
   * @example
   * $results = MexcBypass::batchAccounts([
   *     'acc1' => ['apiKey' => 'key1'],
   *     'acc2' => ['apiKey' => 'key2', 'isTestnet' => true, 'proxy' => 'socks5://user:pass@host:1080'],
   * ], fn($c) => [
   *     'positions' => fn() => $c->getFuturesOpenPositions(),
   *     'balance'   => fn() => $c->getFuturesAssets(['currency' => 'USDT']),
   * ]);
   */
  public static function batchAccounts(array $accounts, callable $callback): array
  {
    /** @var array<string, MexcBypass> $clients */
    $clients    = [];
    $allPending = [];
    $results    = [];

    foreach ($accounts as $accountKey => $cfg) {
      $client = new self(
        $cfg['apiKey'] ?? $cfg['webkey'] ?? '',
        $cfg['isTestnet'] ?? false,
        $cfg['proxy'] ?? null,
      );
      $client->batchMode = true;

      $allPending[$accountKey] = [];
      foreach ($callback($client) as $requestKey => $callable) {
        $ret = $callable();
        if (isset($ret['_request_id'])) {
          $allPending[$accountKey][$requestKey] = $ret['_request_id'];
        }
      }

      $clients[$accountKey] = $client;
      $results[$accountKey] = [];
    }

    $mh         = curl_multi_init();
    $handleMeta = [];

    foreach ($clients as $accountKey => $client) {
      foreach ($allPending[$accountKey] as $requestKey => $pendingId) {
        $pending = $client->pendingRequests[$pendingId];
        $ch      = $client->curlFactory->make($pending->url, $pending->method, $pending->headers, $pending->body ?: null);
        curl_multi_add_handle($mh, $ch);
        $handleMeta[(int)$ch] = [$accountKey, $requestKey, $pendingId, $ch];
      }
    }

    self::multiExec($mh);

    foreach ($handleMeta as [$accountKey, $requestKey, $pendingId, $ch]) {
      $client  = $clients[$accountKey];
      $pending = $client->pendingRequests[$pendingId];
      $raw     = curl_multi_getcontent($ch);
      $status  = (int)curl_getinfo($ch, CURLINFO_HTTP_CODE);
      $error   = curl_errno($ch) ? curl_error($ch) : null;

      $results[$accountKey][$requestKey] = $error !== null
        ? $client->responseHandler->error($status, "cURL error: {$error}")
        : $client->responseHandler->handle($raw, $status, $pending->endpoint);

      curl_multi_remove_handle($mh, $ch);
      unset($ch);
    }

    curl_multi_close($mh);

    foreach ($clients as $client) {
      $client->batchMode       = false;
      $client->pendingRequests = [];
    }

    return $results;
  }

  private function resolveBase(ApiType $apiType, string $endpoint): string
  {
    if ($apiType === ApiType::Other) {
      $p = parse_url($endpoint);
      return "https://{$p['host']}";
    }
    return $this->baseUrls[$apiType->value];
  }

  private function extractOrigin(string $baseUrl): string
  {
    $pos = strpos($baseUrl, '/', 8);
    return $pos !== false ? substr($baseUrl, 0, $pos) : $baseUrl;
  }

  private function buildHeaders(ApiType $apiType, SignatureResult $sig, string $origin): array
  {
    // Статические заголовки без Accept-Encoding — им управляет CURLOPT_ENCODING
    static $sharedBase = null;
    if ($sharedBase === null) {
      $sharedBase = [
        'Content-Type: application/json',
        'Accept: */*',
        'User-Agent: Mozilla/5.0 (compatible; MEXC-API/1.0)',
        'Connection: keep-alive',
        'Cache-Control: no-cache',
      ];
    }

    $shared = array_merge($sharedBase, ["Origin: {$origin}"]);

    if ($apiType === ApiType::General) {
      return array_merge($shared, [
        "Cookie: u_id={$this->apiKey}; uc_token={$this->apiKey};",
        "Ucenter-Token: {$this->apiKey}",
      ]);
    }

    return array_merge($shared, [
      'Authorization: '  . $this->apiKey,
      'X-Mxc-Nonce: '   . $sig->timestamp,
      'X-Mxc-Sign: '    . $sig->sign,
    ]);
  }

  private function buildUrl(string $baseUrl, string $endpoint, HttpMethod $method, array $params): string
  {
    $url = $baseUrl . $endpoint;

    if (!$method->hasBody() && !empty($params)) {
      $url .= '?' . http_build_query($params, '', '&', PHP_QUERY_RFC3986);
    }

    return $url;
  }

  private function executePending(array $keyToId): array
  {
    if (empty($keyToId)) {
      return [];
    }

    $mh         = curl_multi_init();
    $handleMeta = [];
    $results    = [];

    foreach ($keyToId as $key => $pendingId) {
      $p  = $this->pendingRequests[$pendingId];
      $ch = $this->curlFactory->make($p->url, $p->method, $p->headers, $p->body ?: null);
      curl_multi_add_handle($mh, $ch);
      $handleMeta[(int)$ch] = [$key, $pendingId, $ch];
    }

    self::multiExec($mh);

    foreach ($handleMeta as [$key, $pendingId, $ch]) {
      $p      = $this->pendingRequests[$pendingId];
      $raw    = curl_multi_getcontent($ch);
      $status = (int)curl_getinfo($ch, CURLINFO_HTTP_CODE);
      $error  = curl_errno($ch) ? curl_error($ch) : null;

      $results[$key] = $error !== null
        ? $this->responseHandler->error($status, "cURL error: {$error}")
        : $this->responseHandler->handle($raw, $status, $p->endpoint);

      curl_multi_remove_handle($mh, $ch);
      unset($ch);
    }

    curl_multi_close($mh);
    return $results;
  }

  private static function multiExec(\CurlMultiHandle $mh): void
  {
    $active = null;

    do {
      $rc = curl_multi_exec($mh, $active);
    } while ($rc === CURLM_CALL_MULTI_PERFORM);

    while ($active && $rc === CURLM_OK) {
      $selected = curl_multi_select($mh, 0.5);

      if ($selected === -1) {
        usleep(1000);
      }

      do {
        $rc = curl_multi_exec($mh, $active);
      } while ($rc === CURLM_CALL_MULTI_PERFORM);
    }
  }

  private function weekRange(): array
  {
    return [
      'start' => strtotime('monday this week') * 1000,
      'end'   => strtotime('sunday this week 23:59:59') * 1000 + 99,
    ];
  }

  private function normalizeVolume(float $raw, array $cd): float
  {
    $scale = (int)($cd['volScale'] ?? 0);
    $unit  = (float)($cd['volUnit'] ?? 0);
    $v     = round($raw, $scale);

    if ($unit > 0) {
      $v = floor($v / $unit) * $unit;
    }

    return (float)max((float)($cd['minVol'] ?? 0), min($v, (float)($cd['maxVol'] ?? PHP_FLOAT_MAX)));
  }

  private function volumePayload(array $cd, float $volume, int $leverage, float $price, float $usdtValue, PriceType $pt): array
  {
    return [
      'usdt_value'   => $usdtValue,
      'volume'       => $volume,
      'leverage'     => $leverage,
      'price'        => $price,
      'min_volume'   => $cd['minVol']      ?? null,
      'max_volume'   => $cd['maxVol']      ?? null,
      'min_leverage' => $cd['minLeverage'] ?? null,
      'max_leverage' => $cd['maxLeverage'] ?? null,
      'price_type'   => $pt->value,
      'volume_scale' => $cd['volScale']    ?? null,
      'volume_unit'  => $cd['volUnit']     ?? null,
      'price_scale'  => $cd['priceScale']  ?? null,
      'price_unit'   => $cd['priceUnit']   ?? null,
    ];
  }

  private function error(int $code, string $message): array
  {
    return $this->responseHandler->error($code, $message);
  }

  private function fetchTickerAndContract(string $symbol): array
  {
    $result = $this->batch([
      'ticker'   => fn() => $this->getFuturesTickers(['symbol' => $symbol]),
      'contract' => fn() => $this->getFuturesContracts(['symbol' => $symbol]),
    ]);

    if (empty($result['ticker']['success']) || empty($result['ticker']['data'])) {
      return ['ok' => false, 'error' => $this->error(400, 'Failed to get ticker data')];
    }
    if (empty($result['contract']['success']) || empty($result['contract']['data'][0])) {
      return ['ok' => false, 'error' => $this->error(400, 'Failed to get contract data')];
    }

    return ['ok' => true, 'result' => $result];
  }

  private function resolveMarket(array $market, array $params): array
  {
    $pt = PriceType::tryFrom($params['price_type'] ?? '') ?? PriceType::LastPrice;
    return [
      'ticker'   => $market['result']['ticker']['data'][$pt->tickerField()] ?? null,
      'contract' => $market['result']['contract']['data'][0],
      'pt'       => $pt,
    ];
  }

  /** @return array{0: string, 1: string} */
  private function parseSymbol(array $params): array
  {
    if (!empty($params['symbol'])) {
      $parts = explode('_', $params['symbol'], 2);
      return [$parts[0] ?? '', $parts[1] ?? ''];
    }

    return [$params['base_coin'] ?? '', $params['quote_coin'] ?? ''];
  }

  public function getServerTime(): array
  {
    return $this->request(HttpMethod::GET, ApiType::Contract, '/contract/ping');
  }

  public function ping(): array
  {
    return $this->request(HttpMethod::GET, ApiType::General, '/api/common/ping');
  }

  public function validation(): array
  {
    return $this->request(HttpMethod::POST, ApiType::General, '/ucenter/api/login/validation');
  }

  public function getWSToken(): array
  {
    return $this->request(HttpMethod::GET, ApiType::General, '/ucenter/api/ws_token');
  }

  public function getCustomerInfo(): array
  {
    return $this->request(HttpMethod::GET, ApiType::General, '/ucenter/api/customer_info');
  }

  public function getUserInfo(): array
  {
    return $this->request(HttpMethod::GET, ApiType::General, '/ucenter/api/user_info');
  }

  public function getLatestsDeposits(): array
  {
    return $this->request(HttpMethod::GET, ApiType::General, '/api/platform/deposit/v3/latest');
  }

  public function getDepositAddresses(array $params): array
  {
    if (isset($params['currency'])) {
      return $this->request(HttpMethod::GET, ApiType::General, "/api/platform/asset/api/asset/spot/currency/v3?currency={$params['currency']}");
    } else {
      return $this->request(HttpMethod::GET, ApiType::General, "/api/platform/asset/api/deposit/address/list?vcoinId={$params['vcoin_id']}");
    }
  }

  public function getSecureInfo(): array
  {
    return $this->request(HttpMethod::GET, ApiType::General, '/ucenter/api/secure/check');
  }

  public function getOriginInfo(): array
  {
    return $this->request(HttpMethod::GET, ApiType::General, '/ucenter/api/origin_info');
  }

  public function logout(): array
  {
    return $this->request(HttpMethod::POST, ApiType::General, '/ucenter/api/logout');
  }

  /**
   * @param array{symbol?: string, base_coin?: string, quote_coin?: string} $params
   */
  public function getMarketSymbols(array $params = []): array
  {
    $response = $this->request(HttpMethod::GET, ApiType::General, '/api/platform/spot/market-v2/web/symbolsV2');

    if (empty($params)) {
      return $response;
    }

    [$baseCoin, $quoteCoin] = $this->parseSymbol($params);

    if (empty($baseCoin)) {
      return $response;
    }

    if (empty($response['data']['symbols'][$quoteCoin])) {
      return $this->error(400, "No {$quoteCoin} symbols found");
    }

    $search  = strtoupper($baseCoin);
    $matches = array_values(array_filter(
      $response['data']['symbols'][$quoteCoin],
      static fn($item) => isset($item['currency']) && strtoupper($item['currency']) === $search,
    ));

    if (empty($matches)) {
      return $this->error(404, 'No matching tokens found');
    }

    return ['success' => true, 'count' => count($matches), 'data' => $matches, 'timestamp' => time()];
  }

  public function getSpotOrderBook(array $params): array
  {
    $symbols = $this->getMarketSymbols(['symbol' => $params['symbol']]);
    if (empty($symbols['success'])) {
      return $symbols;
    }

    return $this->request(HttpMethod::GET, ApiType::General, '/api/platform/spot/market-v2/web/depth/v2', [
      'symbolId' => $symbols['data'][0]['id'],
      'decimal'  => $params['decimal'] ?? '0.0000001',
      'count'    => $params['count']   ?? 100,
    ]);
  }

  public function createSpotOrder(array $params): array
  {
    $path    = ($params['type'] === 'LIMIT_ORDER')
      ? '/api/platform/spot/order/place'
      : '/api/platform/spot/v4/order/place';
    $symbols = $this->getMarketSymbols(['symbol' => $params['symbol']]);

    if (empty($symbols['success'])) {
      return $symbols;
    }

    return $this->request(HttpMethod::POST, ApiType::General, $path, [
      'orderType'        => $params['type'],
      'tradeType'        => $params['side'],
      'price'            => $params['price']    ?? null,
      'amount'           => $params['amount']   ?? null,
      'quantity'         => $params['quantity'] ?? null,
      'marketCurrencyId' => $symbols['data'][0]['marketCurrencyId'],
      'currencyId'       => $symbols['data'][0]['coinId'],
      'orderSource'      => 'WEB',
      'ts'               => time() * 1000,
    ]);
  }

  public function cancelSpotOrder(array $params): array
  {
    return $this->request(HttpMethod::DELETE, ApiType::General, '/api/platform/spot/order/cancel/v2', [
      'orderId' => $params['order_id'],
    ]);
  }

  public function getReferralsList(array $params = []): array
  {
    $week = $this->weekRange();
    return $this->request(HttpMethod::GET, ApiType::General, '/api/assetbussiness/invite/invites', [
      'startTime' => $params['start_time'] ?? $week['start'],
      'endTime'   => $params['end_time']   ?? $week['end'],
      'page'      => $params['page_num']   ?? 1,
      'pageSize'  => $params['page_size']  ?? 10,
    ]);
  }

  public function getAssetsOverview(array $params = []): array
  {
    $endpoint = ($params['convert'] ?? false)
      ? '/api/platform/asset/api/asset/overview/convert/v2'
      : '/api/platform/asset/api/asset/overview/v2';

    return $this->request(HttpMethod::GET, ApiType::General, $endpoint);
  }

  public function getFuturesFeeRate(): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/account/contract/fee_rate');
  }

  public function getFuturesZeroFeeRate(): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/account/contract/zero_fee_rate');
  }

  public function getFuturesTodayPnL(): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/account/asset/analysis/today_pnl');
  }

  public function getFuturesAnalysis(array $params = []): array
  {
    $now       = (int)(microtime(true) * 1000);
    $endTime   = min($params['end_time'] ?? $now, $now);
    $startTime = $params['start_time'] ?? ($endTime - 7 * 86_400 * 1000);

    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/account/asset/analysis/v3', [
      'currency'               => $params['currency']                ?? 'USDT',
      'symbol'                 => $params['symbol']                  ?? null,
      'include_unrealised_pnl' => $params['include_unrealised_pnl'] ?? 0,
      'reverse'                => $params['reverse']                 ?? 0,
      'startTime'              => $startTime < $endTime ? $startTime : $endTime - 1000,
      'endTime'                => $endTime,
    ]);
  }

  public function getFuturesAssets(array $params = []): array
  {
    $endpoint = isset($params['currency'])
      ? "/private/account/asset/{$params['currency']}"
      : '/private/account/assets';

    return $this->request(HttpMethod::GET, ApiType::Futures, $endpoint);
  }

  public function getFuturesAssetTransferRecords(array $params = []): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/account/transfer_record', [
      'currency'  => $params['currency']  ?? null,
      'state'     => $params['state']     ?? null,
      'type'      => $params['type']      ?? null,
      'page_num'  => $params['page_num']  ?? 1,
      'page_size' => $params['page_size'] ?? 20,
    ]);
  }

  public function getFuturesRiskLimits(array $params = []): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/account/risk_limit', [
      'symbol' => $params['symbol'] ?? null,
    ]);
  }

  public function getFuturesFundingRate(array $params): array
  {
    return $this->request(HttpMethod::GET, ApiType::Contract, "/contract/funding_rate/{$params['symbol']}");
  }

  public function getFuturesContracts(array $params = []): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/contract/detailV2', [
      'symbol' => $params['symbol'] ?? null,
    ]);
  }

  public function getFuturesContractIndexPrice(array $params): array
  {
    return $this->request(HttpMethod::GET, ApiType::Contract, "/contract/index_price/{$params['symbol']}");
  }

  public function getFuturesContractFairPrice(array $params): array
  {
    return $this->request(HttpMethod::GET, ApiType::Contract, "/contract/fair_price/{$params['symbol']}");
  }

  public function getFuturesContractKlineData(array $params): array
  {
    $pt      = PriceType::tryFrom($params['price_type'] ?? '') ?? PriceType::LastPrice;
    $segment = $pt->klinePathSegment();
    $path    = $segment !== ''
      ? "/contract/kline/{$segment}/{$params['symbol']}"
      : "/contract/kline/{$params['symbol']}";

    return $this->request(HttpMethod::GET, ApiType::Contract, $path, [
      'interval' => $params['interval']   ?? 'Min1',
      'start'    => $params['start_time'] ?? null,
      'end'      => $params['end_time']   ?? null,
    ]);
  }

  public function getFuturesTickers(array $params = []): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/contract/ticker', [
      'symbol' => $params['symbol'] ?? null,
    ]);
  }

  public function getFuturesOpenPositions(array $params = []): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/position/open_positions', array_filter([
      'symbol'     => $params['symbol']      ?? null,
      'positionId' => $params['position_id'] ?? null,
    ]));
  }

  public function getFuturesPositionsHistory(array $params = []): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/position/list/history_positions', [
      'symbol'    => $params['symbol']    ?? null,
      'type'      => $params['type']      ?? null,
      'page_num'  => $params['page_num']  ?? 1,
      'page_size' => $params['page_size'] ?? 20,
    ]);
  }

  public function closeAllFuturesPositions(): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/position/close_all');
  }

  public function reverseFuturesPosition(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/position/reverse', [
      'positionId' => $params['position_id'],
      'symbol'     => $params['symbol'],
      'vol'        => $params['vol'],
    ]);
  }

  public function getFuturesPositionMode(): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/position/position_mode');
  }

  public function changeFuturesPositionMode(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/position/change_position_mode', [
      'positionMode' => $params['position_mode'],
    ]);
  }

  public function getFuturesLeverage(array $params): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/position/leverage', [
      'symbol' => $params['symbol'],
    ]);
  }

  public function changeFuturesPositionMargin(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/position/change_margin', [
      'positionId' => $params['position_id'],
      'amount'     => $params['amount'],
      'type'       => $params['type'],
    ]);
  }

  public function changeFuturesPositionLeverage(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/position/change_leverage', [
      'positionId'   => $params['position_id'],
      'leverage'     => $params['leverage'],
      'openType'     => $params['open_type']     ?? null,
      'symbol'       => $params['symbol']        ?? null,
      'positionType' => $params['position_type'] ?? null,
    ]);
  }

  public function getFuturesOrdersDeals(array $params): array
  {
    $week = $this->weekRange();
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/order/list/order_deals', [
      'symbol'     => $params['symbol'],
      'start_time' => $params['start_time'] ?? $week['start'],
      'end_time'   => $params['end_time']   ?? $week['end'],
      'page_num'   => $params['page_num']   ?? 1,
      'page_size'  => $params['page_size']  ?? 20,
    ]);
  }

  public function getFuturesPendingOrders(array $params = []): array
  {
    $endpoint = isset($params['symbol'])
      ? "/private/order/list/open_orders/{$params['symbol']}"
      : '/private/order/list/open_orders/';

    return $this->request(HttpMethod::GET, ApiType::Futures, $endpoint, [
      'page_num'  => $params['page_num']  ?? 1,
      'page_size' => $params['page_size'] ?? 20,
    ]);
  }

  public function getFuturesOrdersHistory(array $params = []): array
  {
    $week = $this->weekRange();
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/order/list/history_orders', [
      'symbol'     => $params['symbol']     ?? null,
      'states'     => $params['states']     ?? null,
      'category'   => $params['category']   ?? null,
      'side'       => $params['side']       ?? null,
      'start_time' => $params['start_time'] ?? $week['start'],
      'end_time'   => $params['end_time']   ?? $week['end'],
      'page_num'   => $params['page_num']   ?? 1,
      'page_size'  => $params['page_size']  ?? 20,
    ]);
  }

  public function getFuturesOpenLimitOrders(array $params = []): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/order/list/open_orders', [
      'page_size' => $params['page_size'] ?? 200,
    ]);
  }

  public function getFuturesOpenStopOrders(array $params = []): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/stoporder/open_orders', [
      'page_size' => $params['page_size'] ?? 200,
    ]);
  }

  public function getFuturesOpenOrders(array $params = []): array
  {
    $pageSize = $params['page_size'] ?? 200;
    $result   = $this->batch([
      'limit_orders' => fn() => $this->getFuturesOpenLimitOrders(['page_size' => $pageSize]),
      'stop_orders'  => fn() => $this->getFuturesOpenStopOrders(['page_size'  => $pageSize]),
    ]);

    $merged = array_merge(
      ($result['limit_orders']['success'] ?? false) ? ($result['limit_orders']['data'] ?? []) : [],
      ($result['stop_orders']['success']  ?? false) ? ($result['stop_orders']['data']  ?? []) : [],
    );

    return ['success' => true, 'code' => 0, 'data' => $merged];
  }

  public function getFuturesClosedOrders(array $params = []): array
  {
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/order/close_orders', [
      'symbol'    => $params['symbol']    ?? null,
      'category'  => $params['category']  ?? null,
      'page_num'  => $params['page_num']  ?? 1,
      'page_size' => $params['page_size'] ?? 20,
    ]);
  }

  public function getFuturesOrdersById(array $params): array
  {
    $ids = array_map('trim', explode(',', (string)$params['ids']));

    return count($ids) > 1
      ? $this->request(HttpMethod::GET, ApiType::Futures, '/private/order/batch_query', ['order_ids' => $params['ids']])
      : $this->request(HttpMethod::GET, ApiType::Futures, "/private/order/get/{$params['ids']}");
  }

  public function createFuturesOrder(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/order/create', array_filter([
      'symbol'          => $params['symbol'],
      'price'           => $params['price']             ?? null,
      'type'            => $params['type'],
      'openType'        => $params['open_type']         ?? null,
      'positionMode'    => $params['position_mode']     ?? null,
      'side'            => $params['side'],
      'vol'             => $params['vol'],
      'leverage'        => $params['leverage']          ?? null,
      'positionId'      => $params['position_id']       ?? null,
      'externalOid'     => $params['external_id']       ?? null,
      'takeProfitPrice' => $params['take_profit_price'] ?? null,
      'profitTrend'     => $params['take_profit_trend'] ?? 1,
      'stopLossPrice'   => $params['stop_loss_price']   ?? null,
      'lossTrend'       => $params['stop_loss_trend']   ?? 1,
      'priceProtect'    => $params['price_protect']     ?? 0,
      'reduceOnly'      => $params['reduce_only']       ?? false,
      'marketCeiling'   => $params['market_ceiling']    ?? false,
      'flashClose'      => $params['flash_close']       ?? null,
      'bboTypeNum'      => $params['bbo_type']          ?? null,
    ]));
  }

  public function cancelFuturesOrders(array $params): array
  {
    $ids = is_array($params['ids'])
      ? $params['ids']
      : array_map('trim', explode(',', (string)$params['ids']));

    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/order/cancel', $ids);
  }

  public function cancelFuturesOrderWithExternalId(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/order/cancel_with_external', [
      'symbol'      => $params['symbol'],
      'externalOid' => $params['external_id'],
    ]);
  }

  public function cancelAllFuturesOrders(array $params = []): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/order/cancel_all', [
      'symbol' => $params['symbol'] ?? null,
    ]);
  }

  public function changeFuturesLimitOrderPrice(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, 'private/order/change_limit_order', [
      'orderId' => $params['order_id'],
      'price'   => $params['price'],
      'vol'     => $params['vol'],
    ]);
  }

  public function chaseFuturesOrder(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/order/chase_limit_order', [
      'orderId' => $params['order_id'],
    ]);
  }

  public function createFuturesChaseOrder(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/order/chase_limit/place', array_filter([
      'chaseType'        => $params['chase_type'],
      'distanceType'     => $params['distance_type']      ?? null,
      'distanceValue'    => $params['distance_value']     ?? null,
      'maxDistanceType'  => $params['max_distance_type']  ?? null,
      'maxDistanceValue' => $params['max_distance_value'] ?? null,
      'leverage'         => $params['leverage'],
      'openType'         => $params['open_type'],
      'side'             => $params['side'],
      'symbol'           => $params['symbol'],
      'vol'              => $params['vol'],
    ]));
  }

  public function createFuturesStopOrder(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/stoporder/place/v2', [
      'positionId'           => $params['position_id'],
      'volType'              => $params['vol_type']                ?? 2,
      'vol'                  => $params['vol']                     ?? null,
      'takeProfitType'       => $params['take_profit_type']        ?? null,
      'takeProfitOrderPrice' => $params['take_profit_order_price'] ?? null,
      'takeProfitPrice'      => $params['take_profit_price']       ?? null,
      'takeProfitReverse'    => $params['take_profit_reverse']     ?? 2,
      'profitTrend'          => $params['take_profit_trend']       ?? 1,
      'takeProfitVol'        => $params['take_profit_volume']      ?? null,
      'stopLossType'         => $params['stop_loss_type']          ?? null,
      'stopLossOrderPrice'   => $params['stop_loss_order_price']   ?? null,
      'stopLossPrice'        => $params['stop_loss_price']         ?? null,
      'stopLossReverse'      => $params['stop_loss_reverse']       ?? 2,
      'lossTrend'            => $params['stop_loss_trend']         ?? 1,
      'profitLossVolType'    => $params['profit_loss_vol_type']    ?? 'SAME',
      'priceProtect'         => $params['price_protect']           ?? 0,
    ]);
  }

  public function getFuturesStopLimitOrders(array $params = []): array
  {
    $week = $this->weekRange();
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/stoporder/list/orders', [
      'symbol'      => $params['symbol']      ?? null,
      'is_finished' => $params['is_finished'] ?? null,
      'start_time'  => $params['start_time']  ?? $week['start'],
      'end_time'    => $params['end_time']     ?? $week['end'],
      'page_num'    => $params['page_num']    ?? 1,
      'page_size'   => $params['page_size']   ?? 20,
    ]);
  }

  public function cancelStopLimitOrders(array $params): array
  {
    $ids = is_string($params['ids'])
      ? array_filter(array_map('trim', explode(',', $params['ids'])))
      : $params['ids'];

    $payload = array_map(static fn($id) => ['stopPlanOrderId' => (int)$id], array_values($ids));

    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/stoporder/cancel', $payload);
  }

  public function cancelAllFuturesStopLimitOrders(array $params = []): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/stoporder/cancel_all', [
      'positionId' => $params['position_id'] ?? null,
      'symbol'     => $params['symbol']      ?? null,
    ]);
  }

  public function changeFuturesOrderStopLimitPrice(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/stoporder/change_price', [
      'orderId'         => $params['order_id'],
      'takeProfitPrice' => $params['take_profit_price'] ?? null,
      'stopLossPrice'   => $params['stop_loss_pirce']   ?? null,
    ]);
  }

  public function changeFuturesPlanOrderStopLimitPrice(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/stoporder/change_plan_price', [
      'stopPlanOrderId' => $params['order_id'],
      'takeProfitPrice' => $params['take_profit_price'] ?? null,
      'stopLossPrice'   => $params['stop_loss_pirce']   ?? null,
    ]);
  }

  public function changeFuturesOrderTargets(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/stoporder/change_plan_order', [
      'orderId'          => $params['real_order_id'],
      'profitTrend'      => $params['take_profit_trend']  ?? null,
      'takeProfitPrice'  => $params['take_profit_price']  ?? null,
      'takeProfitVolume' => $params['take_profit_volume'] ?? null,
      'lossTrend'        => $params['stop_loss_trend']    ?? null,
      'stopLossPrice'    => $params['stop_loss_price']    ?? null,
      'stopLossVolume'   => $params['stop_loss_volume']   ?? null,
    ]);
  }

  public function createFuturesTriggerOrder(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/planorder/place', [
      'symbol'       => $params['symbol'],
      'price'        => $params['price']        ?? null,
      'vol'          => $params['vol'],
      'leverage'     => $params['leverage']     ?? null,
      'side'         => $params['side'],
      'openType'     => $params['open_type'],
      'triggerPrice' => $params['trigger_price'],
      'triggerType'  => $params['trigger_type'],
      'executeCycle' => $params['execute_cycle'],
      'orderType'    => $params['order_type'],
      'trend'        => $params['trend'],
    ]);
  }

  public function getFuturesTriggerOrders(array $params = []): array
  {
    $week = $this->weekRange();
    return $this->request(HttpMethod::GET, ApiType::Futures, '/private/planorder/list/orders', [
      'symbol'     => $params['symbol']     ?? null,
      'states'     => $params['states']     ?? null,
      'start_time' => $params['start_time'] ?? $week['start'],
      'end_time'   => $params['end_time']   ?? $week['end'],
      'page_num'   => $params['page_num']   ?? 1,
      'page_size'  => $params['page_size']  ?? 20,
    ]);
  }

  public function cancelFuturesTriggerOrders(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/planorder/cancel', $params['ids']);
  }

  public function cancelAllFuturesTriggerOrders(array $params = []): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/planorder/cancel_all', [
      'symbol' => $params['symbol'] ?? null,
    ]);
  }

  public function createFuturesTrailingOrder(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/trackorder/place', array_filter([
      'symbol'       => $params['symbol'],
      'leverage'     => $params['leverage'],
      'side'         => $params['side'],
      'vol'          => $params['vol'],
      'openType'     => $params['open_type'],
      'trend'        => $params['trend'],
      'activePrice'  => $params['active_price'],
      'backType'     => $params['back_type'],
      'backValue'    => $params['back_value'],
      'positionMode' => $params['position_mode'] ?? 0,
      'reduceOnly'   => $params['reduce_only']   ?? null,
    ]));
  }

  public function cancelFuturesTrailingOrder(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/trackorder/cancel', [
      'symbol'       => $params['symbol']   ?? null,
      'trackOrderId' => $params['order_id'] ?? null,
    ]);
  }

  public function changeFuturesTrailingOrder(array $params): array
  {
    return $this->request(HttpMethod::POST, ApiType::Futures, '/private/trackorder/change_order', [
      'symbol'       => $params['symbol'],
      'trackOrderId' => $params['order_id'],
      'trend'        => $params['trend'],
      'activePrice'  => $params['active_price'],
      'backType'     => $params['back_type'],
      'backValue'    => $params['back_value'],
      'vol'          => $params['vol'],
    ]);
  }

  public function calculateFuturesPositionPnL(array $params): array
  {
    $result = $this->batch([
      'contract' => fn() => $this->getFuturesContracts(['symbol' => $params['symbol']]),
      'ticker'   => fn() => $this->getFuturesTickers(['symbol' => $params['symbol']]),
    ]);

    $contract = $result['contract']['data'][0] ?? null;
    $ticker   = $result['ticker']['data']      ?? null;

    if (!$contract || !$ticker || empty($contract['contractSize'])) {
      return $this->error(-1, 'Unable to calculate PnL: missing market data');
    }

    $pt            = PriceType::tryFrom($params['price_type'] ?? '') ?? PriceType::LastPrice;
    $price         = (float)($ticker[$pt->tickerField()] ?? 0);
    $entry         = (float)$params['entry_price'];
    $volume        = (float)$params['volume'];
    $leverage      = max(1, (int)($params['leverage'] ?? 1));
    $cs            = (float)$contract['contractSize'];
    $isLong        = ((int)$params['side'] === 1);
    $pnl           = $isLong ? ($price - $entry) * $volume * $cs : ($entry - $price) * $volume * $cs;
    $initialMargin = $entry * $volume * $cs / $leverage;

    return [
      'success' => true,
      'code'    => 0,
      'data'    => [
        'pnl'           => round($pnl, 4),
        'pnl_percent'   => $initialMargin > 0 ? round($pnl / $initialMargin * 100, 2) : 0,
        'volume'        => $volume,
        'entry_price'   => $entry,
        'current_price' => $price,
        'price_type'    => $pt->value,
      ],
    ];
  }

  public function calculateFuturesVolume(array $params): array
  {
    foreach (['symbol', 'amount', 'leverage'] as $req) {
      if (empty($params[$req])) {
        return $this->error(404, "Missing required parameter: {$req}");
      }
    }

    $market = $this->fetchTickerAndContract($params['symbol']);
    if (!$market['ok']) {
      return $market['error'];
    }

    ['ticker' => $ticker, 'contract' => $cd, 'pt' => $pt] = $this->resolveMarket($market, $params);

    if ($ticker === null)                                        return $this->error(400, 'Invalid price value');
    if ($params['amount'] <= 0)                                  return $this->error(400, 'Amount must be positive');
    if ($params['leverage'] <= 0)                                return $this->error(400, 'Leverage must be positive');
    if (empty($cd['contractSize']) || $cd['contractSize'] <= 0)  return $this->error(400, 'Invalid contract size');

    $price    = (float)$ticker;
    $leverage = (int)max($cd['minLeverage'], min($params['leverage'], $cd['maxLeverage']));
    $volume   = $this->normalizeVolume(($params['amount'] * $leverage) / ($price * $cd['contractSize']), $cd);
    $usdt     = round($volume * $price * $cd['contractSize'] / $leverage, $cd['priceScale']);

    return ['success' => true, 'code' => 0, 'data' => $this->volumePayload($cd, $volume, $leverage, $price, $usdt, $pt)];
  }

  public function calculateFuturesVolumeFromBaseAmount(array $params): array
  {
    foreach (['symbol', 'amount', 'leverage'] as $req) {
      if (empty($params[$req])) {
        return $this->error(404, "Missing required parameter: {$req}");
      }
    }

    $market = $this->fetchTickerAndContract($params['symbol']);
    if (!$market['ok']) {
      return $market['error'];
    }

    ['ticker' => $ticker, 'contract' => $cd, 'pt' => $pt] = $this->resolveMarket($market, $params);

    if ($ticker === null)                                        return $this->error(400, 'Invalid price value');
    if ($params['amount'] <= 0)                                  return $this->error(400, 'Base amount must be positive');
    if ($params['leverage'] <= 0)                                return $this->error(400, 'Leverage must be positive');
    if (empty($cd['contractSize']) || $cd['contractSize'] <= 0)  return $this->error(400, 'Invalid contract size');

    $price    = (float)$ticker;
    $leverage = (int)max($cd['minLeverage'], min($params['leverage'], $cd['maxLeverage']));
    $volume   = $this->normalizeVolume($params['amount'] / $cd['contractSize'], $cd);
    $notional = $params['amount'] * $price;
    $usdt     = round($notional, $cd['priceScale']);

    return [
      'success' => true,
      'code'    => 0,
      'data'    => array_merge(
        $this->volumePayload($cd, $volume, $leverage, $price, $usdt, $pt),
        [
          'required_margin' => round($notional / $leverage, $cd['priceScale']),
          'amount'          => $params['amount'],
          'notional_value'  => $notional,
          'contract_size'   => $cd['contractSize'],
        ],
      ),
    ];
  }

  public function receiveFuturesTestnetAsset(array $params = []): array
  {
    return $this->request(
      HttpMethod::POST,
      ApiType::Other,
      'https://futures.testnet.mexc.com/mock/contract/asset/receive',
      [
        'currency' => $params['currency'] ?? 'USDT',
        'amount'   => $params['amount']   ?? null,
      ],
    );
  }
}
