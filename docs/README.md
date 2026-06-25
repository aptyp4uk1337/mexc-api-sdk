# 🚀 MEXC API SDK

This project provides a wrapper around the MEXC Futures API with support for proxy usage, testnet/mainnet environments, and custom authorization headers. It is designed for fast and convenient access to MEXC methods without direct login.

<!--
## 🌐 Base URL

```
https://api.mexc-bypass.xyz/v1
```

## 📌 Headers

All requests must include the following headers:

| Header                  | Required | Description                                                                 |
|-------------------------|----------|-----------------------------------------------------------------------------|
| `X-MEXC-BYPASS-API-KEY` | ✅       | Your unique API key to access the MEXC Bypass API.                          |
| `X-MEXC-WEB-KEY`        | ❌       | Optional MEXC web key. Used only in some methods.                           |
| `X-MEXC-NETWORK`        | ❌       | Network type: `TESTNET` (default) or `MAINNET`.                             |
| `X-MEXC-PROXY`          | ❌       | Optional proxy URL (supports `http://` or `socks5://`).                     |

-->

---

## 📁 Available Methods

| Method | Category | Description |
|--------|----------|-------------|
| [**getServerTime**](/docs/methods/getServerTime.md) | General | Get the server time. |
| [**getCustomerInfo**](/docs/methods/getCustomerInfo.md) | General | Get brief information about the authenticated user. |
| [**getUserInfo**](/docs/methods/getUserInfo.md) | General | Get brief information about the authenticated user. |
| [**getOriginInfo**](/docs/methods/getOriginInfo.md) | General | Get account origin info. |
| [**getReferralsList**](/docs/methods/getReferralsList.md) | General | Get referral invite list and their commission statistics. |
| [**getFuturesTodayPnL**](/docs/methods/getFuturesTodayPnL.md) | General | Get futures today PnL. |
| [**getAssetsOverview**](/docs/methods/getAssetsOverview.md) | General | Get an overview of your asset balances. |
| [**logout**](/docs/methods/logout.md) | General | Deactivate X-MEXC-WEB-KEY and close current sessions. |
| [**getSpotOrderBook**](/docs/methods/getSpotOrderBook.md) | Spot | Get order book. |
| [**createSpotOrder**](/docs/methods/createSpotOrder.md) | Spot | Create a new spot order. |
| [**cancelSpotOrder**](/docs/methods/cancelSpotOrder.md) | Spot | Cancel spot limit order. |
| [**getFuturesUser**](/docs/methods/getFuturesUser.md) | Futures | Get futures user info. |
| [**getFuturesAssets**](/docs/methods/getFuturesAssets.md) | Futures | Get detailed balance data for your futures account. |
| [**getFuturesAssetTransferRecords**](/docs/methods/getFuturesAssetTransferRecords.md) | Futures | Get the list of asset transfer records on your futures account. |
| [**getFuturesAnalysis**](/docs/methods/getFuturesAnalysis.md) | Futures | Get the list of asset transfer records on your futures account. |
| [**getFuturesOrdersDeals**](/docs/methods/getFuturesOrdersDeals.md) | Futures | Fetches the deal history for your futures orders. |
| [**getFuturesContracts**](/docs/methods/getFuturesContracts.md) | Futures | Get details of one or all futures contracts. |
| [**getFuturesContractIndexPrice**](/docs/methods/getFuturesContractIndexPrice.md) | Futures | Retrieve the index price of a specific futures contract. |
| [**getFuturesContractFairPrice**](/docs/methods/getFuturesContractFairPrice.md) | Futures | Fetch the fair price of a specific futures contract. |
| [**getFuturesContractKlineData**](/docs/methods/getFuturesContractKlineData.md) | Futures | Get contract K-line data. |
| [**getFuturesTickers**](/docs/methods/getFuturesTickers.md) | Futures | Get the latest market ticker for a futures contract. |
| [**calculateFuturesPositionPnL**](/docs/methods/calculateFuturesPositionPnL.md) | Futures | Calculate futures position PnL. |
| [**calculateFuturesVolume**](/docs/methods/calculateFuturesVolume.md) | Futures | Calculate position volume quantity. |
| [**getFuturesPendingOrders**](/docs/methods/getFuturesPendingOrders.md) | Futures | Get a list of your open (pending) futures orders. |
| [**getFuturesOrdersHistory**](/docs/methods/getFuturesOrdersHistory.md) | Futures | Get historical futures orders (filled, cancelled, etc.) |
| [**getFuturesOpenPositions**](/docs/methods/getFuturesOpenPositions.md) | Futures | Get currently open futures positions. |
| [**getFuturesPositionsHistory**](/docs/methods/getFuturesPositionsHistory.md) | Futures | Retrieve the historical closed positions for a specific contract. |
| [**reverseFuturesPosition**](/docs/methods/reverseFuturesPosition.md) | Futures | Reverse futures position. |
| [**closeAllFuturesPositions**](/docs/methods/closeAllFuturesPositions.md) | Futures | Close all currently open futures positions for the account. |
| [**getFuturesOpenOrders**](/docs/methods/getFuturesOpenOrders.md) | Futures | Get currently open futures orders. |
| [**getFuturesClosedOrders**](/docs/methods/getFuturesClosedOrders.md) | Futures | Get futures closed orders. |
| [**getFuturesOpenLimitOrders**](/docs/methods/getFuturesOpenLimitOrders.md) | Futures | Get currently open limit futures orders. |
| [**getFuturesOpenStopOrders**](/docs/methods/getFuturesOpenStopOrders.md) | Futures | Get currently open stop futures orders. |
| [**createFuturesOrder**](/docs/methods/createFuturesOrder.md) | Futures | Create a new futures order. |
| [**createFuturesStopOrder**](/docs/methods/createFuturesTrailingOrder.md) | Futures | Create a new futures stop order. |
| [**createFuturesTrailingOrder**](/docs/methods/createFuturesStopOrder.md) | Futures | Create a new futures trailing order. |
| [**createFuturesChaseOrder**](/docs/methods/createFuturesChaseOrder.md) | Futures | Create a new futures chase limit order. |
| [**getFuturesOrdersById**](/docs/methods/getFuturesOrdersById.md) | Futures | Fetch futures order(s) by ID. |
| [**chaseFuturesOrder**](/docs/methods/chaseFuturesOrder.md) | Futures | Modify order price to the corresponding one-tick price. |
| [**cancelFuturesOrders**](/docs/methods/cancelFuturesOrders.md) | Futures | Cancel the pending order placed before, each time can cancel up to 50 orders. |
| [**cancelFuturesOrderWithExternalId**](/docs/methods/cancelFuturesOrderWithExternalId.md) | Futures | Cancel a futures order using an external order ID. |
| [**cancelAllFuturesOrders**](/docs/methods/cancelAllFuturesOrders.md) | Futures | Cancel all open futures orders for a given symbol. |
| [**createFuturesTriggerOrder**](/docs/methods/createFuturesTriggerOrder.md) | Futures | Create a trigger (planned) futures order that executes once market conditions are met. |
| [**getFuturesTriggerOrders**](/docs/methods/getFuturesTriggerOrders.md) | Futures | Fetch the list of trigger orders (plan orders). |
| [**cancelFuturesTriggerOrders**](/docs/methods/cancelFuturesTriggerOrders.md) | Futures | Cancel one or multiple Trigger Orders. |
| [**cancelFuturesTrailingOrder**](/docs/methods/cancelFuturesTrailingOrder.md) | Futures | Cancel fuutres trailing order. |
| [**cancelAllFuturesTriggerOrders**](/docs/methods/cancelAllFuturesTriggerOrders.md) | Futures | Cancel all open trigger (planned) futures orders. You can cancel all at once, or only for a specific symbol. |
| [**getFuturesStopLimitOrders**](/docs/methods/getFuturesStopLimitOrders.md) | Futures | Retrieve the list of Stop-Limit orders (take-profit & stop-loss). |
| [**cancelStopLimitOrders**](/docs/methods/cancelStopLimitOrders.md) | Futures | Cancel stop-limit futures orders by id. |
| [**changeFuturesLimitOrderPrice**](/docs/methods/changeFuturesLimitOrderPrice.md) | Futures | Change futures limit order price. |
| [**changeFuturesTrailingOrder**](/docs/methods/changeFuturesTrailingOrder.md) | Futures | Modify futures trailing order. |
| [**cancelAllFuturesStopLimitOrders**](/docs/methods/cancelAllFuturesStopLimitOrders.md) | Futures | Cancel all stop-limit (stoporder) futures orders. |
| [**getFuturesPositionMode**](/docs/methods/getFuturesPositionMode.md) | Futures | Get the current position mode for the account. |
| [**getFuturesLeverage**](/docs/methods/getFuturesLeverage.md) | Futures | Get leverage and margin information for a specific futures contract. |
| [**getFuturesRiskLimits**](/docs/methods/getFuturesRiskLimits.md) | Futures | Get risk limit tiers for a specific futures contract. |
| [**changeFuturesPositionMargin**](/docs/methods/changeFuturesPositionMargin.md) | Futures | Change the margin amount of an open futures position. |
| [**changeFuturesPositionLeverage**](/docs/methods/changeFuturesPositionLeverage.md) | Futures | Change the leverage of a position. If the position does not exist yet, additional info is required. |
| [**changeFuturesPositionMode**](/docs/methods/changeFuturesPositionMode.md) | Futures | Change the position mode for your futures account. |
| [**changeFuturesOrderStopLimitPrice**](/docs/methods/changeFuturesOrderStopLimitPrice.md) | Futures | Switch Stop-Limit limited order price. |
| [**changeFuturesPlanOrderStopLimitPrice**](/docs/methods/changeFuturesPlanOrderStopLimitPrice.md) | Futures | Switch the Stop-Limit price of trigger orders. |
| [**changeFuturesOrderTargets**](/docs/methods/changeFuturesOrderTargets.md) | Futures | Switch the take profit price & stop loss price (fixed). |
| [**receiveFuturesTestnetAsset**](/docs/methods/receiveFuturesTestnetAsset.md) | Futures | Receive futures testnet assets. |

