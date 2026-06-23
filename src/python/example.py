import asyncio
from MexcBypass import MexcBypass

MEXC_WEB_KEY = "YOUR_MEXC_WEB_KEY"

async def main():
    async with MexcBypass(api_key=MEXC_WEB_KEY, is_testnet=True, proxy_url=None) as client:
        results = await client.batch({
            "assets": lambda: client.get_futures_assets({"currency": "USDT"}),
            "positions": client.get_futures_open_positions,
            "ticker_btc": lambda: client.get_futures_tickers({"symbol": "BTC_USDT"}),
        })
        
        print("USDT Balance:", results["assets"].get("data", {}).get("availableBalance"))
        print("Open positions:", results["positions"].get("data", []))
        print("BTC Price:", results["ticker_btc"].get("data", {}).get("lastPrice"))
        
        order = await client.create_futures_order({
            "symbol": "BTC_USDT",
            "side": 1,
            "type": 5,
            "open_type": 1,
            "vol": 1,
            "leverage": 10
        })

        print("Order created:", order)

if __name__ == "__main__":
    asyncio.run(main())
