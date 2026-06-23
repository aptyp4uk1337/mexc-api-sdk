using System;
using System.Collections.Generic;
using MexcApi;

namespace MexcApiTest
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== MEXC API Test ===");

            var client = new MexcBypass(
                apiKey: "YOUR_MEXC_WEB_KEY",
                isTestnet: true,
                proxyUrl: null
            );

            Console.WriteLine("\n--- Batch Requests ---");
            var batchResults = client.Batch(new Dictionary<string, Func<Dictionary<string, object?>>>
            {
                ["assets"] = () => client.GetFuturesAssets(new Dictionary<string, object?> { ["currency"] = "USDT" }),
                ["positions"] = () => client.GetFuturesOpenPositions(),
                ["ticker_btc"] = () => client.GetFuturesTickers(new Dictionary<string, object?> { ["symbol"] = "BTC_USDT" })
            });

            var assetsData = GetDict(batchResults["assets"], "data");
            var tickerData = GetDict(batchResults["ticker_btc"], "data");
            
            Console.WriteLine($"USDT Balance: {GetValue(assetsData, "availableBalance")}");
            Console.WriteLine($"Open positions: {SerializeResults(GetList(batchResults["positions"], "data"))}");
            Console.WriteLine($"BTC Price: {GetValue(tickerData, "lastPrice")}");

            Console.WriteLine("\n--- Create Order ---");
            var order = client.CreateFuturesOrder(new Dictionary<string, object?>
            {
                ["symbol"] = "BTC_USDT",
                ["side"] = 1,
                ["type"] = 5,
                ["open_type"] = 1,
                ["vol"] = 1,
                ["leverage"] = 10,
                ["price"] = 50000
            });

            Console.WriteLine($"Order created: {SerializeResults(order)}");

            Console.WriteLine("\n--- Contract Info ---");
            var contracts = client.GetFuturesContracts(new Dictionary<string, object?> { ["symbol"] = "BTC_USDT" });
            var contractData = GetList(contracts, "data");
            if (contractData != null && contractData.Count > 0)
            {
                var firstContract = contractData[0] as Dictionary<string, object?>;
                Console.WriteLine($"Contract: {GetValue(firstContract, "displayName")}");
                Console.WriteLine($"Min Leverage: {GetValue(firstContract, "minLeverage")}");
                Console.WriteLine($"Max Leverage: {GetValue(firstContract, "maxLeverage")}");
                Console.WriteLine($"Contract Size: {GetValue(firstContract, "contractSize")}");
            }

            Console.WriteLine("\n--- Calculations ---");
            
            var volumeCalc = client.CalculateFuturesVolume(new Dictionary<string, object?>
            {
                ["symbol"] = "BTC_USDT",
                ["amount"] = 100,
                ["leverage"] = 10
            });
            
            var volumeData = GetDict(volumeCalc, "data");
            Console.WriteLine($"Calculated Volume: {GetValue(volumeData, "volume")}");
            Console.WriteLine($"Required Margin: {GetValue(volumeData, "usdt_value")}");

            Console.WriteLine("\n--- Multi-Account Batch ---");
            
            var accounts = new Dictionary<string, Dictionary<string, object?>>
            {
                ["account1"] = new Dictionary<string, object?>
                {
                    ["apiKey"] = "ACCOUNT_1_KEY",
                    ["isTestnet"] = true
                },
                ["account2"] = new Dictionary<string, object?>
                {
                    ["apiKey"] = "ACCOUNT_2_KEY",
                    ["isTestnet"] = true
                }
            };

            var multiResults = MexcBypass.BatchAccounts(accounts, (acc) =>
            {
                return new Dictionary<string, Func<Dictionary<string, object?>>>
                {
                    ["balance"] = () => acc.GetFuturesAssets(new Dictionary<string, object?> { ["currency"] = "USDT" }),
                    ["ping"] = () => acc.Ping()
                };
            });

            foreach (var accResult in multiResults)
            {
                Console.WriteLine($"Account: {accResult.Key}");
                var balance = GetDict(accResult.Value["balance"], "data");
                Console.WriteLine($"  Balance: {GetValue(balance, "availableBalance")}");
            }

            client.Dispose();
            Console.WriteLine("\n=== Test Complete ===");
        }

        static Dictionary<string, object?>? GetDict(Dictionary<string, object?> response, string key)
        {
            if (response.TryGetValue(key, out var val) && val is Dictionary<string, object?> dict)
                return dict;
            return null;
        }

        static List<object?>? GetList(Dictionary<string, object?> response, string key)
        {
            if (response.TryGetValue(key, out var val) && val is List<object?> list)
                return list;
            return null;
        }

        static object? GetValue(Dictionary<string, object?>? dict, string key)
        {
            if (dict != null && dict.TryGetValue(key, out var val))
                return val;
            return "N/A";
        }

        static string SerializeResults(Dictionary<string, object?> results)
        {
            return System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
        }

        static string SerializeResults(List<object?>? results)
        {
            if (results == null) return "[]";
            return System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
        }
    }
}