<!--

## 📦 Quick Example

#### 🌐 cURL

```bash
curl -X GET 'https://api.mexc-bypass.xyz/v1/getServerTime' \
  -H 'X-MEXC-BYPASS-API-KEY: <YOUR_MEXC_BYPASS_API_KEY>' \
  -H 'X-MEXC-WEB-KEY: <YOUR_MEXC_WEB_KEY>' \
  -H 'X-MEXC-NETWORK: TESTNET'
```

### 🐍 Python

```python
import requests

url = "https://api.mexc-bypass.xyz/v1/getUserInfo"

headers = {
    "Accept": "application/json",
    "X-MEXC-BYPASS-API-KEY": "YOUR_MEXC_BYPASS_API_KEY",
    "X-MEXC-WEB-KEY": "YOUR_MEXC_WEB_KEY",
    "X-MEXC-NETWORK": "MAINNET"
}

response = requests.get(url, headers=headers)
print(response.json())
```

### 🚀 Node.js

```javascript
import axios from "axios";

const url = "https://api.mexc-bypass.xyz/v1/createFuturesOrder";

const headers = {
  "Accept": "application/json",
  "X-MEXC-BYPASS-API-KEY": "YOUR_MEXC_BYPASS_API_KEY",
  "X-MEXC-WEB-KEY": "YOUR_MEXC_WEB_KEY",
  "X-MEXC-NETWORK": "TESTNET"
};

const data = {
  symbol: "BTC_USDT",
  type: 5,
  side: 1,
  vol: 1,
  leverage: 20
};

try {
  const response = await axios.post(url, data, { headers });
  console.log(response.data);
} catch (error) {
  console.error(error.response?.data || error.message);
}
```

