<?php

require_once(__DIR__ . '/MexcBypass.php');

$client = new MexcBypass(apiKey: 'YOUR_MEXC_WEB_KEY', isTestnet: false, proxyUrl: null);

$results = $client->batch([
  'assets'     => fn() => $client->getFuturesAssets(['currency' => 'USDT']),
  'positions'  => fn() => $client->getFuturesOpenPositions(),
  'ticker_btc' => fn() => $client->getFuturesTickers(['symbol' => 'BTC_USDT']),
]);

echo "USDT Balance: " . ($results['assets']['data']['availableBalance'] ?? 'N/A') . "\n";
echo "Open positions: " . json_encode($results['positions']['data'] ?? []) . "\n";
echo "BTC Price: " . ($results['ticker_btc']['data']['lastPrice'] ?? 'N/A') . "\n";

$order = $client->createFuturesOrder([
  'symbol'   => 'BTC_USDT',
  'side'     => 1,
  'type'     => 5,
  'open_type' => 1,
  'vol'      => 1,
  'leverage' => 10
]);

echo "Order created: " . json_encode($order, JSON_PRETTY_PRINT) . "\n";
