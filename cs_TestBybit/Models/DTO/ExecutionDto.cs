using System;
using System.Collections.Generic;
using System.Text;

namespace cs_TestBybit.Models.DTO
{
    public record ExecutionDto(
        string ExecId,
        string Symbol,
        decimal Price,
        decimal Qty,
        long ExecTime,
        string Side
);
}
