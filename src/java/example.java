import java.util.Map;

/**
 * Compile and run:
 * javac --enable-preview --source 24 MexcBypass.java Example.java
 * java --enable-preview Example
 */
public class Example {
    private static final String API_KEY = "YOUR_MEXC_WEB_KEY";
    
    @SuppressWarnings("unchecked")
    public static void main(String[] args) {
        System.out.println("Starting MEXC API test...\n");

        MexcBypass client = new MexcBypass(API_KEY, true, null);
        
        var results = client.batch(Map.of(
            "assets",      () -> client.getFuturesAssets(Map.of("currency", "USDT")),
            "positions",   () -> client.getFuturesOpenPositions(Map.of()),
            "ticker_btc",  () -> client.getFuturesTickers(Map.of("symbol", "BTC_USDT"))
        ));

        Object balance = "N/A";
        if (results.containsKey("assets")) {
            Map<String, Object> assets = results.get("assets");
            if (assets.containsKey("data") && assets.get("data") instanceof Map) {
                Map<String, Object> data = (Map<String, Object>) assets.get("data");
                balance = data.getOrDefault("availableBalance", "N/A");
            }
        }
        System.out.println("USDT Balance: " + balance);
        
        Object positions = "[]";
        if (results.containsKey("positions")) {
            Map<String, Object> pos = results.get("positions");
            positions = pos.getOrDefault("data", "[]");
        }
        System.out.println("Open positions: " + positions);
        
        Object btcPrice = "N/A";
        if (results.containsKey("ticker_btc")) {
            Map<String, Object> ticker = results.get("ticker_btc");
            if (ticker.containsKey("data") && ticker.get("data") instanceof Map) {
                Map<String, Object> data = (Map<String, Object>) ticker.get("data");
                btcPrice = data.getOrDefault("lastPrice", "N/A");
            }
        }
        System.out.println("BTC Price: " + btcPrice);
        System.out.println();

        System.out.println("Creating order...");

        var order = client.createFuturesOrder(Map.of(
            "symbol",    "BTC_USDT",
            "side",      1,
            "type",      5,
            "open_type", 1,
            "vol",       1,
            "leverage",  10
        ));

        System.out.println("Order created: " + order);
    }
}