<div align="center">
   <img src="/assets/mexc-logo.png" height="150" width="150">
</div>

<div align="center">
  <a href="/packages/php"><img src="https://img.shields.io/badge/php-%23777BB4.svg?&logo=php&logoColor=white" alt="PHP"></a>
  <a href="/packages/python"><img src="https://img.shields.io/badge/Python-3776AB?logo=python&logoColor=white" alt="Python"></a>
  <a href="/packages/nodejs"><img src="https://img.shields.io/badge/Node.js-6DA55F?logo=node.js&logoColor=white" alt="Node.js"></a>
</div>

# 🔷 Unofficial MEXC API SDK

This is an unofficial *MEXC API SDK* with support for *futures* and *spot* trading, as well as many other methods, providing full trading and account access, even if some routes are marked as "[Under maintenance](https://mexcdevelop.github.io/apidocs/contract_v1_en/#order-under-maintenance)".

<br>

> [!NOTE]
> The source code of the library is not distributed openly. You can get access by contacting me on Telegram: [@aptyp4uk1337_bot](https://t.me/aptyp4uk1337_bot?text=%F0%9F%91%8B%20Hi%2C%20I%20am%20writing%20regarding%20the%20acquisition%20of%20MEXC%20Futures%20API.)

<br>

### 🔴 Live Demo

* You can test the opening of a position yourself: https://mexc-bypass.xyz/demo
* 6h free trial: [@mexc_api_robot](https://t.me/mexc_api_robot?start=trial) → `🆓 Get Free Trial`

<div align="center">
  <img src="/assets/preview.gif" title="Telegram">
</div>

> **Demo file:** [./demo/app.js](/main/demo/app.js)

---

## 🎖 Features

- ⚡ Blazing fast _(200 - 400 ms)_
- 🔐 No third-party requests
- 🌐 Works on mainnet & testnet
- 🔀 Support for batch requests
- 🥷🏻 Multi-accounting and proxy support _(HTTP/HTTPS/SOCKS5)_
- 🫂 Ability to work with multiple accounts at the same time
- ⚙️ Compatible with any programming language
- ⌨️ Simple PHP, Python, Go, Rust, Java & Node.js library
- 🆓 Free updates & support included
- 🔔 TradingView Alerts Integration

---

### ❓ FAQ

> Does it fully support placing, cancelling, and tracking all types of futures orders?
- Yes, including market, limit, stop-limit, and trigger orders.

> How many orders can be sent per second, per minute, per day?
- See the [results of the Rate Limit Test](#-rate-limit-test) for 200 requests.

> Can the bypass API fetch account info, open positions, and adjust leverage/margin?
- Yes. For more info, look at [available methods](#-available-methods) section.

> Is the library provided as open source or as compiled/obfuscated code?
- Currently, everything is open-sourced, nothing is obfuscated.

> Can the library be used with multiple accounts, or is the authentication tied to a single one?
- No limitation on number of accounts.

> Does it use anything third-party to make those requests?
- In the test version and subscription mode, all requests go through our server. When purchasing the source code, all requests go directly.

> Will I get a risk control ban for using the library?
- In my experience - no. For more information on risk control, see here 🛡️ [Risk Control on MEXC](/docs/risk_control_en.md).


---

### ⏱️ Rate Limit Test

<div align="center">
  <img src="/assets/rate-limit-test.png" title="Telegram">
</div>

> **Demo file:** [./demo/rate_limit_test.js](/demo/rate_limit_test.js)

---

### 🚀 API initialization

```JS
import { MexcClient } from './MexcClient.js';

const client = new MexcClient({
  apiKey: 'YOUR_API_KEY',
  isTestnet: false,
  proxy: 'socks5://user:pass@127.0.0.1:1080', // socks5://user:pass@host:port || http://user:pass@host:port
});
```

### 💥 Create Order Example

```JS
import { MexcClient } from './MexcClient.js';

const client = new MexcClient({ apiKey: 'YOUR_API_KEY', isTestnet: true });

const order = await client.createOrder({
  symbol: 'BTC_USDT',
  type: 5,
  side: 1,
  openType: 2,
  vol: 15,
  leverage: 25
});
```

---


### 📖 Available Methods

The library supports **50+ endpoints** including:

- Placing, modifying and canceling orders on spot and futures
- Accessing wallet and asset data
- Managing open positions, leverage and margin
- Retrieving contract info and price feeds

📚 **[Full method documentation](/docs#-available-methods)** is available in `/docs/methods/`

---

### ▶ Live preview: placing and cancelling a futures order

<video src="https://github.com/user-attachments/assets/d51a6a12-a596-440e-bc3c-147ef8aad5b0" align="center">
  👀 <a href="https://www.youtube.com/shorts/wMQ-Iq3xHHQ">Watch Live Preview</a>
</video>

### 💌 Contact me

<a href="https://t.me/aptyp4uk1337_bot?text=%F0%9F%91%8B%20Hi%2C%20I%20am%20writing%20regarding%20the%20acquisition%20of%20MEXC%20API%20SDK."><img src="https://img.shields.io/badge/Telegram-2CA5E0?logo=telegram&logoColor=white" title="Telegram"></a>
