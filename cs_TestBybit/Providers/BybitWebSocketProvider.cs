using cs_TestBybit.Models;
using cs_TestBybit.Models.DTO;
using cs_TestBybit.Providers.Rest;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static System.Net.WebRequestMethods;


namespace cs_TestBybit.Providers
{
    internal class BybitWebSocketProvider() : IProvider
    {
        private ExchangeOption _options;
        private ClientWebSocket _ws = null!;

        private const string API_KEY = "Bybit";
        private readonly HashSet<string> _processedExecIds = new();


        private long _lastExecTime = 0; // для REST recovery
        public IProvider SetOption()
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .Build();

            var apiKey = configuration[$"{API_KEY}:ApiKey"];
            var apiSecret = configuration[$"{API_KEY}:ApiSecret"];
            var baseUrl = configuration[$"{API_KEY}:BaseUrl"];
            var restUrl = configuration[$"{API_KEY}:BaseRestUrl"];
            if (apiKey == null || apiSecret == null || baseUrl == null) throw new ArgumentNullException("Not found config");

            _options = new ExchangeOption(
                ApiKey: apiKey!,
                ApiSecret: apiSecret!,
                PrivateWsUrl: baseUrl!,
                BaseRestUrl: restUrl!
            );

            return this;
        }
        public async Task RunAsync(
            Func<ExecutionDto, Task> onExecution,
            CancellationToken ct)
        {
            var attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    attempt++;
                    Console.WriteLine($"[WS] Connecting (attempt {attempt})");

                    await ConnectAsync(ct);

                    if (attempt > 1)
                    {
                        // REST recovery пропущенных событий
                        _lastExecTime = await new BybitRestService(_options, _processedExecIds).RecoverMissedExecutions(onExecution, _lastExecTime, ct);
                    }

                    await ReceiveLoopAsync(onExecution, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WS] Error: {ex.Message}");
                }

                await SafeReconnectCleanupAsync();

                var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * attempt));
                Console.WriteLine($"[WS] Reconnect in {delay.TotalSeconds}s");

                await Task.Delay(delay, ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await SafeReconnectCleanupAsync();
        }

        #region Private Methods 

        private const string SubscribeMessage =
            @"{
                ""op"": ""subscribe"",
                ""args"": [""execution""]
            }";

        private async Task ConnectAsync(CancellationToken ct)
        {
            _ws = new ClientWebSocket();

            await _ws.ConnectAsync(
                new Uri(_options.PrivateWsUrl),
                ct
            );

            await SendAsync(
                BuildAuthMessage(_options.ApiKey, _options.ApiSecret),
                ct
            );

            await SendAsync(SubscribeMessage, ct);
        }

        private async Task SafeReconnectCleanupAsync()
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await _ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Reconnect",
                        CancellationToken.None
                    );
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                _ws.Dispose();
            }
        }

        /// <summary>
        /// RECEIVE LOOP
        /// </summary>
        /// <param name="onExecution"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        /// <exception cref="WebSocketException"></exception>

        private async Task ReceiveLoopAsync(
            Func<ExecutionDto, Task> onExecution,
            CancellationToken ct)
        {
            var buffer = new byte[8192];

            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("WS closed by server");

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("op", out var op))
                {
                    if (op.GetString() == "auth")
                    {
                        Console.WriteLine("[WS] Auth successful");
                        continue;
                    }
                    continue;
                }

                foreach (var execution in ParseExecutions(json))
                {
                    if (_processedExecIds.Add(execution.ExecId))
                    {
                        _lastExecTime = execution.ExecTime;
                        await onExecution(execution);
                    }
                }
            }
        }
       

        private async Task SendAsync(string message, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(message);

            await _ws.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                true,
                ct
            );
        }

        /// <summary>
        /// Auth
        /// </summary>
        /// <param name="apiKey"></param>
        /// <param name="secret"></param>
        /// <returns></returns>

        private static string BuildAuthMessage(string apiKey, string secret)
        {
            var expires = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000;
            var payload = $"GET/realtime{expires}";
            var signature = Sign(payload, secret);

            return JsonSerializer.Serialize(new
            {
                op = "auth",
                args = new object[]
                {
                apiKey,
                expires,
                signature
                }
            });
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

       /// <summary>
       /// Parsing
       /// </summary>
       /// <param name="json"></param>
       /// <returns></returns>

        private static IEnumerable<ExecutionDto> ParseExecutions(string json)
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("topic", out var topic))
                yield break;

            if (topic.GetString() != "execution")
                yield break;
            
            foreach (var item in doc.RootElement
                         .GetProperty("data")
                         .EnumerateArray())
            {
               yield return GetExecInfo(item);
            }
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

        #endregion
    }
}
