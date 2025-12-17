using cs_TestBybit.Models;
using cs_TestBybit.Models.DTO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static System.Net.WebRequestMethods;

namespace cs_TestBybit.Providers.Rest
{
    public class BybitRestService
    {
        public BybitRestService(ExchangeOption option, HashSet<string> processedExecIds) 
        {
            _options = option;
            _processedExecIds = processedExecIds;
        }
        private ExchangeOption _options;

        private readonly HashSet<string> _processedExecIds;
        private readonly HttpClient _http = new();

        /// <summary>
        /// Можно было использовать api для получения всех монет, но для простоты взял только 2 пары
        /// </summary>
        private readonly string[] SYMBOL_ORDERS = new string[]
        {
            "BTCUSDT",
            "ETHUSDT"
        };

        /// <summary>
        /// REST RECOVERY
        /// </summary>
        /// <param name="onExecution"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task<long> RecoverMissedExecutions(
           Func<ExecutionDto, Task> onExecution,      
           long lastExecTime,
           CancellationToken ct)
        {
            if(lastExecTime == 0) lastExecTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long lastExecutionTime = lastExecTime;
            foreach (var symbol in SYMBOL_ORDERS) {
                Console.WriteLine($"[WS] Starting REST recovery for: {symbol}");

                var endpoint = "/v5/execution/list";
                //Только фьючерсы
                var query = $"category=linear&symbol={symbol}&startTime={lastExecTime}";
                var url = $"{_options.BaseRestUrl}{endpoint}?{query}";

                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var recvWindow = "25000";
                var payload = ts + _options.ApiKey + recvWindow + query;
                var signature = Sign(payload, _options.ApiSecret);

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-BAPI-API-KEY", _options.ApiKey);
                req.Headers.Add("X-BAPI-TIMESTAMP", ts.ToString());
                req.Headers.Add("X-BAPI-RECV-WINDOW", recvWindow);
                req.Headers.Add("X-BAPI-SIGN", signature);

                using var resp = await _http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();

                var content = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(content);

                if (!doc.RootElement.TryGetProperty("result", out var result))
                {
                    Console.WriteLine($"Not found result in symbol: {symbol}");
                    continue;
                }

                foreach (var item in result.GetProperty("list").EnumerateArray())
                {
                    var exec = GetExecInfo(item);

                    //Провиряем дубликаты и выводим уникальные значения
                    if (_processedExecIds.Add(exec.ExecId))
                    {
                        lastExecutionTime = exec.ExecTime;
                        await onExecution(exec);
                    }
                }
                Console.WriteLine("[WS] REST recovery done");
            }

            return lastExecutionTime;
        }

        private static string Sign(string payload, string secret)
        {
            using var hmac = new HMACSHA256(
                Encoding.UTF8.GetBytes(secret)
            );

            return Convert
                .ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)))
                .ToLowerInvariant();
        }

        private static ExecutionDto GetExecInfo(JsonElement item)
        {
            var id = item.GetProperty("execId").GetString()!;
            var symbol = item.GetProperty("symbol").GetString()!;
            var execTime = long.Parse(item.GetProperty("execTime").GetString()!, CultureInfo.InvariantCulture);
            var price = decimal.Parse(item.GetProperty("execPrice").GetString()!, CultureInfo.InvariantCulture);
            var qty = decimal.Parse(item.GetProperty("execQty").GetString()!, CultureInfo.InvariantCulture);
            var side = item.GetProperty("side").GetString()!;

            return new ExecutionDto(
                ExecId: id,
                Symbol: symbol,
                Price: price,
                Qty: qty,
                ExecTime: execTime,
                Side: side
            );
        }
    }
}