---

## 🌍 Proxy Format
The API supports **HTTP**, **HTTPS** and **SOCKS5** proxies provided as a URL string.

##### 👀 Examples

```
X-MEXC-PROXY: http://127.0.0.1:8080
X-MEXC-PROXY: https://user:pass@proxy.example.com:3128
X-MEXC-PROXY: socks5://user:pass@127.0.0.1:9050
```

This allows you to route API requests through your own proxy.

-->

---

## 📚 Documentation & Guides

Expand your knowledge and integrate faster with these helpful articles:

| Title | Description |
|-------|-------------|
| 🆕 [Changelog](./CHANGELOG.md) | History of updates and changes. |
| 🔑 [Where to get MEXC token?](./mexc_token.md) | Find out how to get a token to work with MEXC. |
| 🔢 [Volume Calculation Guide](./volume_calculation.md) | Learn how to calculate the correct `vol` value when placing futures orders using MEXC Bypass API. |
| 🛡️ [What is Risk Control?](./risk_control_en.md) | Understand how MEXC’s internal risk control systems might affect your orders and how to handle it. |
| 🛑 [How to change TP / SL?](./change_tp_sl.md) | Current method of multiple changes of order TP/SL. |
| 🚧 [Error Codes](./error_codes.md) | Description of the error code that the endpoint may return. |

<!--

---

## 📥 Postman Collection

Want to test the API easily?

- **Download collection:** [postman_collection.json](./assets/postman_collection.json)
- **Importing into Postman:**
  1. Open Postman
  2. Click **Import**
  3. Choose **Link** or **Upload File**
  4. Paste the link above or select the file

-->