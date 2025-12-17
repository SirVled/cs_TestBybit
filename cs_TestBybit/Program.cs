using cs_TestBybit.Models;
using cs_TestBybit.Providers;
using cs_TestBybit.Services;

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await new ExchangeAssetClient().RunAsync<BybitWebSocketProvider>(cts);