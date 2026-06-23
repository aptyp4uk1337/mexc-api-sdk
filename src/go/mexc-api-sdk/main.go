package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"

	"mexc-api-sdk/mexc"
)

func main() {
	ctx := context.Background()

	client, err := mexcbypass.New(mexcbypass.Config{
		APIKey:    "YOUR_MEXC_WEB_KEY",
		IsTestnet: true,
		ProxyURL:  "",
	})

	if err != nil {
		log.Fatalf("Failed to create client: %v", err)
	}

	results := client.Batch(ctx, map[string]mexcbypass.BatchFunc{
		"assets": func() mexcbypass.Response {
			return client.GetFuturesAssets(ctx, mexcbypass.Params{"currency": "USDT"})
		},
		"positions": func() mexcbypass.Response {
			return client.GetFuturesOpenPositions(ctx, nil)
		},
		"ticker_btc": func() mexcbypass.Response {
			return client.GetFuturesTickers(ctx, mexcbypass.Params{"symbol": "BTC_USDT"})
		},
	})

	if assets, ok := results["assets"]; ok && assets.Success() {
		if data, ok := assets["data"].(map[string]any); ok {
			fmt.Printf("USDT Balance: %v\n", data["availableBalance"])
		}
	}

	if positions, ok := results["positions"]; ok && positions.Success() {
		positionsJSON, _ := json.MarshalIndent(positions["data"], "", "  ")
		fmt.Printf("Open positions: %s\n", positionsJSON)
	}

	if ticker, ok := results["ticker_btc"]; ok && ticker.Success() {
		if data, ok := ticker["data"].(map[string]any); ok {
			fmt.Printf("BTC Price: %v\n", data["lastPrice"])
		}
	}

	order := client.CreateFuturesOrder(ctx, mexcbypass.Params{
		"symbol":    "BTC_USDT",
		"side":      1,
		"type":      5,
		"open_type": 1,
		"vol":       1,
		"leverage":  10,
	})

	orderJSON, _ := json.MarshalIndent(order, "", "  ")
	fmt.Printf("Order created: %s\n", orderJSON)
}