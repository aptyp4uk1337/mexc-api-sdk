use mexc_bypass::MexcBypass;
use serde_json::json;
use std::sync::Arc;

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    println!("MEXC Client Demo");
    println!("================");

    let client = Arc::new(MexcBypass::new(
        "YOUR_MEXC_WEB_KEY",
        true,
        None
    )?);
    
    println!("Клиент создан, выполняем запросы...\n");
    
    let client1 = client.clone();
    let client2 = client.clone();
    let client3 = client.clone();
    
    let (assets, positions, ticker) = tokio::join!(
        async move { client1.get_futures_assets(json!({"currency": "USDT"})).await },
        async move { client2.get_futures_open_positions(json!({})).await },
        async move { client3.get_futures_tickers(json!({"symbol": "BTC_USDT"})).await },
    );
    
    println!("📊 Баланс USDT:");
    if let Some(balance) = assets["data"]["availableBalance"].as_f64() {
        println!("   Available: ${:.2}", balance);
    } else {
        println!("   {}", serde_json::to_string_pretty(&assets)?);
    }
    
    println!("\n📈 Открытые позиции:");
    if positions["data"].is_array() {
        println!("   {}", serde_json::to_string_pretty(&positions["data"])?);
    } else {
        println!("   {}", serde_json::to_string_pretty(&positions)?);
    }
    
    println!("\n💵 BTC Price:");
    if let Some(price) = ticker["data"]["lastPrice"].as_f64() {
        println!("   ${:.2}", price);
    } else {
        println!("   {}", serde_json::to_string_pretty(&ticker)?);
    }
    
    println!("\n📝 Создание ордера...");
    let order = client.create_futures_order(json!({
        "symbol": "BTC_USDT",
        "side": 1,
        "type": 5,
        "open_type": 1,
        "vol": 1,
        "leverage": 10
    })).await;
    
    println!("Результат: {}", serde_json::to_string_pretty(&order)?);
    
    Ok(())
}