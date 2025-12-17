using cs_TestBybit.Providers;
using System;
using System.Collections.Generic;
using System.Text;

namespace cs_TestBybit.Services
{
    public class ExchangeAssetClient
    {
        public async Task RunAsync<T>(CancellationTokenSource ct) where T : IProvider
        {
            await using var provider = GetProvider<T>();
            if (provider == null) return;

            Console.WriteLine("Start listen");

            await provider.RunAsync(
                execution =>
                {
                    Console.WriteLine(
                        $"[{DateTimeOffset.FromUnixTimeMilliseconds(execution.ExecTime)}] Execution ID: {execution.ExecId}, Symbol: {execution.Symbol}, Side: {execution.Side},  Price: {execution.Price}, Qty: {execution.Qty}, Time: {DateTimeOffset.FromUnixTimeMilliseconds(execution.ExecTime)}"
                    );
                    return Task.CompletedTask;
                },
            ct.Token);
        }


        ///Вместо DI из-за консольного приложения
        private IProvider GetProvider<T>() where T : IProvider
        {
            if(typeof(T) == typeof(BybitWebSocketProvider))
            {
                return new BybitWebSocketProvider().SetOption();
            }

            throw new ArgumentException("Not found Provider");
        }
    }
}
