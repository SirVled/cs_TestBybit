using cs_TestBybit.Models;
using cs_TestBybit.Models.DTO;
using System;
using System.Collections.Generic;
using System.Text;

namespace cs_TestBybit.Providers
{
    public interface IProvider : IAsyncDisposable
    {
        IProvider SetOption();
        Task RunAsync(Func<ExecutionDto, Task> onExecution, CancellationToken ct);

    }
}
