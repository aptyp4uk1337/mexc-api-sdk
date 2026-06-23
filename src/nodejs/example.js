import { MexcBypass } from './MexcBypass.js';

const client = new MexcBypass('YOUR_MEXC_WEB_KEY', true, null);

try {
  const results = await client.batch({
    assets: () => client.getFuturesAssets({ currency: 'USDT' }),
    positions: () => client.getFuturesOpenPositions(),
    ticker_btc: () => client.getFuturesTickers({ symbol: 'BTC_USDT' }),
  });

  console.log('USDT Balance:', results.assets?.data?.availableBalance ?? 'N/A');
  console.log('Open positions:', results.positions?.data ?? []);
  console.log('BTC Price:', results.ticker_btc?.data?.lastPrice ?? 'N/A');

  const order = await client.createFuturesOrder({
    symbol: 'BTC_USDT',
    side: 1,
    type: 5,
    open_type: 1,
    vol: 1,
    leverage: 10
  });

  console.log('Order created:', JSON.stringify(order, null, 2));
} catch (error) {
  console.error('Error:', error);
}